using System.Text;
using System.Text.Json;

internal enum JournalOperationKind
{
    Review,
    Refresh,
    Cleanup,
}

internal enum JournalOperationStatus
{
    Running,
    Completed,
    CompletedWithErrors,
    Failed,
    Canceled,
}

internal enum JournalStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled,
}

internal sealed record JournalStep(
    string Key,
    string Title,
    string? Detail,
    JournalStepStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

internal sealed record JournalOperation(
    string Id,
    string? CorrelationKey,
    JournalOperationKind Kind,
    string Title,
    string? Subtitle,
    string? Url,
    JournalOperationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<JournalStep> Steps);

internal sealed class DashboardActivityJournal
{
    private const int CurrentVersion = 1;
    private const int MaximumEntries = 30;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();
    private readonly string _path;
    private List<JournalOperation> _entries;

    public DashboardActivityJournal(string settingsPath, CodexReviewSettings settings)
    {
        var settingsDirectory = Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? AppContext.BaseDirectory;
        var dataDirectory = ReviewPaths.ResolveDirectory(settingsDirectory, settings.DataDirectory);
        _path = Path.Combine(dataDirectory, "journal.json");
        _entries = Load(_path);
        MarkInterruptedOperations();
    }

    internal DashboardActivityJournal(string path)
    {
        _path = Path.GetFullPath(path);
        _entries = Load(_path);
        MarkInterruptedOperations();
    }

    public IReadOnlyList<JournalOperation> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public string Start(
        JournalOperationKind kind,
        string title,
        string? subtitle,
        IReadOnlyList<(string Key, string Title)> steps,
        string firstStepKey,
        string? firstStepDetail = null,
        string? correlationKey = null,
        string? url = null,
        DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var id = $"{kind.ToString().ToLowerInvariant()}:{timestamp.UtcTicks}:{Guid.NewGuid():N}";
        var operation = new JournalOperation(
            id,
            correlationKey,
            kind,
            title,
            subtitle,
            url,
            JournalOperationStatus.Running,
            timestamp,
            CompletedAt: null,
            steps.Select(step => new JournalStep(
                step.Key,
                step.Title,
                string.Equals(step.Key, firstStepKey, StringComparison.Ordinal) ? firstStepDetail : null,
                string.Equals(step.Key, firstStepKey, StringComparison.Ordinal)
                    ? JournalStepStatus.Running
                    : JournalStepStatus.Pending,
                string.Equals(step.Key, firstStepKey, StringComparison.Ordinal) ? timestamp : null,
                CompletedAt: null)).ToArray());
        lock (_gate)
        {
            _entries.Insert(0, operation);
            TrimAndSave();
        }

        return id;
    }

    public void UpdateStep(
        string? operationId,
        string stepKey,
        JournalStepStatus status,
        string? detail = null,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        var timestamp = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => string.Equals(entry.Id, operationId, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            var operation = _entries[index];
            var changed = false;
            var steps = operation.Steps.Select(step =>
            {
                if (!string.Equals(step.Key, stepKey, StringComparison.Ordinal))
                {
                    return step;
                }

                changed = true;
                return step with
                {
                    Detail = detail ?? step.Detail,
                    Status = status,
                    StartedAt = status == JournalStepStatus.Pending ? step.StartedAt : step.StartedAt ?? timestamp,
                    CompletedAt = IsTerminal(status) ? step.CompletedAt ?? timestamp : null,
                };
            }).ToArray();
            if (!changed)
            {
                return;
            }

            _entries[index] = operation with { Steps = steps };
            TrimAndSave();
        }
    }

    public void Finish(
        string? operationId,
        JournalOperationStatus status,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        var timestamp = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => string.Equals(entry.Id, operationId, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            var operation = _entries[index];
            var unfinishedStatus = status == JournalOperationStatus.Canceled
                ? JournalStepStatus.Canceled
                : JournalStepStatus.Failed;
            var steps = status is JournalOperationStatus.Failed or JournalOperationStatus.Canceled
                ? operation.Steps.Select(step => IsTerminal(step.Status)
                    ? step
                    : step with
                    {
                        Status = unfinishedStatus,
                        StartedAt = step.StartedAt ?? timestamp,
                        CompletedAt = timestamp,
                        Detail = step.Detail ?? (status == JournalOperationStatus.Canceled ? "Canceled" : "Not completed"),
                    }).ToArray()
                : operation.Steps.ToArray();
            _entries[index] = operation with
            {
                Status = status,
                CompletedAt = timestamp,
                Steps = steps,
            };
            TrimAndSave();
        }
    }

    public void ObserveReview(ReviewPipelineProgress? progress)
    {
        if (progress is null)
        {
            return;
        }

        var operation = BuildReviewOperation(progress);
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => string.Equals(entry.Id, operation.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _entries[index] = operation;
            }
            else
            {
                _entries.Insert(0, operation);
            }

            _entries = _entries
                .OrderByDescending(entry => entry.StartedAt)
                .Take(MaximumEntries)
                .ToList();
            Save();
        }
    }

    private static JournalOperation BuildReviewOperation(ReviewPipelineProgress progress)
    {
        var steps = new List<JournalStep>
        {
            new(
                "queued",
                "Queued",
                progress.IsManual ? "Manually requested" : "Selected by the review watcher",
                JournalStepStatus.Completed,
                progress.StartedAt,
                progress.StartedAt),
        };
        var workflow = progress.WorkflowSteps ?? [];
        AddWorkflowStep(ReviewWorkflowPhase.Eligibility);
        AddWorkflowStep(ReviewWorkflowPhase.Workspace);
        foreach (var stage in progress.Stages.OrderBy(stage => stage.Index))
        {
            var model = string.IsNullOrWhiteSpace(stage.Effort)
                ? stage.Model
                : $"{stage.Model} / {stage.Effort}";
            var result = stage.Status == ReviewAgentStageStatus.Completed
                ? $"{stage.ReturnedFindings} returned, {stage.AcceptedFindings} added"
                : stage.Status is ReviewAgentStageStatus.Failed or ReviewAgentStageStatus.Canceled
                    ? stage.Error
                    : stage.Status == ReviewAgentStageStatus.Running
                        ? "Inspecting and testing the worktree"
                        : "Waiting";
            steps.Add(new JournalStep(
                $"agent:{stage.AgentName}",
                stage.DisplayName,
                string.IsNullOrWhiteSpace(result) ? model : $"{model} | {result}",
                Map(stage.Status),
                stage.StartedAt,
                stage.CompletedAt));
        }

        AddWorkflowStep(ReviewWorkflowPhase.Activity);
        AddWorkflowStep(ReviewWorkflowPhase.Publication);

        return new JournalOperation(
            $"review:{progress.PullRequestKey}:{progress.StartedAt.UtcTicks}",
            progress.PullRequestKey,
            JournalOperationKind.Review,
            $"Review {progress.Repository} #{progress.PullRequestNumber}",
            progress.PullRequestTitle,
            progress.PullRequestUrl,
            Map(progress.Status),
            progress.StartedAt,
            progress.CompletedAt,
            steps);

        void AddWorkflowStep(ReviewWorkflowPhase phase)
        {
            var step = workflow.FirstOrDefault(candidate => candidate.Phase == phase);
            if (step is null)
            {
                return;
            }

            steps.Add(new JournalStep(
                $"workflow:{phase.ToString().ToLowerInvariant()}",
                step.Title,
                step.Detail,
                Map(step.Status),
                step.StartedAt,
                step.CompletedAt));
        }
    }

    private void MarkInterruptedOperations()
    {
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        for (var index = 0; index < _entries.Count; index++)
        {
            var operation = _entries[index];
            if (operation.Status != JournalOperationStatus.Running)
            {
                continue;
            }

            changed = true;
            _entries[index] = operation with
            {
                Status = JournalOperationStatus.Failed,
                CompletedAt = now,
                Steps = operation.Steps.Select(step => IsTerminal(step.Status)
                    ? step
                    : step with
                    {
                        Status = step.Status == JournalStepStatus.Running
                            ? JournalStepStatus.Failed
                            : JournalStepStatus.Canceled,
                        StartedAt = step.StartedAt ?? now,
                        CompletedAt = now,
                        Detail = step.Detail ?? "Application stopped before completion",
                    }).ToArray(),
            };
        }

        if (changed)
        {
            Save();
        }
    }

    private void TrimAndSave()
    {
        if (_entries.Count > MaximumEntries)
        {
            _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
        }

        Save();
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = _path + ".tmp";
            var state = new JournalState(CurrentVersion, _entries);
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonDefaults.Options), Utf8WithoutBom);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static List<JournalOperation> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var state = JsonSerializer.Deserialize<JournalState>(File.ReadAllText(path), JsonDefaults.Options);
            return (state?.Entries ?? [])
                .OrderByDescending(entry => entry.StartedAt)
                .Take(MaximumEntries)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsTerminal(JournalStepStatus status) => status is
        JournalStepStatus.Completed or
        JournalStepStatus.Failed or
        JournalStepStatus.Canceled;

    private static JournalStepStatus Map(ReviewAgentStageStatus status) => status switch
    {
        ReviewAgentStageStatus.Running => JournalStepStatus.Running,
        ReviewAgentStageStatus.Completed => JournalStepStatus.Completed,
        ReviewAgentStageStatus.Failed => JournalStepStatus.Failed,
        ReviewAgentStageStatus.Canceled => JournalStepStatus.Canceled,
        _ => JournalStepStatus.Pending,
    };

    private static JournalOperationStatus Map(ReviewPipelineStatus status) => status switch
    {
        ReviewPipelineStatus.Completed => JournalOperationStatus.Completed,
        ReviewPipelineStatus.CompletedWithErrors => JournalOperationStatus.CompletedWithErrors,
        ReviewPipelineStatus.Failed => JournalOperationStatus.Failed,
        ReviewPipelineStatus.Canceled => JournalOperationStatus.Canceled,
        _ => JournalOperationStatus.Running,
    };

    private sealed record JournalState(int Version, List<JournalOperation> Entries);
}

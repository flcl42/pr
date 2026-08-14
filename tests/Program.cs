using System.Diagnostics;
using System.Text.Json;

if (args is ["--single-instance-holder", var scope, var readyPath, var lockDirectory])
{
    using var singleInstance = SingleInstanceLease.Acquire(scope, lockDirectory);
    File.WriteAllText(readyPath, Environment.ProcessId.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

var failures = new List<string>();
await RunAsync("new process replaces previous instance", TestSingleInstanceReplacementAsync);
await RunAsync("settings default disabled and round trip", TestSettingsRoundTripAsync);
await RunAsync("top PR settings cleanup", TestTopPullRequestCleanupAsync);
await RunAsync("review workspace path", TestReviewWorkspacePathAsync);
await RunAsync("review agent invocation", TestReviewAgentInvocationAsync);
await RunAsync("Codex schema uses BOM-free JSON", TestCodexSchemaEncodingAsync);
await RunAsync("review comment tone", TestReviewCommentToneAsync);
await RunAsync("review summary is neutral and specific", TestReviewSummaryAsync);
await RunAsync("review payload supports draft and auto-send", TestReviewPayloadDeliveryAsync);
await RunAsync("existing pending review is reused safely", TestPendingReviewReuseAsync);
await RunAsync("pending review count survives status transitions", TestPendingReviewWorkCountAsync);
await RunAsync("review diff anchors", TestReviewDiffAnchorsAsync);
await RunAsync("Claude structured review output", TestClaudeStructuredOutputAsync);
await RunAsync("Kimi structured review output", TestKimiStructuredOutputAsync);
await RunAsync("startup review scan lookback", TestStartupScanLookbackAsync);
await RunAsync("manual review enqueue persistence", TestManualReviewEnqueueAsync);
await RunAsync("manual review bypasses policy gates", TestManualReviewPolicyAsync);
await RunAsync("external contributors cannot be reviewed", TestExternalContributorSafetyAsync);
await RunAsync("activity excludes author and bots", TestActivityFilteringAsync);
await RunAsync("latest decisive review controls approvals", TestLatestApprovalStateAsync);
await RunAsync("inline repository context precedence", TestContextPrecedenceAsync);
await RunAsync("embedded repository context", TestEmbeddedContextAsync);
await RunAsync("Windows review agent command wrappers", TestReviewAgentCommandsAsync);
if (Environment.GetEnvironmentVariable("PR_LIVE_TEST_REPO") is { Length: > 0 })
{
    await RunAsync("live dashboard priority cache", TestLiveDashboardPriorityCacheAsync);
    await RunAsync("live weekly stats", TestLiveWeeklyStatsAsync);
    await RunAsync("live requested-PR scan", TestLiveRequestedPullRequestScanAsync);
    await RunAsync("live first-run watcher baseline", TestLiveWatcherBaselineAsync);
}

if (failures.Count == 0)
{
    Console.WriteLine("All tests passed.");
    return 0;
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

return 1;

async Task TestSingleInstanceReplacementAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-instance-tests-" + Guid.NewGuid().ToString("N"));
    var lockDirectory = Path.Combine(directory, "locks");
    var firstReady = Path.Combine(directory, "first.ready");
    var secondReady = Path.Combine(directory, "second.ready");
    var scope = Guid.NewGuid().ToString("N");
    Directory.CreateDirectory(directory);
    Process? first = null;
    Process? second = null;
    try
    {
        first = StartInstanceHolder(scope, firstReady, lockDirectory);
        await WaitUntilReadyAsync(first, firstReady);

        second = StartInstanceHolder(scope, secondReady, lockDirectory);
        await WaitUntilReadyAsync(second, secondReady);
        await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert(first.HasExited, "the previous instance was not terminated");
        Assert(!second.HasExited, "the replacement instance did not retain the lease");
    }
    finally
    {
        await StopAsync(second);
        await StopAsync(first);
        Directory.Delete(directory, recursive: true);
    }
}

Process StartInstanceHolder(string scope, string readyPath, string lockDirectory)
{
    var processPath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Could not locate the test process executable");
    var startInfo = new ProcessStartInfo
    {
        FileName = processPath,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
    {
        startInfo.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    }

    startInfo.ArgumentList.Add("--single-instance-holder");
    startInfo.ArgumentList.Add(scope);
    startInfo.ArgumentList.Add(readyPath);
    startInfo.ArgumentList.Add(lockDirectory);
    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start the single-instance test process");
}

static async Task WaitUntilReadyAsync(Process process, string readyPath)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (!File.Exists(readyPath))
    {
        if (process.HasExited)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"instance holder exited before acquiring its lease: {error}");
        }

        if (DateTimeOffset.UtcNow >= deadline)
        {
            throw new TimeoutException("instance holder did not acquire its lease");
        }

        await Task.Delay(25);
    }
}

static async Task StopAsync(Process? process)
{
    if (process is null)
    {
        return;
    }

    using (process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();
    }
}

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"FAIL {name}: {ex.Message}");
    }
}

Task TestSettingsRoundTripAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, ".pr.yml");
        var settings = AppSettings.Load(path);
        Equal(false, settings.CodexReview.Enabled, "Codex review must be disabled by default");
        Equal(false, settings.CodexReview.AutoSubmit, "review auto-send must be disabled by default");
        Equal(20d, settings.CodexReview.ReadyDelayMinutes, "default delay");
        Equal(4, settings.CodexReview.StartupScanDays, "default startup scan");
        Equal(ReviewAgent.Codex, settings.CodexReview.Agent, "default review agent");
        Equal<string?>(null, settings.CodexReview.Model, "default model override");
        Equal(CodexReviewSettings.DefaultCodexModel, settings.CodexReview.ResolvedModel, "resolved default Codex model");
        settings.SetCodexReviewEnabled(true);
        settings.SetCodexReviewAutoSubmit(true);
        settings.Save();
        Assert(
            File.ReadAllText(path).Contains(
                $"model: {CodexReviewSettings.DefaultCodexModel}",
                StringComparison.Ordinal),
            "saved settings did not pin the default Codex model");

        var loaded = AppSettings.Load(path);
        Equal(true, loaded.CodexReview.Enabled, "enabled value did not round trip");
        Equal(true, loaded.CodexReview.AutoSubmit, "auto-submit value did not round trip");
        Equal(CodexReviewSettings.DefaultCodexModel, loaded.CodexReview.Model, "saved default Codex model");
        Equal(1_000, loaded.CodexReview.MaxOpenPullRequests, "max open PR count");
        Equal(false, loaded.CodexReview.PostNoFindingsComment, "no-findings comments must stay disabled");
        Assert(loaded.CodexReview.IgnoredAuthorPatterns.Contains("codex"), "default bot patterns were lost");
        Assert(loaded.CodexReview.IgnoredAuthorPatterns.Contains("kimi"), "Kimi bot pattern was lost");

        File.WriteAllText(
            path,
            """
            codexReview:
              enabled: false
              readyDelayMinutes: 7.5
              startupScanDays: 3
              agent: claude
              model: opus
              autoSubmit: true
              command: claude
              workspaceDirectory: review-cache
              contexts:
                "Owner/Repo": |-
                  # Keep this heading
                  Rules:
                    - preserve nested indentation

                  enabled: true
              ignoredAuthorPatterns:
                - custom-review-bot
            """);
        var customized = AppSettings.Load(path);
        const string expectedContext = "# Keep this heading\nRules:\n  - preserve nested indentation\n\nenabled: true";
        Equal(false, customized.CodexReview.Enabled, "context content was parsed as settings");
        Equal(7.5d, customized.CodexReview.ReadyDelayMinutes, "custom delay");
        Equal(3, customized.CodexReview.StartupScanDays, "custom startup scan");
        Equal(ReviewAgent.Claude, customized.CodexReview.Agent, "custom review agent");
        Equal(true, customized.CodexReview.AutoSubmit, "custom auto-submit mode");
        Equal("opus", customized.CodexReview.Model, "custom review model");
        Equal<string?>(null, customized.CodexReview.Command, "built-in command should migrate to agent selection");
        Equal("review-cache", customized.CodexReview.WorkspaceDirectory, "custom workspace directory");
        Equal(expectedContext, customized.CodexReview.Contexts["owner/repo"], "inline context");
        SetEqual(["custom-review-bot"], customized.CodexReview.IgnoredAuthorPatterns, "custom ignored authors");

        customized.Save();
        var roundTripped = AppSettings.Load(path);
        Equal(expectedContext, roundTripped.CodexReview.Contexts["OWNER/REPO"], "round-tripped context");
        Equal(false, roundTripped.CodexReview.Enabled, "round-tripped enabled value");
        Equal(3, roundTripped.CodexReview.StartupScanDays, "round-tripped startup scan");
        Equal(ReviewAgent.Claude, roundTripped.CodexReview.Agent, "round-tripped review agent");
        Equal(true, roundTripped.CodexReview.AutoSubmit, "round-tripped auto-submit mode");
        Equal("opus", roundTripped.CodexReview.Model, "round-tripped review model");
        Equal("review-cache", roundTripped.CodexReview.WorkspaceDirectory, "round-tripped workspace directory");

        File.WriteAllText(path, "codexReview:\n  agent: kimi\n");
        var kimi = AppSettings.Load(path);
        Equal(ReviewAgent.Kimi, kimi.CodexReview.Agent, "Kimi review agent parsing");
        Equal<string?>(null, kimi.CodexReview.ResolvedModel, "Kimi inherited the Codex default model");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

Task TestTopPullRequestCleanupAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, ".pr.yml");
        var settings = AppSettings.Load(path);
        var repository = new RepositoryRef("owner", "repo");
        var closed = new TopPullRequest(repository, 41);
        var open = new TopPullRequest(repository, 42);
        Assert(settings.AddTopPullRequest(closed), "closed top PR was not added");
        Assert(settings.AddTopPullRequest(open), "open top PR was not added");
        settings.Save();

        var loaded = AppSettings.Load(path);
        Assert(loaded.RemoveTopPullRequests([closed]), "closed top PR was not removed");
        loaded.Save();

        var cleaned = AppSettings.Load(path);
        SetEqual([open.Key], cleaned.TopPullRequests.Select(pullRequest => pullRequest.Key), "persisted top PR cleanup");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

Task TestReviewAgentInvocationAsync()
{
    var settingsDirectory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    var repository = new RepositoryRef("owner", "repo");
    var pullRequest = new CodexPullRequest(
        repository,
        42,
        "https://github.com/owner/repo/pull/42",
        "Agent invocation",
        "OPEN",
        false,
        "0123456789abcdef",
        "main",
        "contributor",
        DateTimeOffset.UtcNow,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        "MEMBER");
    var paths = ReviewPaths.Create(settingsDirectory, pullRequest, CodexReviewSettings.Default);

    var defaultCodex = LocalReviewAgent.CreateInvocation(
        CodexReviewSettings.Default,
        paths,
        settingsDirectory);
    AssertArgumentPair(
        defaultCodex.Arguments,
        "--model",
        CodexReviewSettings.DefaultCodexModel,
        "default Codex model");

    var codexBuilder = CodexReviewSettings.Default.ToBuilder();
    codexBuilder.Model = "gpt-test";
    var codex = LocalReviewAgent.CreateInvocation(codexBuilder.Build(), paths, settingsDirectory);
    Equal("codex", codex.Command, "Codex executable");
    AssertArgumentPair(codex.Arguments, "--model", "gpt-test", "Codex model");
    AssertArgumentPair(codex.Arguments, "--sandbox", "read-only", "Codex sandbox");
    Assert(codex.Arguments.Contains("--output-schema"), "Codex output schema was omitted");

    var claudeBuilder = CodexReviewSettings.Default.ToBuilder();
    claudeBuilder.Agent = ReviewAgent.Claude;
    var defaultClaude = LocalReviewAgent.CreateInvocation(claudeBuilder.Build(), paths, settingsDirectory);
    Assert(!defaultClaude.Arguments.Contains("--model"), "Claude inherited the Codex default model");
    claudeBuilder.Model = "opus";
    claudeBuilder.Command = "codex";
    var claudeSettings = claudeBuilder.Build();
    var claude = LocalReviewAgent.CreateInvocation(claudeSettings, paths, settingsDirectory);
    Equal("claude", claude.Command, "Claude executable");
    AssertArgumentPair(claude.Arguments, "--model", "opus", "Claude model");
    AssertArgumentPair(claude.Arguments, "--permission-mode", "plan", "Claude permission mode");
    AssertArgumentPair(claude.Arguments, "--output-format", "json", "Claude output format");
    Assert(claude.Arguments.Contains("--json-schema"), "Claude JSON schema was omitted");
    Assert(claude.Arguments.Contains("--no-session-persistence"), "Claude session persistence was not disabled");
    Assert(claude.Arguments.Contains("--safe-mode"), "Claude user configuration was not disabled");
    Assert(!claude.Arguments.Contains("exec"), "Claude received Codex arguments");

    var kimiBuilder = CodexReviewSettings.Default.ToBuilder();
    kimiBuilder.Agent = ReviewAgent.Kimi;
    kimiBuilder.Command = "codex";
    var kimi = LocalReviewAgent.CreateInvocation(kimiBuilder.Build(), paths, settingsDirectory);
    Equal("kimi", kimi.Command, "Kimi executable");
    Assert(!kimi.PromptInStandardInput, "Kimi prompt was sent through stdin");
    Assert(!kimi.Arguments.Contains("--model"), "Kimi inherited the Codex default model");
    AssertArgumentPair(kimi.Arguments, "--output-format", "stream-json", "Kimi output format");
    AssertArgumentPair(kimi.Arguments, "--agent-file", paths.KimiAgentPath, "Kimi agent profile");
    AssertArgumentPair(kimi.Arguments, "--skills-dir", paths.KimiSkillsDirectory, "Kimi skills isolation");
    AssertArgumentPair(kimi.Arguments, "--add-dir", paths.RunsDirectory, "Kimi run artifacts");
    Equal(
        "1",
        kimi.EnvironmentVariables?["KIMI_CODE_EXPERIMENTAL_FLAG"],
        "Kimi v2 engine flag");

    kimiBuilder.Command = "kimi";
    Equal<string?>(null, kimiBuilder.Build().Command, "built-in Kimi command was persisted as an override");

    claudeBuilder.Command = OperatingSystem.IsWindows() ? @"C:\Tools\claude-custom.exe" : "/opt/claude-custom";
    var custom = claudeBuilder.Build();
    Equal(claudeBuilder.Command, custom.ResolvedCommand, "custom agent command");
    return Task.CompletedTask;
}

Task TestCodexSchemaEncodingAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "schema.json");
        LocalReviewAgent.WriteOutputSchema(path);
        var bytes = File.ReadAllBytes(path);
        Assert(bytes.Length > 0 && bytes[0] == (byte)'{', "Codex schema contains a UTF-8 BOM");
        using var document = JsonDocument.Parse(bytes);
        Equal(JsonValueKind.Object, document.RootElement.ValueKind, "Codex schema root");
        var findingProperties = document.RootElement
            .GetProperty("properties")
            .GetProperty("findings")
            .GetProperty("items")
            .GetProperty("properties");
        Assert(
            findingProperties.GetProperty("title").GetProperty("description").GetString()!
                .Contains("neutral declarative", StringComparison.Ordinal),
            "finding-title schema did not require declarative wording");
        Assert(
            findingProperties.GetProperty("body").GetProperty("description").GetString()!
                .Contains("Avoid commands", StringComparison.Ordinal),
            "finding-body schema did not discourage commands");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

Task TestReviewCommentToneAsync()
{
    var pullRequest = new CodexPullRequest(
        new RepositoryRef("owner", "repo"),
        42,
        "https://github.com/owner/repo/pull/42",
        "Adjust CI chunk sizing",
        "OPEN",
        false,
        "0123456789abcdef",
        "main",
        "contributor",
        DateTimeOffset.UtcNow,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        "MEMBER");
    var prompt = LocalReviewAgent.BuildPrompt(
        pullRequest,
        "reviewer",
        "Repository context",
        isManual: false);

    Assert(
        prompt.Contains("neutral declarative statement", StringComparison.Ordinal),
        "review prompt did not require declarative titles");
    Assert(
        prompt.Contains(
            "Chunk-sizing rationale does not match the matrix",
            StringComparison.Ordinal),
        "review prompt did not include a neutral title example");
    Assert(
        prompt.Contains("without addressing the author", StringComparison.Ordinal),
        "review prompt did not require an impersonal explanation");
    Assert(
        prompt.Contains("phrase it conditionally", StringComparison.Ordinal),
        "review prompt did not make remedies conditional");
    return Task.CompletedTask;
}

Task TestReviewSummaryAsync()
{
    var findings = new List<CodexReviewFinding>
    {
        Finding("critical", "src/A.cs"),
        Finding("high", "src/B.cs"),
        Finding("high", "src/B.cs"),
        Finding("low", "tests/A.Tests.cs"),
    };
    Equal(
        "Found 4 issues (1 critical, 2 high, and 1 low) in `src/A.cs`, `src/B.cs`, and `tests/A.Tests.cs`.",
        ReviewText.Summary(findings),
        "mixed-severity summary");
    Equal(
        "Found 1 medium-severity issue in `src/A.cs`.",
        ReviewText.Summary([Finding("medium", "src/A.cs")]),
        "single-severity summary");
    Assert(
        ReviewText.Summary(
            [
                Finding("low", "A.cs"),
                Finding("low", "B.cs"),
                Finding("low", "C.cs"),
                Finding("low", "D.cs"),
                Finding("low", "E.cs"),
            ]).Contains("1 other file", StringComparison.Ordinal),
        "summary did not abbreviate a long file list");
    Equal("No actionable issues found.", ReviewText.NoFindings(), "no-findings summary");
    Assert(!ReviewText.Summary(findings).Contains("review", StringComparison.OrdinalIgnoreCase), "summary mentions review state");
    Assert(!ReviewText.Summary(findings).Contains("Codex", StringComparison.OrdinalIgnoreCase), "summary mentions Codex");
    return Task.CompletedTask;
}

Task TestReviewPayloadDeliveryAsync()
{
    var payload = new GitHubReviewPayload(
        "0123456789abcdef",
        "Found 1 high-severity issue in `src/A.cs`.",
        [new GitHubPendingReviewComment("src/A.cs", 42, "RIGHT", "Specific issue")]);
    foreach (var options in new JsonSerializerOptions?[] { null, JsonDefaults.Options })
    {
        var json = options is null
            ? JsonSerializer.Serialize(payload)
            : JsonSerializer.Serialize(payload, options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert(!root.TryGetProperty("event", out _), "pending payload contains a submission event");
        Equal("0123456789abcdef", root.GetProperty("commit_id").GetString(), "pending payload commit");
        Equal(1, root.GetProperty("comments").GetArrayLength(), "pending payload comments");
    }

    var summaryOnly = JsonSerializer.Serialize(
        new GitHubReviewPayload("head", "No actionable issues found.", Comments: null));
    using var summaryDocument = JsonDocument.Parse(summaryOnly);
    Assert(!summaryDocument.RootElement.TryGetProperty("comments", out _), "null comments were serialized");
    Assert(!summaryDocument.RootElement.TryGetProperty("event", out _), "summary payload contains a submission event");

    var submitted = JsonSerializer.Serialize(
        new GitHubReviewPayload(
            "head",
            "Found 1 issue in `src/A.cs`.",
            [new GitHubPendingReviewComment("src/A.cs", 42, "RIGHT", "Specific issue")],
            Event: "COMMENT"));
    using var submittedDocument = JsonDocument.Parse(submitted);
    Equal("COMMENT", submittedDocument.RootElement.GetProperty("event").GetString(), "auto-send event");
    return Task.CompletedTask;
}

Task TestPendingReviewReuseAsync()
{
    var reviews = Elements("""
        [
          {"id":11,"node_id":"PRR_other","commit_id":"head","state":"PENDING","user":{"login":"other"}},
          {"id":12,"node_id":"PRR_submitted","commit_id":"head","state":"COMMENTED","user":{"login":"flcl42"}},
          {"id":13,"node_id":"PRR_pending","commit_id":"head","state":"PENDING","user":{"login":"flcl42"}}
        ]
        """);
    var pending = GitHubReviewApi.FindPendingReview(reviews, "FLCL42");
    Assert(pending is not null, "current user's pending review was not found");
    Equal(13L, pending!.Id, "pending review id");
    Equal("PRR_pending", pending.NodeId, "pending review node id");
    Equal("head", pending.CommitId, "pending review commit");

    var comments = Elements("""
        [
          {"pull_request_review_id":13,"path":"src/A.cs","line":42,"side":"RIGHT","body":"  Specific issue  "},
          {"pull_request_review_id":99,"path":"src/B.cs","line":7,"side":"LEFT","body":"Other review"},
          {"pull_request_review_id":13,"path":"src/Old.cs","line":null,"side":"RIGHT","body":"Outdated"}
        ]
        """);
    var keys = GitHubReviewApi.ExistingPendingCommentKeys(comments, pending.Id);
    Equal(1, keys.Count, "pending comment keys");
    Assert(
        keys.Contains(PendingReviewCommentKey.From(
            new GitHubPendingReviewComment("src\\A.cs", 42, "right", "Specific issue"))),
        "existing pending comment was not normalized for deduplication");
    Assert(GitHubReviewApi.FindPendingReview(reviews, "missing") is null, "another user's pending review was reused");
    return Task.CompletedTask;
}

Task TestPendingReviewWorkCountAsync()
{
    var records = new[]
    {
        Record("waiting", "waiting"),
        Record("queued", "queued"),
        Record("manual", "manual_queued"),
        Record("reviewing", "reviewing"),
        Record("reviewing-manual", "reviewing_manual"),
        Record("failed", "failed"),
        Record("failed-waiting", "failed_waiting"),
        Record("done", "done"),
        Record("skipped", "enough_human_activity"),
    };
    Equal(
        8,
        CodexReviewWatcher.CountPendingWork(records, ["QUEUED", "manual-fetch"]),
        "pending work count");
    return Task.CompletedTask;

    static CodexReviewRecord Record(string key, string status) => new()
    {
        Key = key,
        Status = status,
    };
}

Task TestReviewDiffAnchorsAsync()
{
    var anchors = ReviewDiff.ParseAnchors("""
        diff --git a/src/A.cs b/src/A.cs
        index 1111111..2222222 100644
        --- a/src/A.cs
        +++ b/src/A.cs
        @@ -10,2 +10,3 @@
        -old
        +new
         context
        +++ literal
        diff --git a/src/New.cs b/src/New.cs
        new file mode 100644
        --- /dev/null
        +++ b/src/New.cs
        @@ -0,0 +1,2 @@
        +one
        +two
        """);

    var anchorList = string.Join(", ", anchors.OrderBy(anchor => anchor.Path).ThenBy(anchor => anchor.Line).ThenBy(anchor => anchor.Side));
    Assert(anchors.Contains(new ReviewDiffAnchor("src/A.cs", 10, "LEFT")), $"deleted-line anchor missing: {anchorList}");
    Assert(anchors.Contains(new ReviewDiffAnchor("src/A.cs", 10, "RIGHT")), "added-line anchor missing");
    Assert(anchors.Contains(new ReviewDiffAnchor("src/A.cs", 11, "LEFT")), "left context anchor missing");
    Assert(anchors.Contains(new ReviewDiffAnchor("src/A.cs", 11, "RIGHT")), "right context anchor missing");
    Assert(anchors.Contains(new ReviewDiffAnchor("src/A.cs", 12, "RIGHT")), "plus-prefixed content was parsed as a header");
    Assert(anchors.Contains(new ReviewDiffAnchor("src/New.cs", 2, "RIGHT")), "new-file anchor missing");
    Assert(!anchors.Contains(new ReviewDiffAnchor("src/New.cs", 1, "LEFT")), "new-file left anchor was invented");
    return Task.CompletedTask;
}

static CodexReviewFinding Finding(string severity, string path)
{
    return new CodexReviewFinding
    {
        Severity = severity,
        Title = "Specific issue",
        Body = "Concrete failure path.",
        Path = path,
        Line = 1,
        Side = "RIGHT",
    };
}

Task TestClaudeStructuredOutputAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var resultPath = Path.Combine(directory, "result.json");
        var review = LocalReviewAgent.ParseClaudeResult(
            """
            {
              "type": "result",
              "subtype": "success",
              "is_error": false,
              "structured_output": {
                "summary": "No actionable issues",
                "findings": []
              }
            }
            """,
            resultPath);
        Equal("No actionable issues", review.Summary, "Claude review summary");
        Equal(0, review.Findings.Count, "Claude review findings");
        Assert(File.Exists(resultPath), "Claude structured result was not persisted");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

Task TestKimiStructuredOutputAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var resultPath = Path.Combine(directory, "result.json");
        var agentPath = Path.Combine(directory, "reviewer.md");
        LocalReviewAgent.WriteKimiAgentProfile(agentPath);
        var profile = File.ReadAllText(agentPath);
        Assert(profile.Contains("tools: Read, Grep, Glob", StringComparison.Ordinal), "Kimi profile is not read-only");
        Assert(!profile.Contains("Bash", StringComparison.Ordinal), "Kimi profile allows shell execution");

        var reviewJson =
            """
            {"summary":"No actionable issues","findings":[]}
            """;
        var output = string.Join(
            '\n',
            JsonSerializer.Serialize(new { role = "meta", type = "system.version", version = "0.34.0" }),
            JsonSerializer.Serialize(new { role = "assistant", content = $"```json\n{reviewJson}\n```" }),
            JsonSerializer.Serialize(new { role = "meta", type = "session.resume_hint" }));
        var review = LocalReviewAgent.ParseKimiResult(output, resultPath);
        Equal("No actionable issues", review.Summary, "Kimi review summary");
        Equal(0, review.Findings.Count, "Kimi review findings");
        Assert(File.Exists(resultPath), "Kimi structured result was not persisted");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

Task TestReviewWorkspacePathAsync()
{
    var settingsDirectory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    var repository = new RepositoryRef("owner", "repo");
    var pullRequest = new CodexPullRequest(
        repository,
        42,
        "https://github.com/owner/repo/pull/42",
        "Workspace path",
        "OPEN",
        false,
        "0123456789abcdef",
        "main",
        "contributor",
        DateTimeOffset.UtcNow,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        "MEMBER");
    var builder = CodexReviewSettings.Default.ToBuilder();
    builder.DataDirectory = "metadata";

    var fallback = ReviewPaths.Create(settingsDirectory, pullRequest, builder.Build());
    Equal(
        Path.GetFullPath(Path.Combine(settingsDirectory, "metadata")),
        fallback.WorkspaceDirectory,
        "workspace did not fall back to data directory");

    builder.WorkspaceDirectory = Path.Combine("cache", "reviews");
    var customized = ReviewPaths.Create(settingsDirectory, pullRequest, builder.Build());
    var expectedWorkspace = Path.GetFullPath(Path.Combine(settingsDirectory, "cache", "reviews"));
    Equal(expectedWorkspace, customized.WorkspaceDirectory, "custom workspace root");
    Equal(
        Path.Combine(expectedWorkspace, "repositories"),
        customized.RepositoriesDirectory,
        "repository cache root");
    Equal(
        Path.Combine(expectedWorkspace, "worktrees"),
        customized.WorktreesDirectory,
        "worktree root");
    if (OperatingSystem.IsWindows())
    {
        builder.WorkspaceDirectory = @"H:\reviews";
        var absolute = ReviewPaths.Create(settingsDirectory, pullRequest, builder.Build());
        Equal(Path.GetFullPath(@"H:\reviews"), absolute.WorkspaceDirectory, "absolute Windows workspace root");
    }

    return Task.CompletedTask;
}

Task TestStartupScanLookbackAsync()
{
    var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
    Assert(
        CodexReviewWatcher.IsWithinStartupScanWindow(now, now, startupScanDays: 4),
        "a just-created PR should be scanned");
    Assert(
        CodexReviewWatcher.IsWithinStartupScanWindow(now.AddDays(-4), now, startupScanDays: 4),
        "the four-day boundary should be included");
    Assert(
        !CodexReviewWatcher.IsWithinStartupScanWindow(now.AddDays(-4).AddTicks(-1), now, startupScanDays: 4),
        "PRs older than four days should stay baselined");
    Assert(
        !CodexReviewWatcher.IsWithinStartupScanWindow(now.AddTicks(1), now, startupScanDays: 4),
        "future timestamps should not enter the startup scan");
    Assert(
        !CodexReviewWatcher.IsWithinStartupScanWindow(now, now, startupScanDays: 0),
        "zero should disable the startup scan");
    return Task.CompletedTask;
}

Task TestManualReviewEnqueueAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var repository = new RepositoryRef("owner", "repo");
        var settingsPath = Path.Combine(directory, ".pr.yml");
        var settings = AppSettings.Load(settingsPath);
        settings.AddRepositories([repository]);
        settings.AddIgnoredPullRequest(new IgnoredPullRequest(repository, 42));
        settings.Save();
        var pullRequest = new PullRequestInfo(
            "PR_node",
            repository,
            42,
            "Queue this review",
            "contributor",
            "https://github.com/owner/repo/pull/42",
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [],
            new PullRequestPriority(30, PullRequestHeat.Hot, 0, 0, true, []),
            "MEMBER");

        var watcher = new CodexReviewWatcher(
            settings.Repositories,
            settingsPath,
            () => settings.CodexReview,
            () => settings.IgnoredPullRequestKeys,
            _ => { });
        var result = watcher.Enqueue(pullRequest);

        Equal(true, result.Enqueued, result.Message);
        Equal(true, watcher.IsManuallyQueued(pullRequest.Key), "manual queue was not visible in memory");
        Equal(1, watcher.Snapshot.ManualQueueCount, "manual queue count was not published");

        var reloaded = new CodexReviewWatcher(
            settings.Repositories,
            settingsPath,
            () => settings.CodexReview,
            () => settings.IgnoredPullRequestKeys,
            _ => { });
        Equal(true, reloaded.IsManuallyQueued(pullRequest.Key), "manual queue did not survive reload");
        Equal(false, reloaded.Enqueue(pullRequest).Enqueued, "duplicate manual queue was accepted");

        var statePath = Path.Combine(directory, ".pr-review", "state.json");
        Assert(File.Exists(statePath), "manual queue state file was not created");
        Assert(
            File.ReadAllText(statePath).Contains(pullRequest.Key, StringComparison.OrdinalIgnoreCase),
            "manual queue key was not persisted");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

Task TestManualReviewPolicyAsync()
{
    var repository = new RepositoryRef("owner", "repo");
    var original = new CodexPullRequest(
        repository,
        42,
        "https://github.com/owner/repo/pull/42",
        "Force review",
        "OPEN",
        true,
        "0123456789abcdef",
        "main",
        "flcl42",
        DateTimeOffset.UtcNow,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        "MEMBER");
    var settings = CodexReviewSettings.Default;

    Assert(
        CodexReviewWatcher.IsEligibleForReview(original, original, "flcl42", settings, isManual: true),
        "manual review did not bypass draft, author, and direct-request gates");
    Assert(
        !CodexReviewWatcher.IsEligibleForReview(original, original, "flcl42", settings, isManual: false),
        "automatic review bypassed its policy gates");
    Assert(
        !CodexReviewWatcher.IsEligibleForReview(
            original,
            original with { State = "CLOSED" },
            "flcl42",
            settings,
            isManual: true),
        "manual review accepted a closed PR");
    Assert(
        !CodexReviewWatcher.IsEligibleForReview(
            original,
            original with { HeadOid = "changed" },
            "flcl42",
            settings,
            isManual: true),
        "manual review accepted a changed head");

    var activity = new CodexReviewActivity(
        ["reviewer-one", "reviewer-two"],
        ["commenter-one", "commenter-two"],
        ["reviewer-one", "reviewer-two"]);
    Assert(
        !CodexReviewWatcher.ShouldSkipForActivity(activity, settings, isManual: true, out _),
        "manual review did not bypass human-activity gates");
    Assert(
        CodexReviewWatcher.ShouldSkipForActivity(activity, settings, isManual: false, out _),
        "automatic review ignored human-activity gates");

    var now = DateTimeOffset.UtcNow;
    var request = new CodexManualReviewRequest(
        original.Key,
        repository.FullName,
        original.Number,
        original.Url,
        original.Title,
        now.AddMinutes(-2));
    var record = CodexReviewRecord.From(original, now.AddMinutes(-3), armed: true);
    record.ManualQueuedAt = request.RequestedAt;
    record.EligibleSince = now;
    record.ReadyAt = now.AddMinutes(settings.ReadyDelayMinutes);
    record.RetryAt = now.AddSeconds(-1);
    CodexReviewWatcher.PrepareManualReview(record, request, now, settings);
    Equal(now, record.ReadyAt, "manual retry was delayed");
    Assert(record.EligibleSince <= now.AddMinutes(-settings.ReadyDelayMinutes), "manual retry lost immediate eligibility");
    Equal(now.AddSeconds(-1), record.RetryAt, "manual retry state was reset");
    return Task.CompletedTask;
}

Task TestExternalContributorSafetyAsync()
{
    foreach (var trustedAssociation in new[] { "OWNER", "MEMBER", "COLLABORATOR", "member" })
    {
        Assert(!GitHubAuthorAssociation.IsExternal(trustedAssociation), $"trusted association {trustedAssociation} was rejected");
    }

    foreach (var externalAssociation in new string?[] { null, "", "NONE", "CONTRIBUTOR", "FIRST_TIME_CONTRIBUTOR" })
    {
        Assert(GitHubAuthorAssociation.IsExternal(externalAssociation), $"external association {externalAssociation ?? "null"} was trusted");
    }

    var repository = new RepositoryRef("owner", "repo");
    var current = new CodexPullRequest(
        repository,
        43,
        "https://github.com/owner/repo/pull/43",
        "External contribution",
        "OPEN",
        false,
        "0123456789abcdef",
        "main",
        "outside-author",
        DateTimeOffset.UtcNow,
        new HashSet<string>(["flcl42"], StringComparer.OrdinalIgnoreCase),
        "CONTRIBUTOR");
    Assert(
        !CodexReviewWatcher.IsEligibleForReview(current, current, "flcl42", CodexReviewSettings.Default, isManual: true),
        "manual review accepted an external contributor");
    Assert(
        !CodexReviewWatcher.IsEligibleForReview(current, current, "flcl42", CodexReviewSettings.Default, isManual: false),
        "automatic review accepted an external contributor");

    var directory = Path.Combine(Path.GetTempPath(), "pr-external-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var settingsPath = Path.Combine(directory, ".pr.yml");
        var settings = AppSettings.Load(settingsPath);
        settings.AddRepositories([repository]);
        var watcher = new CodexReviewWatcher(
            settings.Repositories,
            settingsPath,
            () => settings.CodexReview,
            () => settings.IgnoredPullRequestKeys,
            _ => { });
        var dashboardItem = new PullRequestInfo(
            "PR_external",
            repository,
            current.Number,
            current.Title,
            current.Author,
            current.Url,
            current.CreatedAt,
            current.CreatedAt,
            [],
            new PullRequestPriority(0, PullRequestHeat.Green, 0, 0, true, []),
            current.AuthorAssociation);
        var result = watcher.Enqueue(dashboardItem);
        Equal(false, result.Enqueued, "external contributor was manually queued");
        Equal(false, watcher.IsManuallyQueued(dashboardItem.Key), "external queue request was persisted");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

Task TestActivityFilteringAsync()
{
    var reviews = Elements("""
        [
          {"id":1,"submitted_at":"2026-01-01T00:00:00Z","state":"APPROVED","body":"","user":{"login":"human-one","type":"User"}},
          {"id":2,"submitted_at":"2026-01-01T00:01:00Z","state":"APPROVED","body":"Useful review","user":{"login":"human-two","type":"User"}},
          {"id":3,"submitted_at":"2026-01-01T00:02:00Z","state":"APPROVED","body":"Author note","user":{"login":"author","type":"User"}},
          {"id":4,"submitted_at":"2026-01-01T00:03:00Z","state":"APPROVED","body":"Bot note","user":{"login":"robot-reviewer","type":"Bot"}},
          {"id":5,"submitted_at":"2026-01-01T00:04:00Z","state":"COMMENTED","body":"Another note","user":{"login":"human-three","type":"User"}}
        ]
        """);
    var issueComments = Elements("""
        [
          {"user":{"login":"human-one","type":"User"}},
          {"user":{"login":"author","type":"User"}},
          {"user":{"login":"claude[bot]","type":"Bot"}}
        ]
        """);
    var reviewComments = Elements("""
        [
          {"user":{"login":"HUMAN-TWO","type":"User"}},
          {"user":{"login":"github-copilot[bot]","type":"Bot"}}
        ]
        """);
    var activity = CodexReviewActivity.Calculate(
        "author",
        reviews,
        issueComments,
        reviewComments,
        CodexReviewSettings.Default.IgnoredAuthorPatterns);

    SetEqual(["human-one", "human-two"], activity.Approvers, "approvers");
    SetEqual(["human-one", "human-two", "human-three"], activity.Commenters, "commenters");
    SetEqual(["human-one", "human-two", "human-three"], activity.Reviewers, "reviewers");
    return Task.CompletedTask;
}

Task TestLatestApprovalStateAsync()
{
    var reviews = Elements("""
        [
          {"id":1,"submitted_at":"2026-01-01T00:00:00Z","state":"APPROVED","body":"","user":{"login":"one","type":"User"}},
          {"id":2,"submitted_at":"2026-01-01T00:01:00Z","state":"CHANGES_REQUESTED","body":"","user":{"login":"one","type":"User"}},
          {"id":3,"submitted_at":"2026-01-01T00:02:00Z","state":"CHANGES_REQUESTED","body":"","user":{"login":"two","type":"User"}},
          {"id":4,"submitted_at":"2026-01-01T00:03:00Z","state":"APPROVED","body":"","user":{"login":"two","type":"User"}},
          {"id":5,"submitted_at":"2026-01-01T00:04:00Z","state":"DISMISSED","body":"","user":{"login":"three","type":"User"}}
        ]
        """);
    var activity = CodexReviewActivity.Calculate(
        "author",
        reviews,
        [],
        [],
        CodexReviewSettings.Default.IgnoredAuthorPatterns);
    SetEqual(["two"], activity.Approvers, "latest approvals");
    return Task.CompletedTask;
}

Task TestContextPrecedenceAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-context-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllText(Path.Combine(directory, "nethermind.md"), "external context");
        var repository = new RepositoryRef("NethermindEth", "nethermind");
        var builder = CodexReviewSettings.Default.ToBuilder();
        builder.Contexts["nethermind"] = "short-name context";
        builder.Contexts["nethermindeth/NETHERMIND"] = "full-name context";

        Equal(
            "full-name context",
            ReviewContext.Load(repository, directory, builder.Build()),
            "full repository context precedence");

        builder.Contexts.Remove("nethermindeth/nethermind");
        Equal(
            "short-name context",
            ReviewContext.Load(repository, directory, builder.Build()),
            "short repository context fallback");

        builder.Contexts.Clear();
        Equal(
            "external context",
            ReviewContext.Load(repository, directory, builder.Build()),
            "external context fallback");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

Task TestEmbeddedContextAsync()
{
    var context = ReviewContext.Load(
        new RepositoryRef("NethermindEth", "nethermind"),
        Path.GetTempPath(),
        CodexReviewSettings.Default);
    Assert(context.Contains("Nethermind Pull Request Review Context", StringComparison.Ordinal), "embedded context was not found");
    return Task.CompletedTask;
}

async Task TestReviewAgentCommandsAsync()
{
    await CheckVersionAsync("codex", "codex");
    await CheckVersionAsync("claude", "claude");
    await CheckVersionAsync("kimi", expectedText: null);

    static async Task CheckVersionAsync(string command, string? expectedText)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                command,
                ["--version"],
                Directory.GetCurrentDirectory(),
                input: null,
                timeout: TimeSpan.FromSeconds(20),
                eligibilityCheckInterval: null,
                stillEligible: null,
                CancellationToken.None);
            Equal(0, result.ExitCode, result.StandardError);
            Assert(!string.IsNullOrWhiteSpace(result.StandardOutput), $"empty {command} version output");
            if (!string.IsNullOrWhiteSpace(expectedText))
            {
                Assert(
                    result.StandardOutput.Contains(expectedText, StringComparison.OrdinalIgnoreCase),
                    $"unexpected {command} version output: {result.StandardOutput}");
            }
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine($"SKIP {command} is not installed on this machine");
        }
    }
}

async Task TestLiveDashboardPriorityCacheAsync()
{
    var repositoryValue = Environment.GetEnvironmentVariable("PR_LIVE_TEST_REPO")!;
    Assert(RepositoryRef.TryParse(repositoryValue, out var repository), $"invalid live-test repository: {repositoryValue}");
    var client = new GhClient();
    var firstTimer = System.Diagnostics.Stopwatch.StartNew();
    var first = await client.FetchPullRequestsAsync(
        [repository],
        requiredApprovals: 2,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        PrioritySettings.Default,
        CancellationToken.None);
    firstTimer.Stop();
    var secondTimer = System.Diagnostics.Stopwatch.StartNew();
    var second = await client.FetchPullRequestsAsync(
        [repository],
        requiredApprovals: 2,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        PrioritySettings.Default,
        CancellationToken.None);
    secondTimer.Stop();

    Equal(first.OpenNonDraftCount, second.OpenNonDraftCount, "cached scan open PR count");
    SetEqual(first.Items.Select(item => item.Key), second.Items.Select(item => item.Key), "cached scan PRs");
    var secondByKey = second.Items.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
    foreach (var firstItem in first.Items)
    {
        var secondItem = secondByKey[firstItem.Key];
        Equal(firstItem.Priority.Score, secondItem.Priority.Score, $"cached score for {firstItem.Key}");
        Equal(
            firstItem.Priority.HumanCommenterCount,
            secondItem.Priority.HumanCommenterCount,
            $"cached commenters for {firstItem.Key}");
        Equal(
            firstItem.Priority.HumanReviewCount,
            secondItem.Priority.HumanReviewCount,
            $"cached reviews for {firstItem.Key}");
    }

    if (first.Items.Count > 0)
    {
        Assert(second.ApiCalls < first.ApiCalls, "unchanged PRs did not reuse cached priority inputs");
    }

    Console.WriteLine(
        $"Dashboard calls {first.ApiCalls} -> {second.ApiCalls}; "
        + $"elapsed {firstTimer.Elapsed.TotalSeconds:0.00}s -> {secondTimer.Elapsed.TotalSeconds:0.00}s.");
}

async Task TestLiveWeeklyStatsAsync()
{
    var repositoryValue = Environment.GetEnvironmentVariable("PR_LIVE_TEST_REPO")!;
    Assert(RepositoryRef.TryParse(repositoryValue, out var repository), $"invalid live-test repository: {repositoryValue}");
    var stats = await new GhClient().FetchWeeklyStatsAsync([repository], CancellationToken.None);
    Equal(1, stats.TrackedRepositoryCount, "weekly tracked repository count");
    Assert(!string.IsNullOrWhiteSpace(stats.UserLogin), "weekly stats user was empty");
    Assert(stats.CreatedPullRequestCount >= 0, "weekly created PR count was negative");
    Assert(stats.ReviewCount >= 0, "weekly review count was negative");
    Console.WriteLine(
        $"Weekly stats for @{stats.UserLogin}: {stats.CreatedPullRequestCount} PR(s), {stats.ReviewCount} review(s).");
}

async Task TestLiveRequestedPullRequestScanAsync()
{
    var repositoryValue = Environment.GetEnvironmentVariable("PR_LIVE_TEST_REPO")!;
    Assert(RepositoryRef.TryParse(repositoryValue, out var repository), $"invalid live-test repository: {repositoryValue}");
    var activeUser = await GitHubReviewApi.GetCurrentUserAsync(CancellationToken.None);
    var pullRequests = await GitHubReviewApi.ListRequestedPullRequestsAsync(
        [repository],
        activeUser,
        limit: 1_000,
        CancellationToken.None);
    Assert(
        pullRequests.All(pullRequest => pullRequest.RequestedUsers.Contains(activeUser)),
        "live scan returned a PR without a direct request for the active user");
    if (pullRequests.FirstOrDefault() is { } sample)
    {
        var activity = await GitHubReviewApi.GetActivityAsync(
            sample,
            CodexReviewSettings.Default,
            CancellationToken.None);
        var humanLogins = activity.Approvers.Concat(activity.Commenters).Concat(activity.Reviewers);
        Assert(
            humanLogins.All(login => !string.Equals(login, sample.Author, StringComparison.OrdinalIgnoreCase)),
            "live activity included the PR author");
        Assert(
            humanLogins.All(login => !CodexReviewSettings.Default.IgnoredAuthorPatterns.Any(
                pattern => login.Contains(pattern, StringComparison.OrdinalIgnoreCase))),
            "live activity included a configured bot pattern");
        Console.WriteLine(
            $"Sample #{sample.Number}: {activity.Approvers.Count} approval(s), {activity.Commenters.Count} commenter(s).");
    }

    Console.WriteLine($"Live scan found {pullRequests.Count} directly requested PR(s) for @{activeUser}.");
}

async Task TestLiveWatcherBaselineAsync()
{
    var repositoryValue = Environment.GetEnvironmentVariable("PR_LIVE_TEST_REPO")!;
    Assert(RepositoryRef.TryParse(repositoryValue, out var repository), $"invalid live-test repository: {repositoryValue}");
    var directory = Path.Combine(Path.GetTempPath(), "pr-live-watcher-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var settingsPath = Path.Combine(directory, ".pr.yml");
        var settings = AppSettings.Load(settingsPath);
        settings.AddRepositories([repository]);
        var reviewSettings = settings.CodexReview.ToBuilder();
        reviewSettings.StartupScanDays = 0;
        settings.ReplaceCodexReview(reviewSettings.Build());
        settings.SetCodexReviewEnabled(true);
        settings.Save();
        var watching = new TaskCompletionSource<CodexReviewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new CodexReviewWatcher(
            settings.Repositories,
            settingsPath,
            () => settings.CodexReview,
            () => settings.IgnoredPullRequestKeys,
            snapshot =>
            {
                if (snapshot.Message == "Codex review watching")
                {
                    watching.TrySetResult(snapshot);
                }
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var worker = watcher.RunAsync(cancellation.Token);
        var snapshot = await watching.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Equal(0, snapshot.WaitingCount, "baseline PRs must not be queued");
        Equal(0, snapshot.PendingReviewedPullRequests.Count, "baseline must not produce reviews");
        cancellation.Cancel();
        try
        {
            await worker;
        }
        catch (OperationCanceledException)
        {
        }

        var statePath = Path.Combine(directory, ".pr-review", "state.json");
        Assert(File.Exists(statePath), "watcher state was not persisted");
        var stateText = File.ReadAllText(statePath);
        Assert(stateText.Contains("baseline_ignored", StringComparison.Ordinal), "existing PRs were not baselined");
        Assert(!stateText.Contains("reviewing", StringComparison.Ordinal), "baseline unexpectedly started a review");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static IReadOnlyList<JsonElement> Elements(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
}

static void AssertArgumentPair(
    IReadOnlyList<string> arguments,
    string option,
    string value,
    string label)
{
    for (var index = 0; index < arguments.Count - 1; index++)
    {
        if (string.Equals(arguments[index], option, StringComparison.Ordinal)
            && string.Equals(arguments[index + 1], value, StringComparison.Ordinal))
        {
            return;
        }
    }

    throw new InvalidOperationException($"{label}: expected {option} {value}");
}

static void SetEqual(IEnumerable<string> expected, IEnumerable<string> actual, string label)
{
    var expectedSet = expected.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var actualSet = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (!expectedSet.SetEquals(actualSet))
    {
        throw new InvalidOperationException(
            $"{label}: expected [{string.Join(", ", expectedSet)}], actual [{string.Join(", ", actualSet)}]");
    }
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected {expected}, actual {actual}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

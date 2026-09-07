using System.Collections.ObjectModel;
using System.Globalization;

namespace PrDesktop;

public partial class MainPage : ContentPage
{
    private readonly DashboardApp _dashboard;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<ReviewAgent, ReviewAgentEditor> _reviewAgentEditors = [];
    private DashboardSnapshot? _snapshot;
    private Task? _backendTask;
    private PullRequestRow? _selected;
    private string? _selectedKey;
    private string _listFingerprint = string.Empty;
    private string _journalFingerprint = string.Empty;
    private string? _backendError;
    private int _refreshQueued;
    private bool _started;
    private bool _updatingControls;

    internal MainPage(AppSettings settings)
    {
        InitializeComponent();
        _dashboard = new DashboardApp(settings);
        BuildReviewAgentEditors();
        ApplySnapshot(_dashboard.GetSnapshot());
        _ = LoadReviewModelsAsync();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_started)
        {
            return;
        }

        _started = true;
        _backendTask = Task.Run(RunBackendAsync);
    }

    public void Stop()
    {
        _shutdown.Cancel();
    }

    private async Task RunBackendAsync()
    {
        try
        {
            await _dashboard.RunHeadlessAsync(QueueSnapshotRefresh, _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _backendError = ex.Message;
            QueueSnapshotRefresh();
        }
    }

    private void QueueSnapshotRefresh()
    {
        if (_shutdown.IsCancellationRequested
            || Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                ApplySnapshot(_dashboard.GetSnapshot(SearchEntry.Text ?? string.Empty));
            }
            finally
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
            }
        });
    }

    private void ApplySnapshot(DashboardSnapshot snapshot)
    {
        _snapshot = snapshot;
        RepositorySummaryLabel.Text = snapshot.RepositorySummary;
        ReviewSummaryLabel.Text = snapshot.Review.Message;

        RefreshIndicator.IsRunning = snapshot.IsRefreshing;
        RefreshIndicator.IsVisible = snapshot.IsRefreshing;
        RefreshButton.IsEnabled = !snapshot.IsRefreshing;
        if (snapshot.IsRefreshing)
        {
            RefreshStateLabel.Text = "Refreshing";
            RefreshStateLabel.TextColor = UiColors.Warning;
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            RefreshStateLabel.Text = "Refresh failed";
            RefreshStateLabel.TextColor = UiColors.Danger;
        }
        else
        {
            RefreshStateLabel.Text = "Up to date";
            RefreshStateLabel.TextColor = UiColors.Success;
        }

        CleanupButton.IsEnabled = snapshot.CanCleanup && !snapshot.IsCleaning;
        CleanupButton.Text = snapshot.IsCleaning ? "Cleaning" : "Clean";

        _updatingControls = true;
        ShowIgnoredSwitch.IsToggled = snapshot.ShowIgnoredPullRequests;
        ReviewEnabledSwitch.IsToggled = snapshot.ReviewSettings.Enabled;
        AutoSubmitSwitch.IsToggled = snapshot.ReviewSettings.AutoSubmit;
        _updatingControls = false;

        UpdatePullRequestList(snapshot);
        UpdateReviewPanel(snapshot);
        UpdateJournal(snapshot.Journal);
        UpdateOperations(snapshot);
    }

    private void UpdatePullRequestList(DashboardSnapshot snapshot)
    {
        var fingerprint = string.Join(
            '\n',
            snapshot.Items.Select(item => string.Join(
                '|',
                item.Section,
                item.PullRequest.Key,
                item.PullRequest.UpdatedAt.UtcTicks,
                item.PullRequest.ApprovalCount,
                item.PullRequest.Priority.Score,
                item.IsIgnored,
                item.IsTop,
                item.IsReviewed,
                item.IsReviewQueued)));
        if (!string.Equals(fingerprint, _listFingerprint, StringComparison.Ordinal))
        {
            _listFingerprint = fingerprint;
            var rows = snapshot.Items
                .Select(item => new PullRequestRow(item, snapshot.RequiredApprovals))
                .ToArray();
            var groups = new ObservableCollection<PullRequestGroup>();
            foreach (var section in new[] { "Reviewed", "Top", "Pull requests" })
            {
                var sectionRows = rows.Where(row => row.Source.Section == section).ToArray();
                if (sectionRows.Length > 0)
                {
                    groups.Add(new PullRequestGroup(section, sectionRows));
                }
            }

            PullRequestList.ItemsSource = groups;
            var selected = rows.FirstOrDefault(row => string.Equals(row.Key, _selectedKey, StringComparison.OrdinalIgnoreCase))
                ?? rows.FirstOrDefault();
            _selected = selected;
            _selectedKey = selected?.Key;
            PullRequestList.SelectedItem = selected;
        }
        else if (_selectedKey is not null)
        {
            var latest = snapshot.Items.FirstOrDefault(item =>
                string.Equals(item.PullRequest.Key, _selectedKey, StringComparison.OrdinalIgnoreCase));
            if (latest is not null)
            {
                _selected = new PullRequestRow(latest, snapshot.RequiredApprovals);
            }
        }

        EmptyPanel.IsVisible = snapshot.Items.Count == 0;
        InitialLoadIndicator.IsVisible = snapshot.Items.Count == 0 && snapshot.IsRefreshing;
        InitialLoadIndicator.IsRunning = InitialLoadIndicator.IsVisible;
        EmptyMessageLabel.Text = snapshot.IsRefreshing
            ? "Loading pull requests..."
            : snapshot.EmptyMessage;
        UpdateSelectedPanel();
    }

    private void UpdateSelectedPanel()
    {
        var selected = _selected;
        NoSelectionLabel.IsVisible = selected is null;
        SelectedPanel.IsVisible = selected is not null;
        if (selected is null)
        {
            return;
        }

        var item = selected.Source;
        var pullRequest = item.PullRequest;
        SelectedIdentityLabel.Text = $"{pullRequest.Repository.FullName}  #{pullRequest.Number.ToString(CultureInfo.InvariantCulture)}";
        SelectedTitleLabel.Text = item.DisplayTitle;
        SelectedMetadataLabel.Text = $"@{pullRequest.Author}  |  created {pullRequest.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}  |  {pullRequest.ApprovalCount.ToString(CultureInfo.InvariantCulture)}/{_snapshot?.RequiredApprovals.ToString(CultureInfo.InvariantCulture)} approvals";
        SelectedReviewStateLabel.Text = ReviewStateText(item);
        SelectedReviewStateLabel.IsVisible = SelectedReviewStateLabel.Text.Length > 0;
        TopButton.Text = item.IsTop ? "Remove from Top" : "Move to Top";
        IgnoreButton.Text = item.IsIgnored ? "Unignore" : "Ignore";
        ReviewNowButton.IsEnabled = !item.IsReviewQueued;
        ReviewNowButton.Text = item.IsReviewQueued ? "Review queued" : "Review now";
        AcknowledgeButton.IsVisible = item.IsReviewed;

        var priority = pullRequest.Priority;
        PrioritySummaryButton.Text = priority.Label;
        PrioritySummaryButton.TextColor = UiColors.ForHeat(priority.Heat);
        PriorityActivityLabel.Text = $"{priority.HumanCommenterCount.ToString(CultureInfo.InvariantCulture)} human commenter(s), {priority.HumanReviewCount.ToString(CultureInfo.InvariantCulture)} reviewer(s)";
        PopulatePriorityRules(PriorityRulesLayout, priority, appliedOnly: true);
    }

    private static string ReviewStateText(DashboardPullRequest item)
    {
        if (item.IsReviewQueued)
        {
            return "Manual review queued";
        }

        var review = item.ReviewResult;
        if (review is null)
        {
            return string.Empty;
        }

        var delivery = review.IsDryRun
            ? "Local result"
            : review.ReviewSubmitted
                ? "Review sent"
                : review.ReviewCreated
                    ? "Draft ready"
                    : "Review complete";
        return $"{delivery}: {review.FindingCount.ToString(CultureInfo.InvariantCulture)} issue(s)";
    }

    private void UpdateReviewPanel(DashboardSnapshot snapshot)
    {
        foreach (var agent in snapshot.ReviewSettings.AvailableAgents)
        {
            if (_reviewAgentEditors.TryGetValue(agent.Agent, out var editor))
            {
                editor.Update(agent);
            }
        }

        var agents = snapshot.ReviewSettings.EnabledAgents;
        AgentPipelineLabel.Text = agents.Count == 0
            ? "No agents enabled in .pr.yml"
            : "Pipeline: " + string.Join("  >  ", agents.Select(agent => agent.Descriptor));
        ReviewStatusLabel.Text = snapshot.Review.Message;
        ReviewStatusLabel.TextColor = snapshot.Review.Enabled ? UiColors.Success : UiColors.Muted;
        ReviewQueueLabel.Text = $"{snapshot.Review.WaitingCount.ToString(CultureInfo.InvariantCulture)} waiting  |  {snapshot.Review.ManualQueueCount.ToString(CultureInfo.InvariantCulture)} manually queued";
    }

    private void BuildReviewAgentEditors()
    {
        ReviewAgentOptionsLayout.Children.Clear();
        foreach (var agent in _dashboard.ReviewAgents)
        {
            var editor = new ReviewAgentEditor(this, agent);
            editor.Changed += OnReviewAgentChanged;
            _reviewAgentEditors[agent.Agent] = editor;
            ReviewAgentOptionsLayout.Children.Add(editor.View);
        }
    }

    private async Task LoadReviewModelsAsync()
    {
        await Task.WhenAll(_reviewAgentEditors.Values.Select(editor => editor.LoadModelsAsync()));
    }

    private void OnReviewAgentChanged(object? sender, EventArgs args)
    {
        if (_updatingControls)
        {
            return;
        }

        _dashboard.SetCodexReviewAgents(_reviewAgentEditors.Values
            .Select(editor => editor.Settings)
            .ToArray());
        QueueSnapshotRefresh();
    }

    private void UpdateJournal(IReadOnlyList<JournalOperation> operations)
    {
        JournalIndicator.IsRunning = operations.Any(operation => operation.Status == JournalOperationStatus.Running);
        JournalIndicator.IsVisible = JournalIndicator.IsRunning;
        JournalEmptyLabel.IsVisible = operations.Count == 0;
        var fingerprint = string.Join(
            '\n',
            operations.Select(operation => string.Join(
                '|',
                operation.Id,
                operation.Status,
                operation.CompletedAt?.UtcTicks,
                string.Join(',', operation.Steps.Select(step =>
                    $"{step.Key}:{step.Status}:{step.Detail}:{step.StartedAt?.UtcTicks}:{step.CompletedAt?.UtcTicks}")))));
        if (string.Equals(fingerprint, _journalFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _journalFingerprint = fingerprint;
        JournalEntriesLayout.Children.Clear();
        for (var index = 0; index < operations.Count; index++)
        {
            JournalEntriesLayout.Children.Add(CreateJournalOperationView(operations[index], index == operations.Count - 1));
        }
    }

    private static View CreateJournalOperationView(JournalOperation operation, bool isLast)
    {
        var statusColor = UiColors.ForJournal(operation.Status);
        var title = new Label
        {
            Text = operation.Title,
            FontAttributes = FontAttributes.Bold,
            FontSize = 14,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        if (!string.IsNullOrWhiteSpace(operation.Url))
        {
            title.TextColor = UiColors.Accent;
            title.TextDecorations = TextDecorations.Underline;
            var open = new TapGestureRecognizer();
            open.Tapped += (_, _) => PullRequests.OpenUrl(operation.Url);
            title.GestureRecognizers.Add(open);
        }

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
        };
        header.Children.Add(title);
        var status = new Label
        {
            Text = JournalStatusText(operation.Status),
            TextColor = statusColor,
            FontAttributes = FontAttributes.Bold,
            FontSize = 12,
            HorizontalTextAlignment = TextAlignment.End,
        };
        Grid.SetColumn(status, 1);
        header.Children.Add(status);

        var metadata = operation.CompletedAt is null
            ? $"Started {operation.StartedAt.LocalDateTime:MMM d HH:mm:ss}"
            : $"{operation.StartedAt.LocalDateTime:MMM d HH:mm:ss}  |  {TimelineView.FormatDuration(operation.CompletedAt.Value - operation.StartedAt)}";
        var details = new VerticalStackLayout
        {
            Spacing = 1,
            Children =
            {
                new Label
                {
                    Text = operation.Subtitle,
                    IsVisible = !string.IsNullOrWhiteSpace(operation.Subtitle),
                    TextColor = UiColors.Muted,
                    FontSize = 12,
                    LineBreakMode = LineBreakMode.WordWrap,
                    MaxLines = 2,
                },
                new Label
                {
                    Text = metadata,
                    TextColor = UiColors.MutedLight,
                    FontSize = 11,
                },
            },
        };
        var timeline = new VerticalStackLayout { Spacing = 0, Margin = new Thickness(0, 5, 0, 0) };
        for (var index = 0; index < operation.Steps.Count; index++)
        {
            timeline.Children.Add(TimelineView.CreateStep(
                operation.Steps[index],
                isFirst: index == 0,
                isLast: index == operation.Steps.Count - 1));
        }

        return new VerticalStackLayout
        {
            Spacing = 3,
            Children =
            {
                header,
                details,
                timeline,
                new BoxView
                {
                    Color = UiColors.Border,
                    HeightRequest = isLast ? 0 : 1,
                    IsVisible = !isLast,
                    Margin = new Thickness(0, 5, 0, 0),
                },
            },
        };
    }

    private void UpdateOperations(DashboardSnapshot snapshot)
    {
        CleanupStatusLabel.Text = snapshot.IsCleaning
            ? "Cleaning tracked GitHub notifications and saved PR state..."
            : $"Cleanup: {snapshot.LastCleanupScanned.ToString(CultureInfo.InvariantCulture)} scanned, {snapshot.LastCleanupMarkedRead.ToString(CultureInfo.InvariantCulture)} marked read, {snapshot.LastCleanupRemovedIgnored.ToString(CultureInfo.InvariantCulture)} ignored entries removed";
        var errors = new[] { snapshot.Error, snapshot.CleanupError, _backendError }
            .Where(error => !string.IsNullOrWhiteSpace(error));
        ErrorLabel.Text = string.Join(Environment.NewLine, errors);
        ErrorLabel.IsVisible = ErrorLabel.Text.Length > 0;
        SettingsPathLabel.Text = "Settings: " + snapshot.SettingsPath;
        ToolTipProperties.SetText(SettingsPathLabel, snapshot.SettingsPath);
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs args)
    {
        ApplySnapshot(_dashboard.GetSnapshot(args.NewTextValue ?? string.Empty));
    }

    private void OnPullRequestSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        _selected = args.CurrentSelection.FirstOrDefault() as PullRequestRow;
        _selectedKey = _selected?.Key;
        UpdateSelectedPanel();
    }

    private void OnRefreshClicked(object? sender, EventArgs args)
    {
        _dashboard.RequestRefresh();
        QueueSnapshotRefresh();
    }

    private void OnCleanupClicked(object? sender, EventArgs args)
    {
        _dashboard.RequestCleanup();
        QueueSnapshotRefresh();
    }

    private async void OnStatsClicked(object? sender, EventArgs args)
    {
        try
        {
            var stats = await _dashboard.FetchWeeklyStatsAsync(_shutdown.Token);
            await Navigation.PushModalAsync(new WeeklyStatsPage(stats));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Weekly stats", ex.Message, "Close");
        }
    }

    private async void OnAddRepositoryClicked(object? sender, EventArgs args)
    {
        var value = await DisplayPromptAsync(
            "Track repository",
            "Enter a GitHub URL or OWNER/REPO.",
            accept: "Add",
            cancel: "Cancel",
            placeholder: "https://github.com/OWNER/REPO",
            maxLength: 300,
            keyboard: Keyboard.Url);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!RepositoryRef.TryParse(value, out var repository))
        {
            await DisplayAlertAsync("Track repository", "That is not a valid GitHub repository URL or OWNER/REPO value.", "Close");
            return;
        }

        try
        {
            var update = _dashboard.AddRepositories([repository]);
            if (update.Added.Count == 0)
            {
                await DisplayAlertAsync("Track repository", $"{repository.FullName} is already tracked.", "Close");
            }

            QueueSnapshotRefresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await DisplayAlertAsync("Track repository", ex.Message, "Close");
        }
    }

    private async void OnLocalReviewClicked(object? sender, EventArgs args)
    {
        await Navigation.PushModalAsync(new LocalReviewPage(_dashboard, _shutdown.Token));
    }

    private void OnShowIgnoredToggled(object? sender, ToggledEventArgs args)
    {
        if (_updatingControls)
        {
            return;
        }

        _dashboard.SetIgnoredVisibility(args.Value);
        QueueSnapshotRefresh();
    }

    private void OnReviewEnabledToggled(object? sender, ToggledEventArgs args)
    {
        if (_updatingControls)
        {
            return;
        }

        _dashboard.SetCodexReviewEnabled(args.Value);
        QueueSnapshotRefresh();
    }

    private void OnAutoSubmitToggled(object? sender, ToggledEventArgs args)
    {
        if (_updatingControls)
        {
            return;
        }

        _dashboard.SetCodexReviewAutoSubmit(args.Value);
        QueueSnapshotRefresh();
    }

    private async void OnPriorityClicked(object? sender, EventArgs args)
    {
        if (sender is Button { CommandParameter: PullRequestRow row })
        {
            await ShowPriorityAsync(row.Source.PullRequest);
        }
    }

    private async void OnSelectedPriorityClicked(object? sender, EventArgs args)
    {
        if (_selected is not null)
        {
            await ShowPriorityAsync(_selected.Source.PullRequest);
        }
    }

    private async Task ShowPriorityAsync(PullRequestInfo pullRequest)
    {
        await Navigation.PushModalAsync(new PriorityPage(pullRequest));
    }

    private void OnTitleTapped(object? sender, TappedEventArgs args)
    {
        if (sender is TapGestureRecognizer { CommandParameter: PullRequestRow row })
        {
            OpenPullRequest(row.Source, acknowledge: true);
        }
    }

    private void OnSelectedTitleTapped(object? sender, TappedEventArgs args)
    {
        if (_selected is not null)
        {
            OpenPullRequest(_selected.Source, acknowledge: true);
        }
    }

    private void OpenPullRequest(DashboardPullRequest item, bool acknowledge)
    {
        PullRequests.OpenUrl(item.PullRequest.Url);
        if (acknowledge && item.IsReviewed)
        {
            _dashboard.AcknowledgeCodexReview(item.PullRequest);
            QueueSnapshotRefresh();
        }
    }

    private void OnToggleTopClicked(object? sender, EventArgs args)
    {
        if (_selected is not null)
        {
            _dashboard.ToggleTop(_selected.Source.PullRequest);
            QueueSnapshotRefresh();
        }
    }

    private void OnToggleIgnoredClicked(object? sender, EventArgs args)
    {
        if (_selected is not null)
        {
            _dashboard.ToggleIgnored(_selected.Source.PullRequest);
            QueueSnapshotRefresh();
        }
    }

    private async void OnReviewNowClicked(object? sender, EventArgs args)
    {
        if (_selected is null)
        {
            return;
        }

        var result = _dashboard.EnqueueCodexReview(_selected.Source.PullRequest);
        QueueSnapshotRefresh();
        await DisplayAlertAsync(result.Enqueued ? "Review queued" : "Review not queued", result.Message, "Close");
    }

    private void OnAcknowledgeClicked(object? sender, EventArgs args)
    {
        if (_selected is not null)
        {
            _dashboard.AcknowledgeCodexReview(_selected.Source.PullRequest);
            QueueSnapshotRefresh();
        }
    }

    internal static void PopulatePriorityRules(
        Layout layout,
        PullRequestPriority priority,
        bool appliedOnly)
    {
        layout.Children.Clear();
        foreach (var rule in priority.Rules.Where(rule => !appliedOnly || rule.Applied))
        {
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
                ColumnSpacing = 8,
            };
            var color = !rule.Applied
                ? UiColors.MutedLight
                : rule.Points < 0
                    ? UiColors.Success
                    : rule.Points > 0
                        ? UiColors.Danger
                        : UiColors.Muted;
            var label = new Label
            {
                Text = rule.Label,
                TextColor = color,
                FontSize = 12,
                LineBreakMode = LineBreakMode.WordWrap,
            };
            var points = new Label
            {
                Text = rule.Applied
                    ? rule.Points.ToString("+0;-0;0", CultureInfo.InvariantCulture)
                    : "not applied",
                TextColor = color,
                FontSize = 12,
                HorizontalTextAlignment = TextAlignment.End,
            };
            Grid.SetColumn(points, 1);
            row.Children.Add(label);
            row.Children.Add(points);
            layout.Children.Add(row);
        }
    }

    private static string JournalStatusText(JournalOperationStatus status) => status switch
    {
        JournalOperationStatus.CompletedWithErrors => "Completed with errors",
        JournalOperationStatus.Completed => "Completed",
        JournalOperationStatus.Failed => "Failed",
        JournalOperationStatus.Canceled => "Canceled",
        _ => "Running",
    };
}

internal sealed class PullRequestGroup : List<PullRequestRow>
{
    public PullRequestGroup(string name, IEnumerable<PullRequestRow> rows)
        : base(rows)
    {
        Name = name;
    }

    public string Name { get; }

    public string CountLabel => $"{Count.ToString(CultureInfo.InvariantCulture)} PR{(Count == 1 ? string.Empty : "s")}";
}

internal sealed class PullRequestRow
{
    public PullRequestRow(DashboardPullRequest source, int requiredApprovals)
    {
        Source = source;
        var pullRequest = source.PullRequest;
        RepositoryName = pullRequest.Repository.Name;
        NumberText = "#" + pullRequest.Number.ToString(CultureInfo.InvariantCulture);
        Title = source.DisplayTitle;
        Author = pullRequest.Author;
        AgeText = FormatAge(DateTimeOffset.UtcNow - pullRequest.CreatedAt);
        ApprovalText = $"{pullRequest.ApprovalCount.ToString(CultureInfo.InvariantCulture)}/{requiredApprovals.ToString(CultureInfo.InvariantCulture)}";
        ApprovalColor = pullRequest.ApprovalCount == 0
            ? UiColors.Danger
            : pullRequest.ApprovalCount < requiredApprovals
                ? UiColors.Warning
                : UiColors.Success;
        HeatColor = UiColors.ForHeat(pullRequest.Priority.Heat);
        BackgroundColor = source.IsIgnored
            ? UiColors.IgnoredRow
            : source.IsReviewed
                ? UiColors.ReviewedRow
                : source.IsTop
                    ? UiColors.TopRow
                    : Colors.Transparent;
    }

    internal DashboardPullRequest Source { get; }

    public string Key => Source.PullRequest.Key;

    public string RepositoryName { get; }

    public string NumberText { get; }

    public string Title { get; }

    public string Author { get; }

    public string AgeText { get; }

    public string ApprovalText { get; }

    public Color ApprovalColor { get; }

    public Color HeatColor { get; }

    public Color BackgroundColor { get; }

    private static string FormatAge(TimeSpan age)
    {
        age = age < TimeSpan.Zero ? TimeSpan.Zero : age;
        if (age.TotalMinutes < 60)
        {
            return $"{Math.Max(0, (int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture)}m";
        }

        if (age.TotalHours < 24)
        {
            return $"{(int)age.TotalHours}h";
        }

        return $"{(int)age.TotalDays}d";
    }
}

internal sealed class PriorityPage : ContentPage
{
    public PriorityPage(PullRequestInfo pullRequest)
    {
        Title = "Urgency calculation";
        BackgroundColor = UiColors.Canvas;
        var priority = pullRequest.Priority;
        var rules = new VerticalStackLayout { Spacing = 8 };
        MainPage.PopulatePriorityRules(rules, priority, appliedOnly: false);
        var close = new Button
        {
            Text = "Close",
            BackgroundColor = UiColors.Accent,
            BorderColor = UiColors.Accent,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.End,
        };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        Content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            },
            Padding = new Thickness(24),
            RowSpacing = 14,
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = 4,
                    Children =
                    {
                        new Label { Text = $"{pullRequest.Repository.FullName} #{pullRequest.Number.ToString(CultureInfo.InvariantCulture)}", TextColor = UiColors.Muted, FontSize = 12 },
                        new Label { Text = pullRequest.Title, FontAttributes = FontAttributes.Bold, FontSize = 20 },
                        new Label { Text = $"Score {priority.Score.ToString(CultureInfo.InvariantCulture)} - {priority.Heat}", TextColor = UiColors.ForHeat(priority.Heat), FontAttributes = FontAttributes.Bold, FontSize = 16 },
                    },
                },
                new ScrollView
                {
                    Content = rules,
                }.AtGridRow(1),
                close.AtGridRow(2),
            },
        };
    }
}

internal sealed class WeeklyStatsPage : ContentPage
{
    public WeeklyStatsPage(WeeklyStats stats)
    {
        Title = "Weekly stats";
        BackgroundColor = UiColors.Canvas;
        var close = new Button
        {
            Text = "Close",
            BackgroundColor = UiColors.Accent,
            BorderColor = UiColors.Accent,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.End,
        };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();
        Content = new VerticalStackLayout
        {
            Padding = new Thickness(28),
            Spacing = 16,
            MaximumWidthRequest = 560,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = "This week", FontSize = 24, FontAttributes = FontAttributes.Bold },
                new Label { Text = $"@{stats.UserLogin} across {stats.TrackedRepositoryCount.ToString(CultureInfo.InvariantCulture)} tracked repository/repositories", TextColor = UiColors.Muted },
                StatLine("Pull requests created", stats.CreatedPullRequestCount),
                StatLine("Reviews submitted", stats.ReviewCount),
                new Label { Text = $"{stats.WeekStartLocal:yyyy-MM-dd} through {stats.WeekEndLocal:yyyy-MM-dd}", TextColor = UiColors.Muted, FontSize = 12 },
                close,
            },
        };
    }

    private static View StatLine(string label, int value)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            Padding = new Thickness(0, 8),
        };
        var name = new Label { Text = label, FontSize = 16 };
        var count = new Label
        {
            Text = value.ToString(CultureInfo.InvariantCulture),
            FontSize = 24,
            FontAttributes = FontAttributes.Bold,
            TextColor = UiColors.Accent,
        };
        Grid.SetColumn(count, 1);
        grid.Children.Add(name);
        grid.Children.Add(count);
        return grid;
    }
}

internal static class UiColors
{
    public static Color Canvas { get; } = Color.FromArgb("#F4F5F7");
    public static Color Surface { get; } = Color.FromArgb("#FFFFFF");
    public static Color Border { get; } = Color.FromArgb("#D5D9E0");
    public static Color Accent { get; } = Color.FromArgb("#0969DA");
    public static Color Success { get; } = Color.FromArgb("#1A7F37");
    public static Color Warning { get; } = Color.FromArgb("#9A6700");
    public static Color Danger { get; } = Color.FromArgb("#CF222E");
    public static Color Muted { get; } = Color.FromArgb("#66707C");
    public static Color MutedLight { get; } = Color.FromArgb("#9AA1AA");
    public static Color IgnoredRow { get; } = Color.FromArgb("#F0F1F2");
    public static Color ReviewedRow { get; } = Color.FromArgb("#EFFAF2");
    public static Color TopRow { get; } = Color.FromArgb("#F4F8FD");

    public static Color ForHeat(PullRequestHeat heat) => heat switch
    {
        PullRequestHeat.SuperHot => Danger,
        PullRequestHeat.Hot => Warning,
        _ => Success,
    };

    public static Color ForPipeline(ReviewPipelineStatus status) => status switch
    {
        ReviewPipelineStatus.Completed => Success,
        ReviewPipelineStatus.CompletedWithErrors => Warning,
        ReviewPipelineStatus.Failed => Danger,
        ReviewPipelineStatus.Canceled => Muted,
        _ => Accent,
    };

    public static Color ForStage(ReviewAgentStageStatus status) => status switch
    {
        ReviewAgentStageStatus.Completed => Success,
        ReviewAgentStageStatus.Failed => Danger,
        ReviewAgentStageStatus.Canceled => Muted,
        ReviewAgentStageStatus.Running => Accent,
        _ => Muted,
    };

    public static Color ForJournal(JournalOperationStatus status) => status switch
    {
        JournalOperationStatus.Completed => Success,
        JournalOperationStatus.CompletedWithErrors => Warning,
        JournalOperationStatus.Failed => Danger,
        JournalOperationStatus.Canceled => Muted,
        _ => Accent,
    };

    public static Color ForJournalStep(JournalStepStatus status) => status switch
    {
        JournalStepStatus.Completed => Success,
        JournalStepStatus.Failed => Danger,
        JournalStepStatus.Canceled => MutedLight,
        JournalStepStatus.Running => Accent,
        _ => MutedLight,
    };
}

internal static class GridPositionExtensions
{
    public static T AtGridRow<T>(this T view, int row)
        where T : BindableObject
    {
        Grid.SetRow(view, row);
        return view;
    }

    public static T AtGridColumn<T>(this T view, int column)
        where T : BindableObject
    {
        Grid.SetColumn(view, column);
        return view;
    }
}

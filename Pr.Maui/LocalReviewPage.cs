using System.Collections.ObjectModel;
using System.Globalization;
using Windows.Storage.Pickers;

namespace PrDesktop;

internal sealed class LocalReviewPage : ContentPage
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly DashboardApp _dashboard;
    private readonly CancellationToken _appCancellation;
    private readonly ObservableCollection<LocalDirectoryRow> _directories = [];
    private readonly Dictionary<ReviewAgent, ReviewAgentEditor> _agentEditors = [];
    private readonly HashSet<string> _startingPaths = new(PathComparer);
    private readonly Entry _pathEntry;
    private readonly CollectionView _directoryList;
    private readonly Label _emptyDirectoriesLabel;
    private readonly Grid _agentOptions;
    private readonly Button _reviewButton;
    private readonly Button _removeButton;
    private readonly Button _cancelButton;
    private readonly Button _openReportButton;
    private readonly ActivityIndicator _activity;
    private readonly Label _projectLabel;
    private readonly Label _statusLabel;
    private readonly Label _detailLabel;
    private readonly Label _resultLabel;
    private readonly VerticalStackLayout _stages;
    private readonly IDispatcherTimer _timer;
    private LocalDirectoryRow? _selected;
    private string? _lastProgressFingerprint;

    public LocalReviewPage(DashboardApp dashboard, CancellationToken appCancellation)
    {
        _dashboard = dashboard;
        _appCancellation = appCancellation;
        Title = "Local review";
        BackgroundColor = UiColors.Canvas;

        _pathEntry = new Entry
        {
            Placeholder = "Local project directory",
            ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
            HorizontalOptions = LayoutOptions.Fill,
        };
        var browseButton = new Button
        {
            Text = "Browse",
        };
        ToolTipProperties.SetText(browseButton, "Choose a project directory");
        browseButton.Clicked += OnBrowseClicked;
        var addButton = new Button
        {
            Text = "Add",
            Style = (Style)Application.Current!.Resources["PrimaryButton"],
        };
        addButton.Clicked += OnAddClicked;

        _directoryList = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsSource = _directories,
            ItemTemplate = new DataTemplate(CreateDirectoryView),
            VerticalOptions = LayoutOptions.Fill,
        };
        _directoryList.SelectionChanged += OnSelectionChanged;
        _emptyDirectoriesLabel = new Label
        {
            Text = "No local directories saved.",
            TextColor = UiColors.Muted,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };

        _agentOptions = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
            },
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
            RowSpacing = 2,
        };
        foreach (var (agent, index) in _dashboard.LocalReviewAgents.Select((agent, index) => (agent, index)))
        {
            var editor = new ReviewAgentEditor(this, agent);
            editor.Changed += (_, _) => UpdateSelectionActions();
            _agentEditors[agent.Agent] = editor;
            Grid.SetRow(editor.View, index);
            _agentOptions.Children.Add(editor.View);
        }

        _reviewButton = new Button
        {
            Text = "Review it",
            Style = (Style)Application.Current.Resources["PrimaryButton"],
            IsEnabled = false,
        };
        _reviewButton.Clicked += OnReviewClicked;
        _removeButton = new Button { Text = "Remove", IsEnabled = false };
        _removeButton.Clicked += OnRemoveClicked;

        _activity = new ActivityIndicator
        {
            WidthRequest = 22,
            HeightRequest = 22,
            Color = UiColors.Accent,
        };
        _projectLabel = new Label
        {
            Text = "Select a directory",
            FontAttributes = FontAttributes.Bold,
            FontSize = 20,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        _statusLabel = new Label
        {
            Text = "Ready",
            TextColor = UiColors.Muted,
            FontAttributes = FontAttributes.Bold,
        };
        _detailLabel = new Label
        {
            TextColor = UiColors.Muted,
            FontSize = 12,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        _resultLabel = new Label
        {
            TextColor = UiColors.Muted,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        _stages = new VerticalStackLayout { Spacing = 8 };
        _cancelButton = new Button { Text = "Cancel", IsVisible = false };
        _cancelButton.Clicked += OnCancelClicked;
        _openReportButton = new Button
        {
            Text = "Open report",
            IsVisible = false,
            Style = (Style)Application.Current.Resources["PrimaryButton"],
        };
        _openReportButton.Clicked += OnOpenReportClicked;

        var closeButton = new Button { Text = "Close" };
        closeButton.Clicked += async (_, _) => await Navigation.PopModalAsync();

        Content = BuildLayout(
            browseButton,
            addButton,
            closeButton);

        ReloadDirectories();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += OnTimerTick;
        _ = LoadReviewModelsAsync();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ReloadDirectories();
        RefreshProgress(force: true);
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        _timer.Stop();
        base.OnDisappearing();
    }

    private View BuildLayout(Button browseButton, Button addButton, Button closeButton)
    {
        var pathRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 7,
        };
        pathRow.Children.Add(_pathEntry);
        Grid.SetColumn(browseButton, 1);
        pathRow.Children.Add(browseButton);
        Grid.SetColumn(addButton, 2);
        pathRow.Children.Add(addButton);

        var listArea = new Grid();
        listArea.Children.Add(_directoryList);
        listArea.Children.Add(_emptyDirectoriesLabel);

        var actionRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 7,
        };
        actionRow.Children.Add(_reviewButton);
        Grid.SetColumn(_removeButton, 1);
        actionRow.Children.Add(_removeButton);

        var leftPanel = new Border
        {
            BackgroundColor = UiColors.Surface,
            Stroke = UiColors.Border,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = 14,
            Content = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                },
                RowSpacing = 10,
                Children =
                {
                    new Label { Text = "Projects", Style = (Style)Application.Current!.Resources["SectionHeading"] },
                    pathRow.AtGridRow(1),
                    listArea.AtGridRow(2),
                    new Label { Text = "Agents - model / effort", Style = (Style)Application.Current.Resources["SectionHeading"] }.AtGridRow(3),
                    _agentOptions.AtGridRow(4),
                    actionRow.AtGridRow(5),
                },
            },
        };

        var progressHeader = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        progressHeader.Children.Add(_projectLabel);
        Grid.SetColumn(_activity, 1);
        progressHeader.Children.Add(_activity);

        var resultActions = new HorizontalStackLayout
        {
            Spacing = 7,
            HorizontalOptions = LayoutOptions.End,
            Children = { _cancelButton, _openReportButton },
        };
        var rightPanel = new Border
        {
            BackgroundColor = UiColors.Surface,
            Stroke = UiColors.Border,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = 16,
            Content = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto),
                },
                RowSpacing = 8,
                Children =
                {
                    progressHeader,
                    _statusLabel.AtGridRow(1),
                    _detailLabel.AtGridRow(2),
                    _resultLabel.AtGridRow(3),
                    new ScrollView { Content = _stages }.AtGridRow(4),
                    resultActions.AtGridRow(5),
                },
            },
        };

        var body = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(0.9, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(1.25, GridUnitType.Star)),
            },
            ColumnSpacing = 12,
        };
        body.Children.Add(leftPanel);
        Grid.SetColumn(rightPanel, 1);
        body.Children.Add(rightPanel);

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = 0,
                    Children =
                    {
                        new Label { Text = "Local review", FontSize = 24, FontAttributes = FontAttributes.Bold },
                    },
                },
                closeButton.AtGridColumn(1),
            },
        };

        return new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
            RowSpacing = 12,
            Padding = new Thickness(18, 14, 18, 16),
            Children =
            {
                header,
                body.AtGridRow(1),
            },
        };
    }

    private async Task LoadReviewModelsAsync()
    {
        await Task.WhenAll(_agentEditors.Values.Select(editor => editor.LoadModelsAsync()));
    }

    private static View CreateDirectoryView()
    {
        var grid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(1),
            },
            Padding = new Thickness(8, 7),
            RowSpacing = 2,
        };
        var name = new Label
        {
            FontAttributes = FontAttributes.Bold,
            FontSize = 14,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        name.SetBinding(Label.TextProperty, nameof(LocalDirectoryRow.Name));
        var path = new Label
        {
            TextColor = UiColors.Muted,
            FontSize = 11,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        path.SetBinding(Label.TextProperty, nameof(LocalDirectoryRow.DisplayPath));
        path.SetBinding(Label.TextColorProperty, nameof(LocalDirectoryRow.PathColor));
        Grid.SetRow(path, 1);
        var divider = new BoxView { Color = UiColors.Border, HeightRequest = 1 };
        Grid.SetRow(divider, 2);
        grid.Children.Add(name);
        grid.Children.Add(path);
        grid.Children.Add(divider);
        return grid;
    }

    private void ReloadDirectories(string? selectPath = null)
    {
        var selectedPath = selectPath ?? _selected?.Path;
        _directories.Clear();
        foreach (var directory in _dashboard.LocalReviewDirectories)
        {
            _directories.Add(new LocalDirectoryRow(directory));
        }

        _emptyDirectoriesLabel.IsVisible = _directories.Count == 0;
        _directoryList.IsVisible = _directories.Count > 0;
        _selected = _directories.FirstOrDefault(row => string.Equals(
                row.Path,
                selectedPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            ?? _directories.FirstOrDefault();
        _directoryList.SelectedItem = _selected;
        UpdateSelectionActions();
    }

    private async void OnBrowseClicked(object? sender, EventArgs args)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*");
            var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (window is not null)
            {
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
            }

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                _pathEntry.Text = folder.Path;
                AddDirectory(folder.Path);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Choose project", ex.Message, "Close");
        }
    }

    private void OnAddClicked(object? sender, EventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(_pathEntry.Text))
        {
            AddDirectory(_pathEntry.Text);
        }
    }

    private async void AddDirectory(string path)
    {
        try
        {
            var update = _dashboard.AddLocalReviewDirectory(path);
            _pathEntry.Text = string.Empty;
            ReloadDirectories(update.Directory);
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            await DisplayAlertAsync("Add local project", ex.Message, "Close");
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        _selected = args.CurrentSelection.FirstOrDefault() as LocalDirectoryRow;
        _lastProgressFingerprint = null;
        RefreshProgress(force: true);
    }

    private void UpdateSelectionActions()
    {
        var selectedPath = _selected?.Path;
        var running = selectedPath is not null && _dashboard.IsLocalReviewRunning(selectedPath);
        var starting = selectedPath is not null && _startingPaths.Contains(selectedPath);
        var hasAgent = _agentEditors.Values.Any(editor => editor.IsSelected);
        _reviewButton.IsEnabled = _selected is { Exists: true } && !running && !starting && hasAgent;
        _removeButton.IsEnabled = _selected is not null && !running;
        _agentOptions.IsEnabled = !running && !starting;
        if (selectedPath is not null
            && _dashboard.GetLocalReviewProgress(selectedPath) is null)
        {
            _projectLabel.Text = _selected!.Name;
            _detailLabel.Text = selectedPath;
        }
    }

    private async void OnReviewClicked(object? sender, EventArgs args)
    {
        if (_selected is not { Exists: true } selected)
        {
            return;
        }

        var agents = _agentEditors.Values
            .Select(editor => editor.Settings)
            .ToArray();
        if (!agents.Any(agent => agent.Enabled) || !_startingPaths.Add(selected.Path))
        {
            return;
        }

        UpdateSelectionActions();
        try
        {
            await _dashboard.ReviewLocalDirectoryAsync(selected.Path, agents, _appCancellation);
            RefreshProgress(force: true);
        }
        catch (OperationCanceledException)
        {
            RefreshProgress(force: true);
        }
        catch (Exception ex)
        {
            RefreshProgress(force: true);
            var detail = _dashboard.GetLocalReviewProgress(selected.Path)?.Detail;
            await DisplayAlertAsync(
                "Local review",
                string.IsNullOrWhiteSpace(detail) ? ex.Message : detail,
                "Close");
        }
        finally
        {
            _startingPaths.Remove(selected.Path);
            UpdateSelectionActions();
        }
    }

    private async void OnRemoveClicked(object? sender, EventArgs args)
    {
        if (_selected is null)
        {
            return;
        }

        try
        {
            _dashboard.RemoveLocalReviewDirectory(_selected.Path);
            ReloadDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await DisplayAlertAsync("Remove local project", ex.Message, "Close");
        }
    }

    private void OnCancelClicked(object? sender, EventArgs args)
    {
        if (_selected is not null)
        {
            _dashboard.CancelLocalReview(_selected.Path);
            UpdateSelectionActions();
        }
    }

    private void OnOpenReportClicked(object? sender, EventArgs args)
    {
        var path = _selected is null
            ? null
            : _dashboard.GetLocalReviewProgress(_selected.Path)?.ReportPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
    }

    private void OnTimerTick(object? sender, EventArgs args)
    {
        RefreshProgress(force: false);
    }

    private void RefreshProgress(bool force)
    {
        var selectedPath = _selected?.Path;
        var progress = selectedPath is null
            ? null
            : _dashboard.GetLocalReviewProgress(selectedPath);
        var fingerprint = progress is null
            ? "none:" + selectedPath
            : string.Join(
                '|',
                progress.SourceDirectory,
                progress.Status,
                progress.CompletedAt?.UtcTicks,
                progress.FindingCount,
                progress.Detail,
                string.Join(',', progress.Stages.Select(stage => $"{stage.Agent}:{stage.Status}:{stage.AcceptedFindings}:{stage.Error}")));
        if (!force && string.Equals(fingerprint, _lastProgressFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _lastProgressFingerprint = fingerprint;
        _stages.Children.Clear();
        if (progress is null)
        {
            _activity.IsRunning = false;
            _activity.IsVisible = false;
            _statusLabel.Text = "Ready";
            _statusLabel.TextColor = UiColors.Muted;
            _projectLabel.Text = _selected?.Name ?? "Select a directory";
            _detailLabel.Text = selectedPath ?? string.Empty;
            _resultLabel.Text = string.Empty;
            _cancelButton.IsVisible = false;
            _openReportButton.IsVisible = false;
            UpdateSelectionActions();
            return;
        }

        var running = progress.Status == ReviewPipelineStatus.Running;
        _projectLabel.Text = progress.ProjectName;
        _activity.IsRunning = running;
        _activity.IsVisible = running;
        _statusLabel.Text = StatusText(progress.Status);
        _statusLabel.TextColor = UiColors.ForPipeline(progress.Status);
        _detailLabel.Text = progress.Detail ?? progress.SourceDirectory;
        var elapsed = (progress.CompletedAt ?? DateTimeOffset.UtcNow) - progress.StartedAt;
        _resultLabel.Text = $"{progress.FindingCount.ToString(CultureInfo.InvariantCulture)} issue(s)  |  {FormatDuration(elapsed)}";
        _cancelButton.IsVisible = running;
        _cancelButton.IsEnabled = running && _dashboard.IsLocalReviewRunning(progress.SourceDirectory);
        _openReportButton.IsVisible = !running
            && !string.IsNullOrWhiteSpace(progress.ReportPath)
            && File.Exists(progress.ReportPath);
        var timeline = BuildTimeline(progress);
        for (var index = 0; index < timeline.Count; index++)
        {
            _stages.Children.Add(TimelineView.CreateStep(
                timeline[index],
                isFirst: index == 0,
                isLast: index == timeline.Count - 1));
        }

        UpdateSelectionActions();
    }

    private static IReadOnlyList<JournalStep> BuildTimeline(LocalReviewProgress progress)
    {
        var timeline = new List<JournalStep>();
        var firstAgentStart = progress.Stages
            .Where(stage => stage.StartedAt is not null)
            .Select(stage => stage.StartedAt)
            .Min();
        var preparationStatus = firstAgentStart is not null
            ? JournalStepStatus.Completed
            : progress.Status == ReviewPipelineStatus.Running
                ? JournalStepStatus.Running
                : progress.Status == ReviewPipelineStatus.Canceled
                    ? JournalStepStatus.Canceled
                    : JournalStepStatus.Failed;
        timeline.Add(new JournalStep(
            "prepare",
            "Prepare project snapshot",
            firstAgentStart is not null ? "Isolated snapshot ready" : progress.Detail,
            preparationStatus,
            progress.StartedAt,
            firstAgentStart));
        timeline.AddRange(progress.Stages
            .OrderBy(stage => stage.Index)
            .Select(stage => TimelineView.FromAgent(stage, "Inspecting and testing the project snapshot")));

        var reportStatus = progress.Status switch
        {
            ReviewPipelineStatus.Completed or ReviewPipelineStatus.CompletedWithErrors => JournalStepStatus.Completed,
            ReviewPipelineStatus.Failed => JournalStepStatus.Failed,
            ReviewPipelineStatus.Canceled => JournalStepStatus.Canceled,
            _ => JournalStepStatus.Pending,
        };
        var reportStart = progress.Stages
            .Where(stage => stage.CompletedAt is not null)
            .Select(stage => stage.CompletedAt)
            .Max();
        timeline.Add(new JournalStep(
            "report",
            "Write review report",
            progress.Status == ReviewPipelineStatus.Running
                ? "Waiting for enabled agents"
                : progress.Detail,
            reportStatus,
            reportStatus == JournalStepStatus.Pending ? null : reportStart ?? progress.CompletedAt,
            progress.CompletedAt));
        return timeline;
    }

    private static string StatusText(ReviewPipelineStatus status) => status switch
    {
        ReviewPipelineStatus.CompletedWithErrors => "Completed with agent errors",
        ReviewPipelineStatus.Completed => "Completed",
        ReviewPipelineStatus.Failed => "Failed",
        ReviewPipelineStatus.Canceled => "Canceled",
        _ => "Running",
    };

    private static string FormatDuration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }
}

internal sealed class LocalDirectoryRow
{
    public LocalDirectoryRow(string path)
    {
        Path = path;
        Name = new DirectoryInfo(path).Name;
        Exists = Directory.Exists(path);
        DisplayPath = Exists ? path : path + "  (missing)";
        PathColor = Exists ? UiColors.Muted : UiColors.Danger;
    }

    public string Path { get; }

    public string Name { get; }

    public bool Exists { get; }

    public string DisplayPath { get; }

    public Color PathColor { get; }
}

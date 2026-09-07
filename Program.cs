using System.Diagnostics;
using System.Globalization;
using System.Collections.Concurrent;
using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Spectre.Console;
using Tui = Terminal.Gui;

Console.OutputEncoding = Encoding.UTF8;
TrySetConsoleTitle("PRs");
try
{
    Console.TreatControlCAsInput = false;
}
catch (IOException)
{
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    shutdown.Cancel();
};

try
{
    LegacyProtocolHandler.TryUnregister();

    var commandLine = CommandLine.Parse(args);
    if (commandLine.ShowHelp)
    {
        PrintUsage();
        return;
    }

    if (commandLine.Error is not null)
    {
        Console.Error.WriteLine(commandLine.Error);
        PrintUsage();
        Environment.ExitCode = 1;
        return;
    }

    var settings = AppSettings.Load();

    if (commandLine.RepositoriesToAdd.Count > 0)
    {
        var update = settings.AddRepositories(commandLine.RepositoriesToAdd);
        settings.Save();
        PrintRepositoryUpdate(update, settings.SettingsPath);

        if (!commandLine.HasRuntimeAction)
        {
            return;
        }
    }

    if (commandLine.PullRequests.Count > 0)
    {
        await PullRequests.OpenAsync(commandLine.PullRequests, settings.Repositories, shutdown.Token);
        return;
    }

    if (commandLine.LaunchUi)
    {
        if (!UiLauncher.TryLaunch(settings.SettingsPath, out var error))
        {
            Console.Error.WriteLine(error);
            Environment.ExitCode = 1;
        }

        return;
    }

    using var singleInstance = SingleInstanceLease.AcquireDashboard(settings.SettingsPath);

    if (commandLine.PrintOnce)
    {
        await PrintOnceAsync(settings, shutdown.Token);
        return;
    }

    if (commandLine.CleanupOnce)
    {
        if (settings.Repositories.Count == 0
            && settings.IgnoredPullRequests.Count == 0
            && settings.TopPullRequests.Count == 0)
        {
            Console.WriteLine("No tracked repositories. Add one with: pr https://github.com/OWNER/REPO");
            return;
        }

        var result = await new NotificationCleaner(
            settings.Repositories,
            settings.IgnoredPullRequests,
            settings.TopPullRequests).CleanupAsync(shutdown.Token);
        var ignoredChanged = settings.RemoveIgnoredPullRequests(result.RemovedIgnoredPullRequests);
        var topChanged = settings.RemoveTopPullRequests(result.RemovedTopPullRequests);
        if (ignoredChanged || topChanged)
        {
            settings.Save();
        }

        Console.WriteLine(
            $"Scanned {result.Scanned} unread notification(s) from tracked repos; "
            + $"marked {result.MarkedRead} as read; "
            + $"removed {result.RemovedIgnoredPullRequests.Count} closed ignored PR(s) and "
            + $"{result.RemovedTopPullRequests.Count} closed top PR(s).");
        return;
    }

    await new DashboardApp(settings).RunAsync(shutdown.Token);
}
catch (OperationCanceledException)
{
}
catch (SingleInstanceException ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  pr");
    Console.Error.WriteLine("  pr https://github.com/OWNER/REPO");
    Console.Error.WriteLine("  pr OWNER/REPO");
    Console.Error.WriteLine("  pr <pull-request-number> [pull-request-number...]");
    Console.Error.WriteLine("  pr OWNER/REPO#<pull-request-number>");
    Console.Error.WriteLine("  pr https://github.com/OWNER/REPO/pull/<pull-request-number>");
    Console.Error.WriteLine("  pr ui");
    Console.Error.WriteLine("  pr --once");
    Console.Error.WriteLine("  pr --cleanup-once");
}

static void TrySetConsoleTitle(string title)
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    try
    {
        Console.Title = title;
    }
    catch (IOException)
    {
    }
    catch (PlatformNotSupportedException)
    {
    }
}

static void PrintRepositoryUpdate(SettingsUpdateResult update, string settingsPath)
{
    foreach (var repo in update.Added)
    {
        Console.WriteLine($"Added tracked repository: {repo.Url}");
    }

    foreach (var repo in update.AlreadyTracked)
    {
        Console.WriteLine($"Already tracked: {repo.Url}");
    }

    Console.WriteLine($"Settings: {settingsPath}");
}

static async Task PrintOnceAsync(AppSettings settings, CancellationToken cancellationToken)
{
    var result = await new GhClient().FetchPullRequestsAsync(
        settings.Repositories,
        settings.RequiredApprovals,
        settings.IgnoredPullRequestKeys,
        settings.Priority,
        cancellationToken);

    var excludedAuthor = result.ExcludedAuthor is null ? "" : $" excluding @{result.ExcludedAuthor}";
    var title = $"PRs below {settings.RequiredApprovals} approvals{excludedAuthor}: {result.Items.Count}/{result.OpenNonDraftCount}";
    var table = new Table()
        .Border(TableBorder.Rounded)
        .Title(title);

    table.AddColumn("Repo");
    table.AddColumn("PR");
    table.AddColumn("Title");
    table.AddColumn("Author");
    table.AddColumn("Created");
    table.AddColumn("Approvals");

    if (settings.Repositories.Count == 0)
    {
        table.AddRow(
            "",
            "",
            "No tracked repositories. Add one with: pr https://github.com/OWNER/REPO",
            "",
            "",
            "");
    }
    else if (result.Items.Count == 0)
    {
        table.AddRow(
            "",
            "",
            settings.IgnoredPullRequests.Count == 0
                ? $"No open non-draft PRs from other authors are currently below {settings.RequiredApprovals} approvals."
                : $"No visible open non-draft PRs from other authors are currently below {settings.RequiredApprovals} approvals. Ignored PRs are hidden.",
            "",
            "",
            "");
    }
    else
    {
        foreach (var pr in result.Items)
        {
            table.AddRow(
                Markup.Escape(pr.Repository.Name),
                PriorityCell($"#{pr.Number}", pr.Priority),
                LinkCell(pr.Title, pr.Url),
                Markup.Escape("@" + pr.Author),
                pr.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                $"{pr.ApprovalCount}/{settings.RequiredApprovals}");
        }
    }

    AnsiConsole.Write(table);

    static string LinkCell(string value, string url)
    {
        return $"[link={url}][underline blue]{Markup.Escape(value)}[/][/]";
    }

    static string PriorityCell(string value, PullRequestPriority priority)
    {
        var color = priority.Heat switch
        {
            PullRequestHeat.SuperHot => "red",
            PullRequestHeat.Hot => "yellow",
            _ => "green",
        };

        return $"[{color}]{Markup.Escape(value)}[/]";
    }
}

internal sealed class DashboardApp
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailedRefreshRetryInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailedCleanupRetryInterval = TimeSpan.FromMinutes(15);
    private static readonly IReadOnlySet<string> NoIgnoredPullRequestKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private const int RepoColumn = 0;
    private const int PullRequestColumn = 1;
    private const int TitleColumn = 2;
    private const int AuthorColumn = 3;
    private const int CreatedColumn = 4;
    private const int ApprovalColumn = 5;
    private const int IgnoreColumn = 6;

    private readonly AppSettings _settings;
    private IReadOnlyList<RepositoryRef> _repositories;
    private readonly int _requiredApprovals;
    private readonly string _settingsPath;
    private readonly GhClient _client = new();
    private readonly DashboardActivityJournal _activityJournal;
    private readonly CodexReviewWatcher _codexReviewWatcher;
    private readonly LocalReviewCoordinator _localReviewCoordinator;
    private IReadOnlyList<PullRequestInfo> _items = [];
    private IReadOnlyDictionary<string, PullRequestInfo> _recentlyAcknowledgedCodexPullRequests
        = new Dictionary<string, PullRequestInfo>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PullRequestPriority> _lastSuccessfulPriorities = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _ignoredPullRequestKeys;
    private HashSet<string> _topPullRequestKeys;
    private HashSet<string>? _knownPullRequestKeys;
    private bool _showIgnoredPullRequests;
    private DateTimeOffset? _lastRefresh;
    private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastCleanup;
    private DateTimeOffset _nextCleanup;
    private string? _error;
    private string? _cleanupError;
    private string? _currentUserLogin;
    private int _apiCalls;
    private int _openNonDraftCount;
    private int _lastCleanupScanned;
    private int _lastCleanupMarkedRead;
    private int _lastCleanupRemovedIgnored;
    private int _ignoredPullRequestCount;
    private DateTime _settingsLastWriteUtc;
    private CancellationTokenSource? _appCancellation;
    private DashboardView? _dashboard;
    private Action? _externalStateChanged;
    private bool _isRefreshing;
    private bool _isCleaning;
    private int _refreshRequested;
    private int _cleanupRequested;
    private CodexReviewSnapshot _codexReviewSnapshot = CodexReviewSnapshot.Disabled;

    public DashboardApp(AppSettings settings)
    {
        _settings = settings;
        _repositories = settings.Repositories.ToArray();
        _requiredApprovals = settings.RequiredApprovals;
        _settingsPath = settings.SettingsPath;
        _ignoredPullRequestKeys = settings.IgnoredPullRequestKeys;
        _topPullRequestKeys = settings.TopPullRequestKeys;
        _ignoredPullRequestCount = settings.IgnoredPullRequests.Count;
        _settingsLastWriteUtc = GetSettingsLastWriteUtc();
        _nextCleanup = DateTimeOffset.UtcNow.AddSeconds(20);
        _activityJournal = new DashboardActivityJournal(_settingsPath, settings.CodexReview);
        _codexReviewWatcher = new CodexReviewWatcher(
            _repositories,
            _settingsPath,
            () => _settings.CodexReview,
            () => _ignoredPullRequestKeys,
            ApplyCodexReviewSnapshot);
        _codexReviewSnapshot = _codexReviewWatcher.Snapshot;
        _localReviewCoordinator = new LocalReviewCoordinator(
            _settingsPath,
            settings.CodexReview,
            NotifyExternalStateChanged);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Tui.Application.Init();
        using var appCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _appCancellation = appCancellation;
        var dashboard = new DashboardView(this);
        _dashboard = dashboard;

        try
        {
            Tui.Application.Driver.SetCursorVisibility(Tui.CursorVisibility.Invisible);
            dashboard.Build(Tui.Application.Top);
            _isRefreshing = _repositories.Count > 0;
            _isCleaning = false;
            dashboard.Refresh(_isRefreshing, _isCleaning);

            var worker = RunBackgroundLoopAsync(
                (isRefreshing, isCleaning) => RefreshOnUi(dashboard, isRefreshing, isCleaning),
                appCancellation.Token);
            var codexReviewWorker = _codexReviewWatcher.RunAsync(appCancellation.Token);
            using var cancelRegistration = cancellationToken.Register(RequestStop);

            Tui.Application.Run();
            appCancellation.Cancel();

            try
            {
                await Task.WhenAll(worker, codexReviewWorker);
            }
            catch (OperationCanceledException) when (appCancellation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            _appCancellation = null;
            _dashboard = null;
            Tui.Application.Shutdown();
        }
    }

    internal async Task RunHeadlessAsync(Action stateChanged, CancellationToken cancellationToken)
    {
        using var appCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _appCancellation = appCancellation;
        _externalStateChanged = stateChanged;
        _isRefreshing = _repositories.Count > 0;
        _isCleaning = false;
        NotifyExternalStateChanged();
        try
        {
            var worker = RunBackgroundLoopAsync(
                (_, _) => NotifyExternalStateChanged(),
                appCancellation.Token);
            var reviewWorker = _codexReviewWatcher.RunAsync(appCancellation.Token);
            await Task.WhenAll(worker, reviewWorker);
        }
        catch (OperationCanceledException) when (appCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _externalStateChanged = null;
            _appCancellation = null;
        }
    }

    private async Task RunBackgroundLoopAsync(
        Action<bool, bool> stateChanged,
        CancellationToken cancellationToken)
    {
        Task<FetchResult>? refresh = null;
        Task<FetchResult>? priorityRefresh = null;
        Task<CleanupResult>? cleanup = null;
        var refreshIsFirstPageOnly = false;
        string? refreshJournalId = null;
        string? cleanupJournalId = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var startedWork = false;

            if (refresh is null
                && priorityRefresh is null
                && Interlocked.Exchange(ref _refreshRequested, 0) != 0)
            {
                _nextRefresh = DateTimeOffset.MinValue;
            }

            if (cleanup is null
                && Interlocked.Exchange(ref _cleanupRequested, 0) != 0)
            {
                _nextCleanup = DateTimeOffset.MinValue;
            }

            if (refresh is null && priorityRefresh is null && DateTimeOffset.UtcNow >= _nextRefresh)
            {
                var firstPaintOnly = _lastRefresh is null;
                refreshJournalId = _activityJournal.Start(
                    JournalOperationKind.Refresh,
                    "Refresh pull requests",
                    _repositories.Count == 1
                        ? _repositories[0].FullName
                        : $"{_repositories.Count.ToString(CultureInfo.InvariantCulture)} tracked repositories",
                    [
                        ("fetch", "Query GitHub"),
                        ("priority", "Calculate urgency"),
                        ("apply", "Apply dashboard update"),
                    ],
                    firstStepKey: "fetch",
                    firstStepDetail: firstPaintOnly
                        ? "Loading the first page for a fast initial view"
                        : "Loading open pull requests");
                refresh = _client.FetchPullRequestsAsync(
                    _repositories,
                    _requiredApprovals,
                    NoIgnoredPullRequestKeys,
                    _settings.Priority,
                    cancellationToken,
                    includePriorityDetails: false,
                    maxPages: firstPaintOnly ? 1 : GhClient.MaxSearchPages);
                refreshIsFirstPageOnly = firstPaintOnly;
                _error = null;
                startedWork = true;
            }

            if ((_repositories.Count > 0
                    || _settings.IgnoredPullRequests.Count > 0
                    || _settings.TopPullRequests.Count > 0)
                && cleanup is null
                && DateTimeOffset.UtcNow >= _nextCleanup)
            {
                cleanupJournalId = _activityJournal.Start(
                    JournalOperationKind.Cleanup,
                    "Clean notifications and saved PRs",
                    null,
                    [
                        ("scan", "Scan GitHub notifications"),
                        ("apply", "Update saved PR state"),
                    ],
                    firstStepKey: "scan",
                    firstStepDetail: "Checking tracked repositories");
                cleanup = new NotificationCleaner(
                    _repositories,
                    _settings.IgnoredPullRequests,
                    _settings.TopPullRequests).CleanupAsync(cancellationToken);
                startedWork = true;
            }

            if (startedWork)
            {
                PublishStateChanged(stateChanged, refresh is not null || priorityRefresh is not null, cleanup is not null);
            }

            if (refresh is not null && refresh.IsCompleted)
            {
                try
                {
                    var result = await refresh;
                    var wasFirstPageOnly = refreshIsFirstPageOnly;
                    ApplyFetchResult(result, dingOnNewItems: !result.IsPartial, preserveExistingPriorities: true);
                    if (result.IsPartial && wasFirstPageOnly)
                    {
                        _activityJournal.UpdateStep(
                            refreshJournalId,
                            "fetch",
                            JournalStepStatus.Running,
                            $"First page loaded; fetching the remaining {result.OpenNonDraftCount.ToString(CultureInfo.InvariantCulture)} open pull request(s)");
                        refresh = _client.FetchPullRequestsAsync(
                            _repositories,
                            _requiredApprovals,
                            NoIgnoredPullRequestKeys,
                            _settings.Priority,
                            cancellationToken,
                            includePriorityDetails: false,
                            maxPages: GhClient.MaxSearchPages);
                        refreshIsFirstPageOnly = false;
                    }
                    else
                    {
                        _activityJournal.UpdateStep(
                            refreshJournalId,
                            "fetch",
                            JournalStepStatus.Completed,
                            $"Loaded {result.OpenNonDraftCount.ToString(CultureInfo.InvariantCulture)} open pull request(s) with {result.ApiCalls.ToString(CultureInfo.InvariantCulture)} GitHub request(s)");
                        _activityJournal.UpdateStep(
                            refreshJournalId,
                            "priority",
                            JournalStepStatus.Running,
                            "Loading discussion and review activity without blocking the table");
                        priorityRefresh = _client.EnrichPullRequestPrioritiesAsync(
                            new FetchResult(_items, _openNonDraftCount, _apiCalls, _repositories.Count, _currentUserLogin, IsPartial: false),
                            _settings.Priority,
                            cancellationToken);
                        refresh = null;
                        refreshIsFirstPageOnly = false;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _error = ex.Message;
                    _nextRefresh = DateTimeOffset.UtcNow.Add(FailedRefreshRetryInterval);
                    _activityJournal.UpdateStep(
                        refreshJournalId,
                        "fetch",
                        JournalStepStatus.Failed,
                        ex.Message);
                    _activityJournal.Finish(refreshJournalId, JournalOperationStatus.Failed);
                    refreshJournalId = null;
                    refresh = null;
                    refreshIsFirstPageOnly = false;
                }

                PublishStateChanged(stateChanged, refresh is not null || priorityRefresh is not null, cleanup is not null);
            }

            if (priorityRefresh is not null && priorityRefresh.IsCompleted)
            {
                var priorityLoaded = false;
                try
                {
                    var result = await priorityRefresh;
                    _activityJournal.UpdateStep(
                        refreshJournalId,
                        "priority",
                        JournalStepStatus.Completed,
                        $"Urgency updated for {result.Items.Count.ToString(CultureInfo.InvariantCulture)} pull request(s)");
                    priorityLoaded = true;
                    _activityJournal.UpdateStep(
                        refreshJournalId,
                        "apply",
                        JournalStepStatus.Running,
                        "Publishing the completed snapshot");
                    ApplyFetchResult(result, dingOnNewItems: false, updateSuccessfulPriorities: true);
                    _activityJournal.UpdateStep(
                        refreshJournalId,
                        "apply",
                        JournalStepStatus.Completed,
                        "Dashboard is up to date");
                    _activityJournal.Finish(refreshJournalId, JournalOperationStatus.Completed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _error = $"priority refresh failed: {ex.Message}";
                    _nextRefresh = DateTimeOffset.UtcNow.Add(FailedRefreshRetryInterval);
                    _activityJournal.UpdateStep(
                        refreshJournalId,
                        priorityLoaded ? "apply" : "priority",
                        JournalStepStatus.Failed,
                        ex.Message);
                    if (!priorityLoaded)
                    {
                        _activityJournal.UpdateStep(
                            refreshJournalId,
                            "apply",
                            JournalStepStatus.Completed,
                            "Kept the last successful urgency values");
                    }

                    _activityJournal.Finish(
                        refreshJournalId,
                        priorityLoaded
                            ? JournalOperationStatus.Failed
                            : JournalOperationStatus.CompletedWithErrors);
                }

                priorityRefresh = null;
                refreshJournalId = null;
                PublishStateChanged(stateChanged, refresh is not null, cleanup is not null);
            }

            if (cleanup is not null && cleanup.IsCompleted)
            {
                var scanCompleted = false;
                try
                {
                    var result = await cleanup;
                    _activityJournal.UpdateStep(
                        cleanupJournalId,
                        "scan",
                        JournalStepStatus.Completed,
                        $"Scanned {result.Scanned.ToString(CultureInfo.InvariantCulture)} notification(s); marked {result.MarkedRead.ToString(CultureInfo.InvariantCulture)} read");
                    scanCompleted = true;
                    _activityJournal.UpdateStep(
                        cleanupJournalId,
                        "apply",
                        JournalStepStatus.Running,
                        "Removing closed ignored and Top entries");
                    ApplyCleanupResult(result);
                    _activityJournal.UpdateStep(
                        cleanupJournalId,
                        "apply",
                        JournalStepStatus.Completed,
                        $"Removed {result.RemovedIgnoredPullRequests.Count.ToString(CultureInfo.InvariantCulture)} ignored and {result.RemovedTopPullRequests.Count.ToString(CultureInfo.InvariantCulture)} Top entry or entries");
                    _activityJournal.Finish(cleanupJournalId, JournalOperationStatus.Completed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _cleanupError = ex.Message;
                    _nextCleanup = DateTimeOffset.UtcNow.Add(FailedCleanupRetryInterval);
                    _activityJournal.UpdateStep(
                        cleanupJournalId,
                        scanCompleted ? "apply" : "scan",
                        JournalStepStatus.Failed,
                        ex.Message);
                    _activityJournal.Finish(cleanupJournalId, JournalOperationStatus.Failed);
                }

                cleanup = null;
                cleanupJournalId = null;
                PublishStateChanged(stateChanged, refresh is not null || priorityRefresh is not null, isCleaning: false);
            }

            if (ReloadSettingsIfChanged())
            {
                PublishStateChanged(stateChanged, refresh is not null || priorityRefresh is not null, cleanup is not null);
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    private static void RequestStop()
    {
        try
        {
            Tui.Application.RequestStop();
        }
        catch
        {
        }
    }

    private void Quit()
    {
        try
        {
            _appCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        RequestStop();
    }

    private static void RefreshOnUi(DashboardView dashboard, bool isRefreshing, bool isCleaning)
    {
        try
        {
            Tui.Application.MainLoop.Invoke(() => dashboard.Refresh(isRefreshing, isCleaning));
        }
        catch
        {
        }
    }

    private void PublishStateChanged(Action<bool, bool> stateChanged, bool isRefreshing, bool isCleaning)
    {
        _isRefreshing = isRefreshing;
        _isCleaning = isCleaning;
        stateChanged(isRefreshing, isCleaning);
    }

    private void NotifyExternalStateChanged()
    {
        try
        {
            _externalStateChanged?.Invoke();
        }
        catch
        {
        }
    }

    private void ApplyFetchResult(
        FetchResult result,
        bool dingOnNewItems = true,
        bool preserveExistingPriorities = false,
        bool updateSuccessfulPriorities = false)
    {
        var items = preserveExistingPriorities
            ? PreserveExistingPriorities(result.Items)
            : result.Items;

        if (dingOnNewItems)
        {
            DingOnNewItems(items);
        }

        _items = items;
        if (!result.IsPartial && !updateSuccessfulPriorities)
        {
            _recentlyAcknowledgedCodexPullRequests = new Dictionary<string, PullRequestInfo>(StringComparer.OrdinalIgnoreCase);
        }
        _lastRefresh = DateTimeOffset.UtcNow;
        _nextRefresh = _lastRefresh.Value.Add(RefreshInterval);
        _apiCalls = result.ApiCalls;
        _openNonDraftCount = result.OpenNonDraftCount;
        _currentUserLogin = result.ExcludedAuthor;
        _error = null;

        if (updateSuccessfulPriorities)
        {
            _lastSuccessfulPriorities = items.ToDictionary(item => item.Key, item => item.Priority, StringComparer.OrdinalIgnoreCase);
        }
    }

    private IReadOnlyList<PullRequestInfo> PreserveExistingPriorities(IReadOnlyList<PullRequestInfo> items)
    {
        if (_lastSuccessfulPriorities.Count == 0 || items.Count == 0)
        {
            return items;
        }

        return items
            .Select(item => _lastSuccessfulPriorities.TryGetValue(item.Key, out var priority)
                ? item with { Priority = priority }
                : item)
            .ToArray();
    }

    private void ApplyCleanupResult(CleanupResult result)
    {
        var ignoredChanged = _settings.RemoveIgnoredPullRequests(result.RemovedIgnoredPullRequests);
        var topChanged = _settings.RemoveTopPullRequests(result.RemovedTopPullRequests);
        if (ignoredChanged || topChanged)
        {
            _settings.Save();
            _settingsLastWriteUtc = GetSettingsLastWriteUtc();
            if (ignoredChanged)
            {
                RefreshIgnoredPullRequests();
            }

            if (topChanged)
            {
                RefreshTopPullRequests();
            }
        }

        _lastCleanup = DateTimeOffset.UtcNow;
        _nextCleanup = _lastCleanup.Value.Add(CleanupInterval);
        _lastCleanupScanned = result.Scanned;
        _lastCleanupMarkedRead = result.MarkedRead;
        _lastCleanupRemovedIgnored = result.RemovedIgnoredPullRequests.Count;
        _cleanupError = null;
    }

    private void RefreshIgnoredPullRequests()
    {
        _ignoredPullRequestKeys = _settings.IgnoredPullRequestKeys;
        _ignoredPullRequestCount = _settings.IgnoredPullRequests.Count;
    }

    private void DingOnNewItems(IReadOnlyList<PullRequestInfo> items)
    {
        var latestKeys = items
            .Where(item => !IsIgnored(item))
            .Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_knownPullRequestKeys is not null && latestKeys.Any(key => !_knownPullRequestKeys.Contains(key)))
        {
            NotificationSound.Ding();
        }

        _knownPullRequestKeys = latestKeys;
    }

    private bool ReloadSettingsIfChanged()
    {
        var lastWriteUtc = GetSettingsLastWriteUtc();
        if (lastWriteUtc == _settingsLastWriteUtc)
        {
            return false;
        }

        _settingsLastWriteUtc = lastWriteUtc;
        var latest = AppSettings.Load();
        var changed = false;
        if (_settings.ReplaceIgnoredPullRequests(latest.IgnoredPullRequests))
        {
            RefreshIgnoredPullRequests();
            changed = true;
        }

        if (_settings.ReplaceTopPullRequests(latest.TopPullRequests))
        {
            RefreshTopPullRequests();
            changed = true;
        }

        if (_settings.ReplaceLocalReviewDirectories(latest.LocalReviewDirectories))
        {
            changed = true;
        }

        if (_settings.ReplaceCodexReview(latest.CodexReview))
        {
            _codexReviewWatcher.Wake();
            changed = true;
        }

        if (_settings.ReplacePriority(latest.Priority))
        {
            RequestRefresh();
            changed = true;
        }

        return changed;
    }

    private IReadOnlyList<PullRequestInfo> GetVisiblePullRequests(string searchText = "")
    {
        var search = searchText.Trim();
        var includeIgnored = search.Length > 0 || _showIgnoredPullRequests;
        var pendingReviewedKeys = _codexReviewSnapshot.PendingReviewedPullRequests
            .Select(pullRequest => pullRequest.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allItems = new List<PullRequestInfo>(
            _items.Count + pendingReviewedKeys.Count + _recentlyAcknowledgedCodexPullRequests.Count);
        var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _items)
        {
            if (knownKeys.Add(item.Key))
            {
                allItems.Add(item);
            }
        }

        foreach (var item in _recentlyAcknowledgedCodexPullRequests.Values)
        {
            if (knownKeys.Add(item.Key))
            {
                allItems.Add(item);
            }
        }

        foreach (var reviewed in _codexReviewSnapshot.PendingReviewedPullRequests)
        {
            if (knownKeys.Add(reviewed.Key))
            {
                allItems.Add(reviewed.ToPullRequestInfo(
                    _settings.Priority,
                    _currentUserLogin,
                    DateTimeOffset.UtcNow));
            }
        }

        var source = includeIgnored
            ? allItems
            : allItems.Where(item => !IsIgnored(item) || pendingReviewedKeys.Contains(item.Key));
        var filtered = search.Length == 0
            ? source.ToArray()
            : source
                .Where(item => item.MatchesSearch(search))
                .ToArray();

        return SortTopFirst(filtered);
    }

    internal DashboardSnapshot GetSnapshot(string searchText = "")
    {
        var items = GetVisiblePullRequests(searchText)
            .Select(pullRequest => new DashboardPullRequest(
                pullRequest,
                IsIgnored(pullRequest),
                IsTop(pullRequest),
                IsCodexReviewed(pullRequest),
                IsCodexReviewQueued(pullRequest),
                CodexReviewFor(pullRequest)))
            .ToArray();
        return new DashboardSnapshot(
            Items: items,
            Repositories: _repositories.ToArray(),
            RequiredApprovals: _requiredApprovals,
            RepositorySummary: RepositorySummary(),
            EmptyMessage: EmptyListMessage(),
            CurrentUserLogin: _currentUserLogin,
            IsRefreshing: _isRefreshing,
            IsCleaning: _isCleaning,
            ShowIgnoredPullRequests: _showIgnoredPullRequests,
            OpenNonDraftCount: _openNonDraftCount,
            IgnoredPullRequestCount: _ignoredPullRequestCount,
            ApiCalls: _apiCalls,
            LastRefresh: _lastRefresh,
            LastCleanup: _lastCleanup,
            LastCleanupScanned: _lastCleanupScanned,
            LastCleanupMarkedRead: _lastCleanupMarkedRead,
            LastCleanupRemovedIgnored: _lastCleanupRemovedIgnored,
            Error: _error,
            CleanupError: _cleanupError,
            CanCleanup: CanCleanup(),
            Review: _codexReviewSnapshot,
            ReviewSettings: _settings.CodexReview,
            Journal: _activityJournal.Snapshot(),
            SettingsPath: _settingsPath);
    }

    private IReadOnlyList<PullRequestInfo> SortTopFirst(IReadOnlyList<PullRequestInfo> source)
    {
        if (source.Count == 0)
        {
            return source;
        }

        var byKey = source.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        var reviewedKeys = _codexReviewSnapshot.PendingReviewedPullRequests
            .Select(pullRequest => pullRequest.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reviewed = _codexReviewSnapshot.PendingReviewedPullRequests
            .Select(pullRequest => pullRequest.Key)
            .Where(key => byKey.ContainsKey(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key => byKey[key]);
        var topKeys = _topPullRequestKeys;
        var top = _settings.TopPullRequests
            .Select(pr => pr.Key)
            .Where(key => byKey.ContainsKey(key) && !reviewedKeys.Contains(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key => byKey[key]);
        var rest = source
            .Where(item => !reviewedKeys.Contains(item.Key) && !topKeys.Contains(item.Key));

        return reviewed.Concat(top).Concat(rest)
            .ToArray();
    }

    private bool IsIgnored(PullRequestInfo pullRequest)
    {
        return _ignoredPullRequestKeys.Contains(pullRequest.Key);
    }

    private bool IsTop(PullRequestInfo pullRequest)
    {
        return _topPullRequestKeys.Contains(pullRequest.Key);
    }

    private bool IsCodexReviewed(PullRequestInfo pullRequest)
    {
        return _codexReviewSnapshot.PendingReviewedPullRequests.Any(
            reviewed => string.Equals(reviewed.Key, pullRequest.Key, StringComparison.OrdinalIgnoreCase));
    }

    private CodexReviewedPullRequest? CodexReviewFor(PullRequestInfo pullRequest)
    {
        return _codexReviewSnapshot.PendingReviewedPullRequests.FirstOrDefault(
            reviewed => string.Equals(reviewed.Key, pullRequest.Key, StringComparison.OrdinalIgnoreCase));
    }

    internal bool ToggleIgnored(PullRequestInfo pullRequest)
    {
        if (IsIgnored(pullRequest))
        {
            _settings.RemoveIgnoredPullRequests([IgnoredPullRequest.From(pullRequest)]);
        }
        else if (!_settings.AddIgnoredPullRequest(IgnoredPullRequest.From(pullRequest)))
        {
            return false;
        }

        _settings.Save();
        _settingsLastWriteUtc = GetSettingsLastWriteUtc();
        RefreshIgnoredPullRequests();
        _codexReviewWatcher.Wake();
        NotifyExternalStateChanged();
        return true;
    }

    internal bool ToggleTop(PullRequestInfo pullRequest)
    {
        if (IsTop(pullRequest))
        {
            _settings.RemoveTopPullRequests([TopPullRequest.From(pullRequest)]);
        }
        else if (!_settings.AddTopPullRequest(TopPullRequest.From(pullRequest)))
        {
            return false;
        }

        _settings.Save();
        _settingsLastWriteUtc = GetSettingsLastWriteUtc();
        RefreshTopPullRequests();
        NotifyExternalStateChanged();
        return true;
    }

    private void RefreshTopPullRequests()
    {
        _topPullRequestKeys = _settings.TopPullRequestKeys;
    }

    private bool ToggleCodexReview()
    {
        return SetCodexReviewEnabled(!_settings.CodexReview.Enabled);
    }

    internal bool SetCodexReviewEnabled(bool enabled)
    {
        if (!_settings.SetCodexReviewEnabled(enabled))
        {
            return false;
        }

        _settings.Save();
        _settingsLastWriteUtc = GetSettingsLastWriteUtc();
        _codexReviewSnapshot = _codexReviewSnapshot with
        {
            Enabled = enabled,
            Message = enabled
                ? $"{_settings.CodexReview.AgentDescriptor} review starting"
                : $"{_settings.CodexReview.AgentDescriptor} review off",
        };
        _codexReviewWatcher.Wake();
        NotifyExternalStateChanged();
        return true;
    }

    private bool ToggleCodexReviewAutoSubmit()
    {
        return SetCodexReviewAutoSubmit(!_settings.CodexReview.AutoSubmit);
    }

    internal bool SetCodexReviewAutoSubmit(bool autoSubmit)
    {
        if (!_settings.SetCodexReviewAutoSubmit(autoSubmit))
        {
            return false;
        }

        _settings.Save();
        _settingsLastWriteUtc = GetSettingsLastWriteUtc();
        _codexReviewSnapshot = _codexReviewSnapshot with
        {
            Message = autoSubmit
                ? $"{_settings.CodexReview.AgentDescriptor} auto-send on"
                : $"{_settings.CodexReview.AgentDescriptor} drafts only",
        };
        _codexReviewWatcher.Wake();
        NotifyExternalStateChanged();
        return true;
    }

    internal bool SetCodexReviewAgents(IReadOnlyCollection<ReviewAgentSettings> agents)
    {
        var settings = _settings.CodexReview.WithAgents(agents);
        if (!_settings.ReplaceCodexReview(settings))
        {
            return false;
        }

        _settings.Save();
        _settingsLastWriteUtc = GetSettingsLastWriteUtc();
        _codexReviewSnapshot = _codexReviewSnapshot with
        {
            Message = settings.EnabledAgents.Count == 0
                ? "Review pipeline has no enabled agents"
                : $"{settings.AgentDescriptor} pipeline updated",
        };
        _codexReviewWatcher.Wake();
        NotifyExternalStateChanged();
        return true;
    }

    internal bool AcknowledgeCodexReview(PullRequestInfo pullRequest)
    {
        if (!_codexReviewWatcher.Acknowledge(pullRequest.Key))
        {
            return false;
        }

        _recentlyAcknowledgedCodexPullRequests = new Dictionary<string, PullRequestInfo>(
            _recentlyAcknowledgedCodexPullRequests,
            StringComparer.OrdinalIgnoreCase)
        {
            [pullRequest.Key] = pullRequest,
        };
        NotifyExternalStateChanged();
        return true;
    }

    private bool IsCodexReviewQueued(PullRequestInfo pullRequest)
    {
        return _codexReviewWatcher.IsManuallyQueued(pullRequest.Key);
    }

    internal CodexReviewEnqueueResult EnqueueCodexReview(PullRequestInfo pullRequest)
    {
        return _codexReviewWatcher.Enqueue(pullRequest);
    }

    private void ApplyCodexReviewSnapshot(CodexReviewSnapshot snapshot)
    {
        _activityJournal.ObserveReview(snapshot.PipelineProgress);
        _codexReviewSnapshot = snapshot;
        NotifyExternalStateChanged();
        var dashboard = _dashboard;
        if (dashboard is null)
        {
            return;
        }

        try
        {
            Tui.Application.MainLoop.Invoke(dashboard.RefreshCurrent);
        }
        catch
        {
        }
    }

    private bool ToggleIgnoredVisibility()
    {
        _showIgnoredPullRequests = !_showIgnoredPullRequests;
        return true;
    }

    internal bool SetIgnoredVisibility(bool showIgnored)
    {
        if (_showIgnoredPullRequests == showIgnored)
        {
            return false;
        }

        _showIgnoredPullRequests = showIgnored;
        NotifyExternalStateChanged();
        return true;
    }

    internal void RequestRefresh()
    {
        Interlocked.Exchange(ref _refreshRequested, 1);
        _error = null;
    }

    internal void RequestCleanup()
    {
        Interlocked.Exchange(ref _cleanupRequested, 1);
        _cleanupError = null;
    }

    private bool CanCleanup()
    {
        return _repositories.Count > 0
            || _settings.IgnoredPullRequests.Count > 0
            || _settings.TopPullRequests.Count > 0;
    }

    internal Task<WeeklyStats> FetchWeeklyStatsAsync(CancellationToken cancellationToken)
    {
        return _client.FetchWeeklyStatsAsync(_repositories, cancellationToken);
    }

    internal SettingsUpdateResult AddRepositories(IReadOnlyList<RepositoryRef> repositories)
    {
        var update = _settings.AddRepositories(repositories);
        if (update.Added.Count == 0)
        {
            return update;
        }

        _settings.Save();
        _settingsLastWriteUtc = GetSettingsLastWriteUtc();
        _repositories = _settings.Repositories.ToArray();
        _codexReviewWatcher.ReplaceRepositories(_repositories);
        RequestRefresh();
        NotifyExternalStateChanged();
        return update;
    }

    internal IReadOnlyList<string> LocalReviewDirectories => _settings.LocalReviewDirectories.ToArray();

    internal IReadOnlyList<ReviewAgentSettings> LocalReviewAgents => _settings.CodexReview.AvailableAgents;

    internal IReadOnlyList<ReviewAgentSettings> ReviewAgents => _settings.CodexReview.AvailableAgents;

    internal LocalReviewProgress? GetLocalReviewProgress(string directory)
    {
        return _localReviewCoordinator.GetProgress(directory);
    }

    internal bool IsLocalReviewRunning(string directory)
    {
        return _localReviewCoordinator.IsRunning(directory);
    }

    internal bool CancelLocalReview(string directory)
    {
        return _localReviewCoordinator.Cancel(directory);
    }

    internal LocalDirectorySettingsUpdate AddLocalReviewDirectory(string directory)
    {
        var normalized = AppSettings.ResolveLocalReviewDirectory(_settingsPath, directory);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"Local review directory was not found: {normalized}");
        }

        var update = _settings.AddLocalReviewDirectory(normalized);
        if (!update.Added)
        {
            return update;
        }

        _settings.Save();
        _settingsLastWriteUtc = GetSettingsLastWriteUtc();
        NotifyExternalStateChanged();
        return update;
    }

    internal bool RemoveLocalReviewDirectory(string directory)
    {
        if (_localReviewCoordinator.IsRunning(directory))
        {
            return false;
        }

        if (!_settings.RemoveLocalReviewDirectory(directory))
        {
            return false;
        }

        _settings.Save();
        _settingsLastWriteUtc = GetSettingsLastWriteUtc();
        _localReviewCoordinator.Forget(directory);
        NotifyExternalStateChanged();
        return true;
    }

    internal Task<LocalReviewOutcome> ReviewLocalDirectoryAsync(
        string directory,
        IReadOnlyCollection<ReviewAgentSettings> agents,
        CancellationToken cancellationToken)
    {
        return _localReviewCoordinator.ReviewAsync(
            directory,
            _settings.CodexReview.WithAgents(agents),
            cancellationToken);
    }

    private string RepositorySummary()
    {
        if (_repositories.Count == 0)
        {
            return "no tracked repositories";
        }

        if (_repositories.Count == 1)
        {
            return _repositories[0].FullName;
        }

        var names = _repositories.Take(3).Select(repo => repo.FullName);
        var suffix = _repositories.Count > 3 ? $", +{_repositories.Count - 3} more" : "";
        return $"{_repositories.Count} repos: {string.Join(", ", names)}{suffix}";
    }

    private string EmptyListMessage()
    {
        if (_error is not null)
        {
            return $"Refresh failed: {_error}";
        }

        if (_repositories.Count == 0)
        {
            return $"No tracked repositories. Add one with: pr https://github.com/OWNER/REPO. Settings: {_settingsPath}";
        }

        return _ignoredPullRequestCount == 0
            ? $"No open non-draft PRs from other authors are currently below {_requiredApprovals} approvals."
            : $"No visible open non-draft PRs from other authors are currently below {_requiredApprovals} approvals. Ignored PRs are hidden.";
    }

    private DateTime GetSettingsLastWriteUtc()
    {
        try
        {
            return File.Exists(_settingsPath)
                ? File.GetLastWriteTimeUtc(_settingsPath)
                : DateTime.MinValue;
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        if (maxLength <= 3)
        {
            return value[..maxLength];
        }

        return value[..(maxLength - 3)] + "...";
    }

    private sealed class DashboardView
    {
        private static readonly Tui.ColorScheme GrayScheme = new()
        {
            Normal = new Tui.Attribute(Tui.Color.Gray, Tui.Color.Black),
            Focus = new Tui.Attribute(Tui.Color.White, Tui.Color.DarkGray),
            HotNormal = new Tui.Attribute(Tui.Color.White, Tui.Color.Black),
            HotFocus = new Tui.Attribute(Tui.Color.White, Tui.Color.DarkGray),
            Disabled = new Tui.Attribute(Tui.Color.DarkGray, Tui.Color.Black),
        };

        private static readonly Tui.ColorScheme IgnoredScheme = new()
        {
            Normal = new Tui.Attribute(Tui.Color.DarkGray, Tui.Color.Black),
            Focus = new Tui.Attribute(Tui.Color.Gray, Tui.Color.DarkGray),
            HotNormal = new Tui.Attribute(Tui.Color.Gray, Tui.Color.Black),
            HotFocus = new Tui.Attribute(Tui.Color.White, Tui.Color.DarkGray),
            Disabled = new Tui.Attribute(Tui.Color.DarkGray, Tui.Color.Black),
        };

        private static readonly Tui.ColorScheme LinkScheme = new()
        {
            Normal = new Tui.Attribute(Tui.Color.BrightBlue, Tui.Color.Black),
            Focus = new Tui.Attribute(Tui.Color.BrightCyan, Tui.Color.DarkGray),
            HotNormal = new Tui.Attribute(Tui.Color.BrightBlue, Tui.Color.Black),
            HotFocus = new Tui.Attribute(Tui.Color.BrightCyan, Tui.Color.DarkGray),
            Disabled = new Tui.Attribute(Tui.Color.DarkGray, Tui.Color.Black),
        };

        private static readonly Tui.ColorScheme RedScheme = new()
        {
            Normal = new Tui.Attribute(Tui.Color.BrightRed, Tui.Color.Black),
            Focus = new Tui.Attribute(Tui.Color.BrightRed, Tui.Color.DarkGray),
            HotNormal = new Tui.Attribute(Tui.Color.BrightRed, Tui.Color.Black),
            HotFocus = new Tui.Attribute(Tui.Color.BrightRed, Tui.Color.DarkGray),
            Disabled = new Tui.Attribute(Tui.Color.DarkGray, Tui.Color.Black),
        };

        private static readonly Tui.ColorScheme YellowScheme = new()
        {
            Normal = new Tui.Attribute(Tui.Color.BrightYellow, Tui.Color.Black),
            Focus = new Tui.Attribute(Tui.Color.BrightYellow, Tui.Color.DarkGray),
            HotNormal = new Tui.Attribute(Tui.Color.BrightYellow, Tui.Color.Black),
            HotFocus = new Tui.Attribute(Tui.Color.BrightYellow, Tui.Color.DarkGray),
            Disabled = new Tui.Attribute(Tui.Color.DarkGray, Tui.Color.Black),
        };

        private static readonly Tui.ColorScheme GreenScheme = new()
        {
            Normal = new Tui.Attribute(Tui.Color.BrightGreen, Tui.Color.Black),
            Focus = new Tui.Attribute(Tui.Color.BrightGreen, Tui.Color.DarkGray),
            HotNormal = new Tui.Attribute(Tui.Color.BrightGreen, Tui.Color.Black),
            HotFocus = new Tui.Attribute(Tui.Color.BrightGreen, Tui.Color.DarkGray),
            Disabled = new Tui.Attribute(Tui.Color.DarkGray, Tui.Color.Black),
        };

        private static readonly char[] WaveSymbols = ['.', ':', '-', '=', '+', '*', '#', '%', '@'];

        private readonly DashboardApp _owner;
        private IReadOnlyList<PullRequestInfo> _visiblePullRequests = [];
        private IReadOnlyList<TableRowBinding> _tableRows = [];
        private ColumnLayout _columns = ColumnLayout.Default;
        private Tui.View _searchBar = null!;
        private Tui.TextField _searchField = null!;
        private ClickableTableView _table = null!;
        private Tui.View _loadingFrame = null!;
        private Tui.Label _loadingArt = null!;
        private Tui.Label _title = null!;
        private Tui.Label _status = null!;
        private Tui.Label _summary = null!;
        private Tui.Label _scan = null!;
        private Tui.Label _footer = null!;
        private object? _loadingTimerToken;
        private int _loadingFrameIndex;
        private bool _isRefreshing;
        private bool _isCleaning;
        private bool _isSearchActive;
        private bool _suppressSearchChanged;
        private string _searchText = "";

        public DashboardView(DashboardApp owner)
        {
            _owner = owner;
        }

        public void Build(Tui.Toplevel top)
        {
            top.ColorScheme = GrayScheme;
            var window = new Tui.Window("PRs")
            {
                X = 0,
                Y = 0,
                Width = Tui.Dim.Fill(),
                Height = Tui.Dim.Fill(),
                ColorScheme = GrayScheme,
            };

            var header = new Tui.FrameView("Status")
            {
                X = 0,
                Y = 0,
                Width = Tui.Dim.Fill(),
                Height = 4,
                ColorScheme = GrayScheme,
            };

            _title = new Tui.Label(1, 0, "GitHub PR Control Panel", false)
            {
                Width = Tui.Dim.Percent(35),
                Height = 1,
            };
            _status = new Tui.Label
            {
                X = Tui.Pos.Percent(35),
                Y = 0,
                Width = Tui.Dim.Fill(),
                Height = 1,
                TextAlignment = Tui.TextAlignment.Right,
            };
            _summary = new Tui.Label
            {
                X = 1,
                Y = 1,
                Width = Tui.Dim.Percent(35),
                Height = 1,
            };
            _scan = new Tui.Label
            {
                X = Tui.Pos.Percent(35),
                Y = 1,
                Width = Tui.Dim.Fill(),
                Height = 1,
                TextAlignment = Tui.TextAlignment.Right,
            };

            header.Add(_title);
            header.Add(_status);
            header.Add(_summary);
            header.Add(_scan);

            _searchBar = new Tui.View
            {
                X = 0,
                Y = Tui.Pos.Bottom(header),
                Width = Tui.Dim.Fill(),
                Height = 1,
                CanFocus = false,
                ColorScheme = GrayScheme,
                Visible = false,
                ClearOnVisibleFalse = true,
            };
            var searchLabel = new Tui.Label(1, 0, "Search:", false)
            {
                Width = 8,
                Height = 1,
            };
            _searchField = new Tui.TextField("")
            {
                X = 9,
                Y = 0,
                Width = Tui.Dim.Fill(1),
                Height = 1,
                ColorScheme = GrayScheme,
            };
            _searchField.TextChanged += _ =>
            {
                if (_suppressSearchChanged)
                {
                    return;
                }

                _searchText = _searchField.Text.ToString() ?? "";
                Refresh(_isRefreshing, _isCleaning);
            };
            _searchField.KeyPress += HandleKeyPress;
            _searchBar.Add(searchLabel);
            _searchBar.Add(_searchField);

            _table = new ClickableTableView
            {
                X = 0,
                Y = Tui.Pos.Bottom(header),
                Width = Tui.Dim.Fill(),
                Height = Tui.Dim.Fill(3),
                FullRowSelect = true,
                CanFocus = true,
                ColorScheme = GrayScheme,
                CellClicked = ActivateCell,
            };
            _table.CellActivated += args => ActivateCell(args.Col, args.Row);
            _table.KeyPress += HandleKeyPress;

            _loadingFrame = new Tui.View
            {
                X = 0,
                Y = Tui.Pos.Bottom(header),
                Width = Tui.Dim.Fill(),
                Height = Tui.Dim.Fill(3),
                CanFocus = false,
                ColorScheme = GrayScheme,
                Visible = false,
                ClearOnVisibleFalse = true,
            };
            _loadingArt = new Tui.Label
            {
                X = 0,
                Y = 0,
                Width = Tui.Dim.Fill(),
                Height = Tui.Dim.Fill(),
            };
            _loadingFrame.Add(_loadingArt);
            _loadingFrame.KeyPress += HandleKeyPress;

            var footerFrame = new Tui.FrameView("Commands")
            {
                X = 0,
                Y = Tui.Pos.AnchorEnd(3),
                Width = Tui.Dim.Fill(),
                Height = 3,
                ColorScheme = GrayScheme,
            };
            _footer = new Tui.Label
            {
                X = 1,
                Y = 0,
                Width = Tui.Dim.Fill(1),
                Height = 1,
            };
            footerFrame.Add(_footer);
            footerFrame.KeyPress += HandleKeyPress;

            window.Add(header);
            window.Add(_searchBar);
            window.Add(_table);
            window.Add(_loadingFrame);
            window.Add(footerFrame);
            window.KeyPress += HandleKeyPress;
            top.Add(window);

            _table.SetFocus();
        }

        public void Refresh(bool isRefreshing, bool isCleaning)
        {
            _isRefreshing = isRefreshing;
            _isCleaning = isCleaning;
            _visiblePullRequests = _owner.GetVisiblePullRequests(_searchText);
            _columns = CalculateColumnLayout();
            _table.MaxCellWidth = Math.Max(200, _columns.Title);

            _status.Text = $"{_owner.RepositorySummary()} - {(isRefreshing ? "refreshing" : "watching")}";
            _status.ColorScheme = isRefreshing ? YellowScheme : GreenScheme;
            _summary.Text = SummaryText();
            _scan.Text = ScanText();
            _scan.ColorScheme = CodexStatusColor();
            _footer.Text = FooterText();
            LayoutContentViews();

            if (ShouldShowInitialLoading())
            {
                ShowInitialLoading();
                Tui.Application.Refresh();
                return;
            }

            HideInitialLoading();

            var previousRow = _table.SelectedRow;
            var previousColumn = _table.SelectedColumn;
            var previousSelectedKey = SelectedPullRequest()?.Key;
            var table = BuildDataTable();
            _table.Table = table;
            _table.Style = BuildTableStyle(table);

            if (_tableRows.Count > 0)
            {
                var selectedRow = previousSelectedKey is null
                    ? -1
                    : FindTableRow(previousSelectedKey);
                _table.SelectedRow = selectedRow >= 0
                    ? selectedRow
                    : Math.Clamp(previousRow, 0, _tableRows.Count - 1);
                _table.SelectedColumn = Math.Clamp(previousColumn, 0, IgnoreColumn);
            }
            else
            {
                _table.SelectedRow = 0;
                _table.SelectedColumn = 0;
            }

            _footer.Text = FooterText();
            _table.Update();
            _table.SetNeedsDisplay();
            Tui.Application.Refresh();
        }

        public void RefreshCurrent()
        {
            Refresh(_isRefreshing, _isCleaning);
        }

        private bool ShouldShowInitialLoading()
        {
            return _isRefreshing
                && _owner._lastRefresh is null
                && _owner._repositories.Count > 0;
        }

        private void ShowInitialLoading()
        {
            _table.Visible = false;
            _loadingFrame.Visible = true;
            LayoutContentViews();
            _summary.Text = "first refresh in progress";
            _scan.Text = "fetching PRs, reviews, comments, and review requests";
            UpdateLoadingArt();
            EnsureLoadingTimer();
            _loadingFrame.SetNeedsDisplay();
        }

        private void HideInitialLoading()
        {
            _loadingFrame.Visible = false;
            _table.Visible = true;
            LayoutContentViews();
            StopLoadingTimer();
        }

        private void LayoutContentViews()
        {
            _searchBar.Visible = _isSearchActive;
            var contentY = _isSearchActive
                ? Tui.Pos.Bottom(_searchBar)
                : _searchBar.Y;
            _table.Y = contentY;
            _loadingFrame.Y = contentY;
        }

        private void EnsureLoadingTimer()
        {
            if (_loadingTimerToken is not null)
            {
                return;
            }

            _loadingTimerToken = Tui.Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(180), _ =>
            {
                if (!ShouldShowInitialLoading())
                {
                    _loadingTimerToken = null;
                    return false;
                }

                _loadingFrameIndex++;
                UpdateLoadingArt();
                Tui.Application.Refresh();
                return true;
            });
        }

        private void StopLoadingTimer()
        {
            if (_loadingTimerToken is null)
            {
                return;
            }

            Tui.Application.MainLoop.RemoveTimeout(_loadingTimerToken);
            _loadingTimerToken = null;
        }

        private void UpdateLoadingArt()
        {
            _loadingArt.Text = BuildLoadingWaveArt();
            _loadingArt.SetNeedsDisplay();
        }

        private string BuildLoadingWaveArt()
        {
            var width = _loadingFrame.Bounds.Width > 0
                ? Math.Max(24, _loadingFrame.Bounds.Width)
                : 76;
            var height = _loadingFrame.Bounds.Height > 0
                ? Math.Max(6, _loadingFrame.Bounds.Height)
                : 18;
            var phase = _loadingFrameIndex * 0.55;
            var builder = new StringBuilder(height * (width + 1));
            for (var row = 0; row < height; row++)
            {
                var rowPhase = row * 0.38;
                for (var column = 0; column < width; column++)
                {
                    var x = column * 0.17;
                    var diagonal = (column + row) * 0.055;
                    var primary = Math.Sin(x + rowPhase - phase);
                    var secondary = Math.Sin((column * 0.07) - (row * 0.31) - (phase * 0.7));
                    var swell = Math.Sin(diagonal - (phase * 0.42));
                    var surface = ((primary * 0.62) + (secondary * 0.26) + (swell * 0.12) + 1.0) / 2.0;
                    var symbol = Math.Clamp(surface, 0.0, 1.0);
                    var symbolIndex = (int)Math.Round(symbol * (WaveSymbols.Length - 1));
                    builder.Append(WaveSymbols[symbolIndex]);
                }

                if (row < height - 1)
                {
                    builder.AppendLine();
                }
            }

            return builder.ToString();
        }

        private DataTable BuildDataTable()
        {
            var table = new DataTable();
            var rows = new List<TableRowBinding>();
            table.Columns.Add(FitCell("Repo", _columns.Repo));
            table.Columns.Add(FitCell("PR", _columns.PullRequest));
            table.Columns.Add(FitCell("Title", _columns.Title));
            table.Columns.Add(FitCell("Author", _columns.Author));
            table.Columns.Add(FitCell("Created", _columns.Created));
            table.Columns.Add(FitCell("Approvals", _columns.Approval));
            table.Columns.Add(FitCell("Ignore", _columns.Ignore));

            if (_visiblePullRequests.Count == 0)
            {
                var message = _isRefreshing && _owner._repositories.Count > 0
                    ? ""
                    : _owner.EmptyListMessage();

                table.Rows.Add(
                    FitCell("", _columns.Repo),
                    FitCell("", _columns.PullRequest),
                    FitCell(message, _columns.Title),
                    FitCell("", _columns.Author),
                    FitCell("", _columns.Created),
                    FitCell("", _columns.Approval),
                    FitCell("", _columns.Ignore));
                rows.Add(TableRowBinding.Empty);
                _tableRows = rows;
                return table;
            }

            TableSection? previousSection = null;
            for (var index = 0; index < _visiblePullRequests.Count; index++)
            {
                var pullRequest = _visiblePullRequests[index];
                var section = SectionFor(pullRequest);
                if (previousSection is not null && section != previousSection)
                {
                    table.Rows.Add(
                        FitCell("", _columns.Repo),
                        FitCell("", _columns.PullRequest),
                        FitCell(SectionSeparator(section, _columns.Title - 2), _columns.Title),
                        FitCell("", _columns.Author),
                        FitCell("", _columns.Created),
                        FitCell("", _columns.Approval),
                        FitCell("", _columns.Ignore));
                    rows.Add(TableRowBinding.Separator);
                }

                previousSection = section;
                var approvers = pullRequest.Approvers.Count == 0
                    ? "none"
                    : Truncate(string.Join(", ", pullRequest.Approvers), 36);
                var title = _owner.IsIgnored(pullRequest)
                    ? $"(ignored) {pullRequest.Title}"
                    : pullRequest.Title;
                if (_owner.CodexReviewFor(pullRequest) is { } codexReview)
                {
                    var result = codexReview.FindingCount == 0
                        ? "clean"
                        : $"{codexReview.FindingCount.ToString(CultureInfo.InvariantCulture)} issue(s)";
                    var delivery = codexReview.IsDryRun
                        ? "local"
                        : codexReview.ReviewSubmitted
                            ? "sent"
                            : codexReview.ReviewCreated
                                ? "draft"
                                : "complete";
                    var agentPrefix = codexReview.AgentLetterPrefix;
                    title = string.IsNullOrEmpty(agentPrefix)
                        ? $"[{delivery} {result}] {title}"
                        : $"{agentPrefix} [{delivery} {result}] {title}";
                }

                table.Rows.Add(
                    FitCell(pullRequest.Repository.Name, _columns.Repo),
                    FitCell($"#{pullRequest.Number.ToString(CultureInfo.InvariantCulture)}", _columns.PullRequest),
                    FitCell(title, _columns.Title, underline: true),
                    FitCell("@" + pullRequest.Author, _columns.Author),
                    FitCell(pullRequest.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture), _columns.Created),
                    FitCell($"{pullRequest.ApprovalCount.ToString(CultureInfo.InvariantCulture)}/{_owner._requiredApprovals.ToString(CultureInfo.InvariantCulture)} {approvers}", _columns.Approval),
                    FitCell(_owner.IsIgnored(pullRequest) ? "unignore" : "ignore", _columns.Ignore, Tui.TextAlignment.Centered));
                rows.Add(new TableRowBinding(pullRequest));
            }

            _tableRows = rows;
            return table;
        }

        private TableSection SectionFor(PullRequestInfo pullRequest)
        {
            return _owner.IsCodexReviewed(pullRequest)
                ? TableSection.CodexReviewed
                : _owner.IsTop(pullRequest)
                    ? TableSection.Top
                    : TableSection.Regular;
        }

        private static string SectionSeparator(TableSection nextSection, int width)
        {
            var label = nextSection == TableSection.Top ? " Top PRs " : " Other PRs ";
            var dashCount = Math.Max(3, width - label.Length);
            var left = dashCount / 2;
            return new string('-', left) + label + new string('-', dashCount - left);
        }

        private Tui.TableView.TableStyle BuildTableStyle(DataTable table)
        {
            var style = new Tui.TableView.TableStyle
            {
                AlwaysShowHeaders = true,
                ShowHorizontalHeaderUnderline = true,
                ShowVerticalCellLines = true,
                ShowHorizontalScrollIndicators = false,
                ExpandLastColumn = false,
                RowColorGetter = args => RowPullRequest(args.RowIndex) is { } pullRequest
                    && _owner.IsIgnored(pullRequest)
                        ? IgnoredScheme
                        : GrayScheme,
            };

            style.ColumnStyles[table.Columns[RepoColumn]!] = FixedColumn(_columns.Repo);
            style.ColumnStyles[table.Columns[PullRequestColumn]!] = FixedColumn(_columns.PullRequest, PullRequestColor);
            style.ColumnStyles[table.Columns[TitleColumn]!] = FixedColumn(_columns.Title, LinkColor);
            style.ColumnStyles[table.Columns[AuthorColumn]!] = FixedColumn(_columns.Author);
            style.ColumnStyles[table.Columns[CreatedColumn]!] = FixedColumn(_columns.Created);
            style.ColumnStyles[table.Columns[ApprovalColumn]!] = FixedColumn(_columns.Approval, ApprovalColor);
            style.ColumnStyles[table.Columns[IgnoreColumn]!] = new Tui.TableView.ColumnStyle
            {
                MinWidth = _columns.Ignore,
                MaxWidth = _columns.Ignore,
                MinAcceptableWidth = _columns.Ignore,
                Alignment = Tui.TextAlignment.Centered,
                ColorGetter = _ => GrayScheme,
            };

            return style;
        }

        private ColumnLayout CalculateColumnLayout()
        {
            var tableWidth = _table.Bounds.Width > 0
                ? _table.Bounds.Width
                : Math.Max(80, Tui.Application.Driver.Cols - 4);
            tableWidth = Math.Max(80, tableWidth);

            const int separatorWidth = 6;
            var extra = Math.Max(0, tableWidth - 80);
            var repo = 12 + Math.Min(12, extra / 8);
            var pullRequest = 8;
            var author = 10 + Math.Min(8, extra / 10);
            var created = 12 + Math.Min(6, extra / 6);
            var approval = 10 + Math.Min(12, extra / 12);
            var ignore = 10;
            var title = Math.Max(12, tableWidth - separatorWidth - repo - pullRequest - author - created - approval - ignore);
            return new ColumnLayout(repo, pullRequest, title, author, created, approval, ignore);
        }

        private static Tui.TableView.ColumnStyle FixedColumn(
            int width,
            Tui.TableView.CellColorGetterDelegate? colorGetter = null)
        {
            return new Tui.TableView.ColumnStyle
            {
                MinWidth = width,
                MaxWidth = width,
                MinAcceptableWidth = width,
                ColorGetter = colorGetter,
            };
        }

        private Tui.ColorScheme ApprovalColor(Tui.TableView.CellColorGetterArgs args)
        {
            var pullRequest = RowPullRequest(args.RowIndex);
            if (pullRequest is null)
            {
                return GrayScheme;
            }

            return pullRequest.ApprovalCount == 0
                ? RedScheme
                : YellowScheme;
        }

        private Tui.ColorScheme LinkColor(Tui.TableView.CellColorGetterArgs args)
        {
            var pullRequest = RowPullRequest(args.RowIndex);
            if (pullRequest is null)
            {
                return GrayScheme;
            }

            return _owner.IsIgnored(pullRequest)
                    ? IgnoredScheme
                    : LinkScheme;
        }

        private Tui.ColorScheme PullRequestColor(Tui.TableView.CellColorGetterArgs args)
        {
            var pullRequest = RowPullRequest(args.RowIndex);
            if (pullRequest is null)
            {
                return GrayScheme;
            }

            if (_owner.IsIgnored(pullRequest))
            {
                return IgnoredScheme;
            }

            return HeatColor(pullRequest.Priority.Heat);
        }

        private static Tui.ColorScheme HeatColor(PullRequestHeat heat)
        {
            return heat switch
            {
                PullRequestHeat.SuperHot => RedScheme,
                PullRequestHeat.Hot => YellowScheme,
                _ => GreenScheme,
            };
        }

        private static string FitCell(
            string value,
            int width,
            Tui.TextAlignment alignment = Tui.TextAlignment.Left,
            bool underline = false)
        {
            var innerWidth = Math.Max(0, width - 2);
            var text = Truncate(NormalizeCell(value), innerWidth);
            var displayText = underline ? Underline(text) : text;
            var leftPadding = 1;
            var rightPadding = 1;

            if (alignment == Tui.TextAlignment.Centered && text.Length < innerWidth)
            {
                var remaining = innerWidth - text.Length;
                leftPadding += remaining / 2;
                rightPadding += remaining - remaining / 2;
            }
            else
            {
                rightPadding += Math.Max(0, innerWidth - text.Length);
            }

            return new string(' ', leftPadding) + displayText + new string(' ', rightPadding);
        }

        private static string Underline(string value)
        {
            if (value.Length == 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length * 2);
            foreach (var character in value)
            {
                builder.Append(character);
                if (!char.IsWhiteSpace(character))
                {
                    builder.Append('\u0332');
                }
            }

            return builder.ToString();
        }

        private static string NormalizeCell(string value)
        {
            return value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private string ScanText()
        {
            var pendingReviews = _owner._codexReviewSnapshot.PendingReviewedPullRequests.Count;
            var scanText = _owner._codexReviewSnapshot.Message;
            scanText += $", {_owner._codexReviewSnapshot.WaitingCount.ToString(CultureInfo.InvariantCulture)} pending";
            if (_owner._codexReviewSnapshot.NextReadyAt is { } nextReadyAt)
            {
                var remaining = nextReadyAt - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    scanText += remaining.TotalMinutes >= 1
                        ? $", next in {Math.Ceiling(remaining.TotalMinutes).ToString(CultureInfo.InvariantCulture)}m"
                        : $", next in {Math.Ceiling(remaining.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s";
                }
            }

            if (pendingReviews > 0)
            {
                scanText += $", {pendingReviews.ToString(CultureInfo.InvariantCulture)} new";
            }

            if (_owner._codexReviewSnapshot.ManualQueueCount > 0)
            {
                scanText += $", {_owner._codexReviewSnapshot.ManualQueueCount.ToString(CultureInfo.InvariantCulture)} manual queued";
            }

            scanText += $" | {_owner._openNonDraftCount.ToString(CultureInfo.InvariantCulture)} matched, {_owner._apiCalls.ToString(CultureInfo.InvariantCulture)} API call(s)";
            if (_owner._ignoredPullRequestCount > 0)
            {
                scanText += $", {_owner._ignoredPullRequestCount.ToString(CultureInfo.InvariantCulture)} ignored";
            }

            var availableWidth = _scan.Bounds.Width > 0 ? _scan.Bounds.Width : 72;
            return Truncate(scanText, Math.Max(20, availableWidth - 1));
        }

        private Tui.ColorScheme CodexStatusColor()
        {
            var message = _owner._codexReviewSnapshot.Message;
            if (message.Contains("error", StringComparison.OrdinalIgnoreCase)
                || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("rejected", StringComparison.OrdinalIgnoreCase)
                || message.Contains("cannot queue", StringComparison.OrdinalIgnoreCase))
            {
                return RedScheme;
            }

            if (message.Contains("reviewing", StringComparison.OrdinalIgnoreCase)
                || message.Contains("scanning", StringComparison.OrdinalIgnoreCase)
                || message.Contains("starting", StringComparison.OrdinalIgnoreCase)
                || message.Contains("waiting", StringComparison.OrdinalIgnoreCase)
                || message.Contains("queued", StringComparison.OrdinalIgnoreCase)
                || _owner._codexReviewSnapshot.ManualQueueCount > 0)
            {
                return YellowScheme;
            }

            return !_owner._codexReviewSnapshot.Enabled ? IgnoredScheme : GreenScheme;
        }

        private string SummaryText()
        {
            var search = _searchText.Trim();
            if (search.Length > 0)
            {
                return $"{_visiblePullRequests.Count} match(es) for \"{Truncate(search, 32)}\"; ignored included";
            }

            return $"{_visiblePullRequests.Count} visible PRs below {_owner._requiredApprovals} approvals";
        }

        private string FooterText()
        {
            var selectedPullRequest = SelectedPullRequest();
            var ignore = selectedPullRequest is null
                ? "I ignore disabled"
                : _owner.IsIgnored(selectedPullRequest)
                    ? "I unignore"
                    : "I ignore";
            var top = selectedPullRequest is null
                ? "T top disabled"
                : _owner.IsTop(selectedPullRequest)
                    ? "T untop"
                    : "T top";
            var enqueue = selectedPullRequest is null
                ? "E force disabled"
                : _owner.IsCodexReviewQueued(selectedPullRequest)
                    ? "E forced"
                    : "E force review";
            var reveal = _owner._showIgnoredPullRequests ? "Ctrl+I hide ignored" : "Ctrl+I show ignored";
            var refresh = _isRefreshing ? "refreshing..." : "R refresh";
            var cleanup = !_owner.CanCleanup()
                ? "C clean disabled"
                : _isCleaning
                    ? "cleaning..."
                    : "C clean";
            var errors = string.Join(" ", new[] { _owner._error, _owner._cleanupError }.Where(error => !string.IsNullOrWhiteSpace(error)));
            var suffix = string.IsNullOrWhiteSpace(errors) ? "" : $"  {errors}";
            var search = string.IsNullOrWhiteSpace(_searchText)
                ? (_isSearchActive ? "Esc cancel search" : "F1 search")
                : $"F1 search '{Truncate(_searchText, 20)}'  Esc cancel search";
            var reviewAgent = _owner._settings.CodexReview.AgentDisplayName;
            var codexReview = _owner._settings.CodexReview.Enabled
                ? $"V {reviewAgent} off"
                : $"V {reviewAgent} on";
            var autoSubmit = _owner._settings.CodexReview.AutoSubmit
                ? "A auto-send:on"
                : "A auto-send:off";
            return $"Up/Down scroll  Enter open  Click Title open/ack  Click Ignore toggle  {ignore}  {top}  {enqueue}  {codexReview}  {autoSubmit}  S stats  {reveal}  {search}  {refresh}  {cleanup}  Q quit{suffix}";
        }

        private PullRequestInfo? SelectedPullRequest()
        {
            return RowPullRequest(_table.SelectedRow);
        }

        private PullRequestInfo? RowPullRequest(int row)
        {
            return row >= 0 && row < _tableRows.Count
                ? _tableRows[row].PullRequest
                : null;
        }

        private int FindTableRow(string pullRequestKey)
        {
            for (var row = 0; row < _tableRows.Count; row++)
            {
                if (_tableRows[row].PullRequest is { } pullRequest
                    && string.Equals(pullRequest.Key, pullRequestKey, StringComparison.OrdinalIgnoreCase))
                {
                    return row;
                }
            }

            return -1;
        }

        private void HandleKeyPress(Tui.View.KeyEventEventArgs args)
        {
            var key = BaseKey(args.KeyEvent.Key);
            var isCtrl = args.KeyEvent.IsCtrl || HasModifier(args.KeyEvent.Key, Tui.Key.CtrlMask);

            if (key == Tui.Key.Esc && _isSearchActive)
            {
                CancelSearch();
                args.Handled = true;
                return;
            }

            if (_isSearchActive && _searchField.HasFocus)
            {
                return;
            }

            if (key == Tui.Key.Q || key == Tui.Key.q || key == Tui.Key.Esc)
            {
                _owner.Quit();
                args.Handled = true;
                return;
            }

            if (key == Tui.Key.Tab || (isCtrl && (key == Tui.Key.I || key == Tui.Key.i)))
            {
                _owner.ToggleIgnoredVisibility();
                Refresh(_isRefreshing, _isCleaning);
                args.Handled = true;
                return;
            }

            if (key == Tui.Key.F1)
            {
                BeginSearch();
                args.Handled = true;
                return;
            }

            if (!isCtrl && (key == Tui.Key.I || key == Tui.Key.i))
            {
                ToggleSelected();
                args.Handled = true;
                return;
            }

            if (!isCtrl && (key == Tui.Key.T || key == Tui.Key.t))
            {
                ToggleSelectedTop();
                args.Handled = true;
                return;
            }

            if (!isCtrl && (key == Tui.Key.E || key == Tui.Key.e))
            {
                EnqueueSelectedReview();
                args.Handled = true;
                return;
            }

            if (!isCtrl && (key == Tui.Key.V || key == Tui.Key.v))
            {
                if (_owner.ToggleCodexReview())
                {
                    Refresh(_isRefreshing, _isCleaning);
                }

                args.Handled = true;
                return;
            }

            if (!isCtrl && (key == Tui.Key.A || key == Tui.Key.a))
            {
                if (_owner.ToggleCodexReviewAutoSubmit())
                {
                    Refresh(_isRefreshing, _isCleaning);
                }

                args.Handled = true;
                return;
            }

            if (!isCtrl && (key == Tui.Key.S || key == Tui.Key.s))
            {
                ShowStatsDialog();
                args.Handled = true;
                return;
            }

            if (!isCtrl && (key == Tui.Key.R || key == Tui.Key.r) && !_isRefreshing)
            {
                _owner.RequestRefresh();
                Refresh(_isRefreshing, _isCleaning);
                args.Handled = true;
                return;
            }

            if (!isCtrl && (key == Tui.Key.C || key == Tui.Key.c) && !_isCleaning && _owner.CanCleanup())
            {
                _owner.RequestCleanup();
                Refresh(_isRefreshing, _isCleaning);
                args.Handled = true;
            }
        }

        private void BeginSearch()
        {
            _isSearchActive = true;
            _suppressSearchChanged = true;
            _searchField.Text = _searchText;
            _suppressSearchChanged = false;
            Refresh(_isRefreshing, _isCleaning);
            _searchField.SetFocus();
        }

        private void CancelSearch()
        {
            _isSearchActive = false;
            _searchText = "";
            _suppressSearchChanged = true;
            _searchField.Text = "";
            _suppressSearchChanged = false;
            Refresh(_isRefreshing, _isCleaning);
            _table.SetFocus();
        }

        private void ActivateCell(int column, int row)
        {
            var pullRequest = RowPullRequest(row);
            if (pullRequest is null)
            {
                return;
            }

            switch (column)
            {
                case PullRequestColumn:
                    ShowPriorityDialog(pullRequest);
                    break;

                case TitleColumn:
                    if (_owner.IsCodexReviewed(pullRequest))
                    {
                        _owner.AcknowledgeCodexReview(pullRequest);
                    }

                    PullRequests.OpenUrl(pullRequest.Url);
                    Refresh(_isRefreshing, _isCleaning);
                    break;

                case IgnoreColumn:
                    if (_owner.ToggleIgnored(pullRequest))
                    {
                        Refresh(_isRefreshing, _isCleaning);
                    }

                    break;
            }
        }

        private void ShowPriorityDialog(PullRequestInfo pullRequest)
        {
            var priority = pullRequest.Priority;
            var width = Math.Clamp(Tui.Application.Driver.Cols - 4, 72, 104);
            var height = Math.Clamp(Tui.Application.Driver.Rows - 4, 18, 26);
            var close = new Tui.Button("Close", true);
            var dialog = new Tui.Dialog($"Hotness #{pullRequest.Number.ToString(CultureInfo.InvariantCulture)}", width, height, close)
            {
                ColorScheme = GrayScheme,
            };
            close.Clicked += () => Tui.Application.RequestStop(dialog);

            var summary = new Tui.Label
            {
                X = 1,
                Y = 1,
                Width = Tui.Dim.Fill(2),
                Height = 3,
                Text = $"{pullRequest.Repository.Name}  #{pullRequest.Number.ToString(CultureInfo.InvariantCulture)}  {Truncate(pullRequest.Title, Math.Max(20, width - 28))}\n"
                    + $"Score {priority.Score.ToString(CultureInfo.InvariantCulture)} ({priority.Label}); hot >= {_owner._settings.Priority.HotThreshold.ToString(CultureInfo.InvariantCulture)}, super > {_owner._settings.Priority.SuperHotThreshold.ToString(CultureInfo.InvariantCulture)}\n"
                    + $"Human commenters/approvers: {priority.HumanCommenterCount.ToString(CultureInfo.InvariantCulture)}; human reviewers: {priority.HumanReviewCount.ToString(CultureInfo.InvariantCulture)}; requested from you: {(priority.ReviewRequestedFromUser ? "yes" : "no")}",
            };

            var table = new Tui.TableView
            {
                X = 1,
                Y = 5,
                Width = Tui.Dim.Fill(2),
                Height = Tui.Dim.Fill(2),
                FullRowSelect = true,
                CanFocus = true,
                ColorScheme = GrayScheme,
            };
            var data = BuildPriorityTable(priority);
            table.Table = data;
            table.Style = BuildPriorityTableStyle(data, priority);

            dialog.Add(summary);
            dialog.Add(table);
            table.SetFocus();
            Tui.Application.Run(dialog);
            _table.SetFocus();
        }

        private void ShowStatsDialog()
        {
            var width = Math.Clamp(Tui.Application.Driver.Cols - 4, 58, 86);
            var height = Math.Clamp(Tui.Application.Driver.Rows - 4, 12, 18);
            var back = new Tui.Button("Back", true);
            var dialog = new Tui.Dialog("This Week", width, height, back)
            {
                ColorScheme = GrayScheme,
            };
            var body = new Tui.Label
            {
                X = 1,
                Y = 1,
                Width = Tui.Dim.Fill(2),
                Height = Tui.Dim.Fill(2),
                Text = "Loading weekly stats...",
            };

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _owner._appCancellation?.Token ?? CancellationToken.None);
            void Close()
            {
                cancellation.Cancel();
                Tui.Application.RequestStop(dialog);
            }

            back.Clicked += Close;
            dialog.KeyPress += args =>
            {
                var key = BaseKey(args.KeyEvent.Key);
                if (key == Tui.Key.Esc || key == Tui.Key.B || key == Tui.Key.b)
                {
                    Close();
                    args.Handled = true;
                }
            };

            dialog.Add(body);
            _ = LoadStatsDialogAsync(body, cancellation.Token);

            Tui.Application.Run(dialog);
            _table.SetFocus();
        }

        private async Task LoadStatsDialogAsync(Tui.Label body, CancellationToken cancellationToken)
        {
            try
            {
                var stats = await _owner.FetchWeeklyStatsAsync(cancellationToken);
                UpdateStatsDialog(body, FormatWeeklyStats(stats), GrayScheme);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                UpdateStatsDialog(body, $"Failed to load weekly stats:\n{ex.Message}", RedScheme);
            }
        }

        private static void UpdateStatsDialog(Tui.Label body, string text, Tui.ColorScheme colorScheme)
        {
            try
            {
                Tui.Application.MainLoop.Invoke(() =>
                {
                    body.Text = text;
                    body.ColorScheme = colorScheme;
                    body.SetNeedsDisplay();
                    Tui.Application.Refresh();
                });
            }
            catch
            {
            }
        }

        private static string FormatWeeklyStats(WeeklyStats stats)
        {
            var start = stats.WeekStartLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            var end = stats.WeekEndLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            var scope = stats.TrackedRepositoryCount == 0
                ? "no tracked repositories"
                : stats.TrackedRepositoryCount == 1
                    ? "1 tracked repository"
                    : $"{stats.TrackedRepositoryCount.ToString(CultureInfo.InvariantCulture)} tracked repositories";

            return $"@{stats.UserLogin}\n\n"
                + $"Created PRs: {stats.CreatedPullRequestCount.ToString(CultureInfo.InvariantCulture)} non-draft, open or merged\n"
                + $"Reviews submitted: {stats.ReviewCount.ToString(CultureInfo.InvariantCulture)}\n\n"
                + $"From: {start}\n"
                + $"To:   {end}\n"
                + $"Scope: {scope}\n"
                + $"GitHub search pages: {stats.ApiCalls.ToString(CultureInfo.InvariantCulture)}\n\n"
                + "Esc or B returns";
        }

        private static DataTable BuildPriorityTable(PullRequestPriority priority)
        {
            var table = new DataTable();
            table.Columns.Add("Rule");
            table.Columns.Add("Points");
            table.Columns.Add("State");

            foreach (var rule in priority.Rules)
            {
                table.Rows.Add(
                    rule.Label,
                    FormatPoints(rule.Points),
                    rule.Applied ? "applied" : "not applied");
            }

            table.Rows.Add("Sum", FormatPoints(priority.Score), priority.Label);
            return table;
        }

        private Tui.TableView.TableStyle BuildPriorityTableStyle(DataTable table, PullRequestPriority priority)
        {
            var style = new Tui.TableView.TableStyle
            {
                AlwaysShowHeaders = true,
                ShowHorizontalHeaderUnderline = true,
                ShowVerticalCellLines = true,
                ShowHorizontalScrollIndicators = false,
                ExpandLastColumn = false,
                RowColorGetter = args => PriorityRuleColor(args.RowIndex, priority),
            };

            style.ColumnStyles[table.Columns[0]!] = new Tui.TableView.ColumnStyle
            {
                MinWidth = 34,
                MaxWidth = Math.Max(34, Math.Min(58, Tui.Application.Driver.Cols - 30)),
                MinAcceptableWidth = 24,
            };
            style.ColumnStyles[table.Columns[1]!] = new Tui.TableView.ColumnStyle
            {
                MinWidth = 8,
                MaxWidth = 8,
                MinAcceptableWidth = 8,
                Alignment = Tui.TextAlignment.Right,
            };
            style.ColumnStyles[table.Columns[2]!] = new Tui.TableView.ColumnStyle
            {
                MinWidth = 14,
                MaxWidth = 18,
                MinAcceptableWidth = 12,
            };

            return style;
        }

        private Tui.ColorScheme PriorityRuleColor(int rowIndex, PullRequestPriority priority)
        {
            if (rowIndex < 0)
            {
                return GrayScheme;
            }

            if (rowIndex >= priority.Rules.Count)
            {
                return HeatColor(priority.Heat);
            }

            var rule = priority.Rules[rowIndex];
            if (!rule.Applied || rule.Points == 0)
            {
                return GrayScheme;
            }

            return rule.Points < 0 ? GreenScheme : RedScheme;
        }

        private static string FormatPoints(int points)
        {
            return points > 0
                ? "+" + points.ToString(CultureInfo.InvariantCulture)
                : points.ToString(CultureInfo.InvariantCulture);
        }

        private void ToggleSelected()
        {
            var selected = SelectedPullRequest();
            if (selected is null)
            {
                return;
            }

            if (_owner.ToggleIgnored(selected))
            {
                Refresh(_isRefreshing, _isCleaning);
            }
        }

        private void ToggleSelectedTop()
        {
            var selected = SelectedPullRequest();
            if (selected is null)
            {
                return;
            }

            if (_owner.ToggleTop(selected))
            {
                Refresh(_isRefreshing, _isCleaning);
            }
        }

        private void EnqueueSelectedReview()
        {
            var selected = SelectedPullRequest();
            if (selected is null)
            {
                return;
            }

            _owner.EnqueueCodexReview(selected);
            Refresh(_isRefreshing, _isCleaning);
        }

        private static Tui.Key BaseKey(Tui.Key key)
        {
            return key & ~(Tui.Key.CtrlMask | Tui.Key.AltMask | Tui.Key.ShiftMask);
        }

        private static bool HasModifier(Tui.Key key, Tui.Key modifier)
        {
            return (key & modifier) == modifier;
        }

        private enum TableSection
        {
            CodexReviewed,
            Top,
            Regular,
        }

        private readonly record struct ColumnLayout(
            int Repo,
            int PullRequest,
            int Title,
            int Author,
            int Created,
            int Approval,
            int Ignore)
        {
            public static ColumnLayout Default { get; } = new(12, 8, 20, 10, 12, 10, 10);
        }

        private sealed record TableRowBinding(PullRequestInfo? PullRequest)
        {
            public static TableRowBinding Empty { get; } = new((PullRequestInfo?)null);

            public static TableRowBinding Separator { get; } = new((PullRequestInfo?)null);
        }
    }

    private sealed class ClickableTableView : Tui.TableView
    {
        public Action<int, int>? CellClicked { get; init; }

        public override bool MouseEvent(Tui.MouseEvent mouseEvent)
        {
            var handled = base.MouseEvent(mouseEvent);
            if (!HasFlag(mouseEvent.Flags, Tui.MouseFlags.Button1Clicked))
            {
                return handled;
            }

            var cell = ScreenToCell(mouseEvent.X, mouseEvent.Y);
            if (!cell.HasValue)
            {
                return handled;
            }

            SetSelection(cell.Value.X, cell.Value.Y, extendExistingSelection: false);
            CellClicked?.Invoke(cell.Value.X, cell.Value.Y);
            mouseEvent.Handled = true;
            return true;
        }

        private static bool HasFlag(Tui.MouseFlags flags, Tui.MouseFlags flag)
        {
            return (flags & flag) == flag;
        }
    }
}

internal sealed class GhClient
{
    private const int SearchPageSize = 100;
    public const int MaxSearchResults = 1_000;
    public const int MaxSearchPages = MaxSearchResults / SearchPageSize;
    private const int MaxSearchQueryLength = 1_800;
    private string? _currentUserLogin;
    private bool _currentUserLoginResolved;
    private readonly ConcurrentDictionary<string, PriorityDetail> _priorityDetails = new(StringComparer.Ordinal);

    private static readonly string GraphQlQuery = $$"""
        query($searchText: String!, $after: String) {
          search(query: $searchText, type: ISSUE, first: {{SearchPageSize}}, after: $after) {
            issueCount
            pageInfo {
              hasNextPage
              endCursor
            }
            nodes {
              ... on PullRequest {
                number
                id
                title
                url
                createdAt
                updatedAt
                authorAssociation
                author {
                  login
                }
                repository {
                  nameWithOwner
                  url
                }
                latestOpinionatedReviews(first: 20) {
                  nodes {
                    state
                    author {
                      login
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private static readonly string WeeklyCreatedPullRequestsQuery = $$"""
        query($searchText: String!, $after: String) {
          search(query: $searchText, type: ISSUE, first: {{SearchPageSize}}, after: $after) {
            pageInfo {
              hasNextPage
              endCursor
            }
            nodes {
              ... on PullRequest {
                createdAt
                isDraft
                merged
                state
                author {
                  login
                }
              }
            }
          }
        }
        """;

    private static readonly string WeeklyReviewedPullRequestsQuery = $$"""
        query($searchText: String!, $after: String, $reviewAuthor: String!) {
          search(query: $searchText, type: ISSUE, first: {{SearchPageSize}}, after: $after) {
            pageInfo {
              hasNextPage
              endCursor
            }
            nodes {
              ... on PullRequest {
                reviews(last: 100, author: $reviewAuthor) {
                  nodes {
                    state
                    submittedAt
                    author {
                      login
                    }
                  }
                }
              }
            }
          }
        }
        """;

    public async Task<FetchResult> FetchPullRequestsAsync(
        IReadOnlyList<RepositoryRef> repositories,
        int requiredApprovals,
        IReadOnlySet<string> ignoredPullRequestKeys,
        PrioritySettings priority,
        CancellationToken cancellationToken,
        bool includePriorityDetails = true,
        int? maxPages = null)
    {
        if (repositories.Count == 0)
        {
            return new FetchResult([], 0, 0, 0, null, IsPartial: false);
        }

        var excludedAuthor = await GetCurrentUserLoginAsync(cancellationToken);
        var items = new List<PullRequestInfo>();
        var apiCalls = 0;
        var openNonDraftCount = 0;
        var isPartial = false;

        foreach (var batch in CreateSearchBatches(repositories, excludedAuthor))
        {
            var batchResult = await FetchPullRequestBatchAsync(batch, requiredApprovals, ignoredPullRequestKeys, excludedAuthor, priority, DateTimeOffset.UtcNow, cancellationToken, maxPages);
            items.AddRange(batchResult.Items);
            apiCalls += batchResult.ApiCalls;
            openNonDraftCount += batchResult.OpenNonDraftCount;
            isPartial |= batchResult.IsPartial;
        }

        var result = new FetchResult(
            items.OrderByDescending(item => item.CreatedAt).ToArray(),
            openNonDraftCount,
            apiCalls,
            repositories.Count,
            excludedAuthor,
            isPartial);

        return includePriorityDetails
            ? await EnrichPullRequestPrioritiesAsync(result, priority, cancellationToken)
            : result;
    }

    public async Task<WeeklyStats> FetchWeeklyStatsAsync(
        IReadOnlyList<RepositoryRef> repositories,
        CancellationToken cancellationToken)
    {
        var currentUserLogin = await GetCurrentUserLoginAsync(cancellationToken)
            ?? throw new InvalidOperationException("failed to resolve GitHub user");
        var nowLocal = DateTimeOffset.Now;
        var weekStartLocal = StartOfWeek(nowLocal, DayOfWeek.Monday);
        if (repositories.Count == 0)
        {
            return new WeeklyStats(
                currentUserLogin,
                weekStartLocal,
                nowLocal,
                TrackedRepositoryCount: 0,
                CreatedPullRequestCount: 0,
                ReviewCount: 0,
                ApiCalls: 0);
        }

        var sinceUtc = weekStartLocal.ToUniversalTime();
        var untilUtc = nowLocal.ToUniversalTime();
        var searchSinceDate = sinceUtc.Date;
        var created = FetchWeeklyCreatedPullRequestCountAsync(
            repositories,
            currentUserLogin,
            sinceUtc,
            untilUtc,
            searchSinceDate,
            cancellationToken);
        var reviewed = FetchWeeklyReviewCountAsync(
            repositories,
            currentUserLogin,
            sinceUtc,
            untilUtc,
            searchSinceDate,
            cancellationToken);

        await Task.WhenAll(created, reviewed);
        return new WeeklyStats(
            currentUserLogin,
            weekStartLocal,
            nowLocal,
            repositories.Count,
            created.Result.Count,
            reviewed.Result.Count,
            created.Result.ApiCalls + reviewed.Result.ApiCalls);
    }

    public async Task<FetchResult> EnrichPullRequestPrioritiesAsync(
        FetchResult result,
        PrioritySettings priority,
        CancellationToken cancellationToken)
    {
        if (result.Items.Count == 0)
        {
            return result;
        }

        var activeNodeIds = result.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.NodeId))
            .Select(item => item.NodeId)
            .ToHashSet(StringComparer.Ordinal);
        if (activeNodeIds.Count == 0)
        {
            return result;
        }

        foreach (var cachedNodeId in _priorityDetails.Keys)
        {
            if (!activeNodeIds.Contains(cachedNodeId))
            {
                _priorityDetails.TryRemove(cachedNodeId, out _);
            }
        }

        var chunks = result.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.NodeId)
                && (!_priorityDetails.TryGetValue(item.NodeId, out var cached)
                    || cached.UpdatedAt != item.UpdatedAt))
            .Chunk(20)
            .ToArray();
        var apiCalls = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var chunk in chunks)
        {
            var details = await FetchPriorityDetailsChunkAsync(chunk, cancellationToken);
            apiCalls++;
            foreach (var detail in details)
            {
                _priorityDetails[detail.NodeId] = detail;
            }
        }

        var items = result.Items
            .Select(item => ApplyPriorityDetails(item, result.ExcludedAuthor, priority, now))
            .ToArray();

        return result with
        {
            Items = items,
            ApiCalls = result.ApiCalls + apiCalls,
        };
    }

    private static async Task<WeeklyCount> FetchWeeklyCreatedPullRequestCountAsync(
        IReadOnlyList<RepositoryRef> repositories,
        string currentUserLogin,
        DateTimeOffset sinceUtc,
        DateTimeOffset untilUtc,
        DateTime searchSinceDate,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var apiCalls = 0;

        foreach (var batch in CreateSearchBatches(
            repositories,
            repos => BuildWeeklyCreatedPullRequestsSearchText(repos, currentUserLogin, searchSinceDate)))
        {
            var searchText = BuildWeeklyCreatedPullRequestsSearchText(batch, currentUserLogin, searchSinceDate);
            string? cursor = null;
            var hasNextPage = true;
            var pages = 0;

            while (hasNextPage && pages < MaxSearchPages)
            {
                pages++;
                apiCalls++;
                using var page = await RunGraphQlSearchPageAsync(
                    WeeklyCreatedPullRequestsQuery,
                    searchText,
                    cursor,
                    cancellationToken);
                var search = page.RootElement.GetProperty("data").GetProperty("search");
                foreach (var node in search.GetProperty("nodes").EnumerateArray())
                {
                    if (ShouldCountWeeklyCreatedPullRequest(node, currentUserLogin, sinceUtc, untilUtc))
                    {
                        count++;
                    }
                }

                var pageInfo = search.GetProperty("pageInfo");
                hasNextPage = pageInfo.GetProperty("hasNextPage").GetBoolean();
                cursor = hasNextPage ? pageInfo.GetProperty("endCursor").GetString() : null;
            }
        }

        return new WeeklyCount(count, apiCalls);
    }

    private static async Task<WeeklyCount> FetchWeeklyReviewCountAsync(
        IReadOnlyList<RepositoryRef> repositories,
        string currentUserLogin,
        DateTimeOffset sinceUtc,
        DateTimeOffset untilUtc,
        DateTime searchSinceDate,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var apiCalls = 0;

        foreach (var batch in CreateSearchBatches(
            repositories,
            repos => BuildWeeklyReviewedPullRequestsSearchText(repos, currentUserLogin, searchSinceDate)))
        {
            var searchText = BuildWeeklyReviewedPullRequestsSearchText(batch, currentUserLogin, searchSinceDate);
            string? cursor = null;
            var hasNextPage = true;
            var pages = 0;

            while (hasNextPage && pages < MaxSearchPages)
            {
                pages++;
                apiCalls++;
                using var page = await RunGraphQlSearchPageAsync(
                    WeeklyReviewedPullRequestsQuery,
                    searchText,
                    cursor,
                    cancellationToken,
                    ("reviewAuthor", currentUserLogin));
                var search = page.RootElement.GetProperty("data").GetProperty("search");
                foreach (var node in search.GetProperty("nodes").EnumerateArray())
                {
                    count += CountWeeklyReviews(node, currentUserLogin, sinceUtc, untilUtc);
                }

                var pageInfo = search.GetProperty("pageInfo");
                hasNextPage = pageInfo.GetProperty("hasNextPage").GetBoolean();
                cursor = hasNextPage ? pageInfo.GetProperty("endCursor").GetString() : null;
            }
        }

        return new WeeklyCount(count, apiCalls);
    }

    private static async Task<IReadOnlyList<PriorityDetail>> FetchPriorityDetailsChunkAsync(
        PullRequestInfo[] pullRequests,
        CancellationToken cancellationToken)
    {
        using var document = await RunGraphQlQueryAsync(BuildPriorityDetailsQuery(pullRequests), cancellationToken);
        var data = document.RootElement.GetProperty("data");
        var details = new List<PriorityDetail>(pullRequests.Length);

        for (var index = 0; index < pullRequests.Length; index++)
        {
            var alias = $"pr{index.ToString(CultureInfo.InvariantCulture)}";
            if (!data.TryGetProperty(alias, out var node)
                || node.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            var pullRequest = pullRequests[index];
            var nodeId = node.TryGetProperty("id", out var idProperty)
                ? idProperty.GetString() ?? pullRequest.NodeId
                : pullRequest.NodeId;
            var updatedAt = node.TryGetProperty("updatedAt", out var updatedAtProperty)
                ? DateTimeOffset.Parse(
                    updatedAtProperty.GetString() ?? "",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal)
                : pullRequest.UpdatedAt;
            var commentAuthors = CommentAuthors(node)
                .Concat(ApprovalAuthors(node))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var reviewAuthors = ReviewAuthors(node)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var requestedReviewers = ReviewRequestedUsers(node)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            details.Add(new PriorityDetail(
                nodeId,
                updatedAt,
                commentAuthors,
                reviewAuthors,
                requestedReviewers));
        }

        return details;
    }

    private static string BuildPriorityDetailsQuery(IReadOnlyList<PullRequestInfo> pullRequests)
    {
        var builder = new StringBuilder();
        builder.AppendLine("query {");
        for (var index = 0; index < pullRequests.Count; index++)
        {
            builder
                .Append("  pr")
                .Append(index.ToString(CultureInfo.InvariantCulture))
                .Append(": node(id: ")
                .Append(JsonSerializer.Serialize(pullRequests[index].NodeId))
                .AppendLine(") {");
            builder.AppendLine("    ... on PullRequest {");
            builder.AppendLine("      id");
            builder.AppendLine("      updatedAt");
            builder.AppendLine("      comments(first: 50) {");
            builder.AppendLine("        nodes {");
            builder.AppendLine("          author {");
            builder.AppendLine("            login");
            builder.AppendLine("          }");
            builder.AppendLine("        }");
            builder.AppendLine("      }");
            builder.AppendLine("      reviews(first: 50) {");
            builder.AppendLine("        nodes {");
            builder.AppendLine("          state");
            builder.AppendLine("          author {");
            builder.AppendLine("            login");
            builder.AppendLine("          }");
            builder.AppendLine("          comments(first: 20) {");
            builder.AppendLine("            nodes {");
            builder.AppendLine("              author {");
            builder.AppendLine("                login");
            builder.AppendLine("              }");
            builder.AppendLine("            }");
            builder.AppendLine("          }");
            builder.AppendLine("        }");
            builder.AppendLine("      }");
            builder.AppendLine("      reviewRequests(first: 100) {");
            builder.AppendLine("        nodes {");
            builder.AppendLine("          requestedReviewer {");
            builder.AppendLine("            ... on User {");
            builder.AppendLine("              login");
            builder.AppendLine("            }");
            builder.AppendLine("          }");
            builder.AppendLine("        }");
            builder.AppendLine("      }");
            builder.AppendLine("    }");
            builder.AppendLine("  }");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private PullRequestInfo ApplyPriorityDetails(
        PullRequestInfo item,
        string? currentUserLogin,
        PrioritySettings priority,
        DateTimeOffset now)
    {
        if (!_priorityDetails.TryGetValue(item.NodeId, out var details))
        {
            return item;
        }

        var reviewRequestedFromUser = item.Priority.ReviewRequestedFromUser
            || (!string.IsNullOrWhiteSpace(currentUserLogin)
                && details.RequestedReviewers.Contains(currentUserLogin, StringComparer.OrdinalIgnoreCase));
        var updatedPriority = priority.Calculate(
            item.CreatedAt,
            item.Author,
            currentUserLogin,
            details.CommentAuthors,
            details.ReviewAuthors,
            reviewRequestedFromUser,
            now);
        return item with { Priority = updatedPriority };
    }

    private static async Task<FetchResult> FetchPullRequestBatchAsync(
        IReadOnlyList<RepositoryRef> repositories,
        int requiredApprovals,
        IReadOnlySet<string> ignoredPullRequestKeys,
        string? excludedAuthor,
        PrioritySettings priority,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        int? maxPages)
    {
        var searchText = BuildSearchText(repositories, excludedAuthor);
        var items = new List<PullRequestInfo>();
        string? cursor = null;
        var hasNextPage = true;
        var apiCalls = 0;
        var openNonDraftCount = 0;

        while (hasNextPage && (!maxPages.HasValue || apiCalls < maxPages.Value))
        {
            apiCalls++;
            using var page = await RunGraphQlPageAsync(searchText, cursor, cancellationToken);
            var search = page.RootElement.GetProperty("data").GetProperty("search");

            if (apiCalls == 1)
            {
                openNonDraftCount = search.GetProperty("issueCount").GetInt32();
            }

            foreach (var node in search.GetProperty("nodes").EnumerateArray())
            {
                var pr = ParsePullRequest(node, excludedAuthor, priority, now);
                if (string.Equals(pr.Author, excludedAuthor, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ignoredPullRequestKeys.Contains(pr.Key))
                {
                    continue;
                }

                if (pr.ApprovalCount < requiredApprovals)
                {
                    items.Add(pr);
                }
            }

            var pageInfo = search.GetProperty("pageInfo");
            hasNextPage = pageInfo.GetProperty("hasNextPage").GetBoolean();
            cursor = hasNextPage ? pageInfo.GetProperty("endCursor").GetString() : null;
        }

        return new FetchResult(items, openNonDraftCount, apiCalls, repositories.Count, excludedAuthor, IsPartial: hasNextPage);
    }

    private static IReadOnlyList<IReadOnlyList<RepositoryRef>> CreateSearchBatches(
        IReadOnlyList<RepositoryRef> repositories,
        string? excludedAuthor)
    {
        return CreateSearchBatches(repositories, repos => BuildSearchText(repos, excludedAuthor));
    }

    private static IReadOnlyList<IReadOnlyList<RepositoryRef>> CreateSearchBatches(
        IReadOnlyList<RepositoryRef> repositories,
        Func<IReadOnlyList<RepositoryRef>, string> buildSearchText)
    {
        var batches = new List<IReadOnlyList<RepositoryRef>>();
        var current = new List<RepositoryRef>();

        foreach (var repo in repositories)
        {
            current.Add(repo);
            if (buildSearchText(current).Length <= MaxSearchQueryLength)
            {
                continue;
            }

            current.RemoveAt(current.Count - 1);
            if (current.Count > 0)
            {
                batches.Add(current.ToArray());
            }

            current = [repo];
        }

        if (current.Count > 0)
        {
            batches.Add(current.ToArray());
        }

        return batches;
    }

    private static string BuildSearchText(IReadOnlyList<RepositoryRef> repositories, string? excludedAuthor)
    {
        var repoQualifiers = string.Join(" ", repositories.Select(repo => $"repo:{repo.FullName}"));
        var authorExclusion = string.IsNullOrWhiteSpace(excludedAuthor) ? "" : $" -author:{excludedAuthor}";
        return $"{repoQualifiers} is:pr is:open draft:false{authorExclusion} sort:created-desc";
    }

    private static string BuildWeeklyCreatedPullRequestsSearchText(
        IReadOnlyList<RepositoryRef> repositories,
        string currentUserLogin,
        DateTime searchSinceDate)
    {
        var repoQualifiers = string.Join(" ", repositories.Select(repo => $"repo:{repo.FullName}"));
        return $"{repoQualifiers} is:pr author:{currentUserLogin} created:>={searchSinceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} draft:false sort:created-desc";
    }

    private static string BuildWeeklyReviewedPullRequestsSearchText(
        IReadOnlyList<RepositoryRef> repositories,
        string currentUserLogin,
        DateTime searchSinceDate)
    {
        var repoQualifiers = string.Join(" ", repositories.Select(repo => $"repo:{repo.FullName}"));
        return $"{repoQualifiers} is:pr reviewed-by:{currentUserLogin} updated:>={searchSinceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} sort:updated-desc";
    }

    private static bool ShouldCountWeeklyCreatedPullRequest(
        JsonElement node,
        string currentUserLogin,
        DateTimeOffset sinceUtc,
        DateTimeOffset untilUtc)
    {
        if (!TryGetLogin(node, out var author)
            || !string.Equals(author, currentUserLogin, StringComparison.OrdinalIgnoreCase)
            || (node.TryGetProperty("isDraft", out var isDraft) && isDraft.GetBoolean()))
        {
            return false;
        }

        var createdAt = DateTimeOffset.Parse(
            node.GetProperty("createdAt").GetString() ?? "",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        if (createdAt < sinceUtc || createdAt > untilUtc)
        {
            return false;
        }

        var state = node.TryGetProperty("state", out var stateProperty)
            ? stateProperty.GetString()
            : "";
        var merged = node.TryGetProperty("merged", out var mergedProperty)
            && mergedProperty.GetBoolean();
        return !string.Equals(state, "CLOSED", StringComparison.Ordinal) || merged;
    }

    private static int CountWeeklyReviews(
        JsonElement node,
        string currentUserLogin,
        DateTimeOffset sinceUtc,
        DateTimeOffset untilUtc)
    {
        if (!node.TryGetProperty("reviews", out var reviews)
            || !reviews.TryGetProperty("nodes", out var reviewNodes))
        {
            return 0;
        }

        var count = 0;
        foreach (var review in reviewNodes.EnumerateArray())
        {
            if (!TryGetLogin(review, out var author)
                || !string.Equals(author, currentUserLogin, StringComparison.OrdinalIgnoreCase)
                || IsPendingReview(review)
                || !review.TryGetProperty("submittedAt", out var submittedAtProperty)
                || submittedAtProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var submittedAt = DateTimeOffset.Parse(
                submittedAtProperty.GetString() ?? "",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal);
            if (submittedAt >= sinceUtc && submittedAt <= untilUtc)
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsPendingReview(JsonElement review)
    {
        return review.TryGetProperty("state", out var state)
            && string.Equals(state.GetString(), "PENDING", StringComparison.Ordinal);
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset value, DayOfWeek firstDayOfWeek)
    {
        var dayOffset = ((int)value.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        return new DateTimeOffset(value.Date.AddDays(-dayOffset), value.Offset);
    }

    private async Task<string?> GetCurrentUserLoginAsync(CancellationToken cancellationToken)
    {
        if (_currentUserLoginResolved)
        {
            return _currentUserLogin;
        }

        _currentUserLogin = await GitHubApiClient.Shared.GetCurrentUserLoginAsync(cancellationToken);
        _currentUserLoginResolved = true;
        return _currentUserLogin;
    }

    private static PullRequestInfo ParsePullRequest(
        JsonElement node,
        string? currentUserLogin,
        PrioritySettings prioritySettings,
        DateTimeOffset now)
    {
        var repository = ParseRepository(node.GetProperty("repository"));
        var author = node.GetProperty("author").GetProperty("login").GetString() ?? "unknown";
        var createdAt = DateTimeOffset.Parse(
            node.GetProperty("createdAt").GetString() ?? "",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        var updatedAt = DateTimeOffset.Parse(
            node.GetProperty("updatedAt").GetString() ?? "",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        var approvers = node
            .GetProperty("latestOpinionatedReviews")
            .GetProperty("nodes")
            .EnumerateArray()
            .Where(review => string.Equals(review.GetProperty("state").GetString(), "APPROVED", StringComparison.Ordinal))
            .Select(review => review.GetProperty("author").GetProperty("login").GetString())
            .Where(login => !string.IsNullOrWhiteSpace(login))
            .Select(login => login!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var reviewRequestedFromUser = !string.IsNullOrWhiteSpace(currentUserLogin)
            && ReviewRequestedUsers(node).Contains(currentUserLogin, StringComparer.OrdinalIgnoreCase);
        var priority = CalculatePriority(node, author, createdAt, reviewRequestedFromUser, currentUserLogin, prioritySettings, now);

        return new PullRequestInfo(
            node.GetProperty("id").GetString() ?? "",
            repository,
            node.GetProperty("number").GetInt32(),
            node.GetProperty("title").GetString() ?? "(untitled)",
            author,
            node.GetProperty("url").GetString() ?? "",
            createdAt,
            updatedAt,
            approvers,
            priority,
            node.TryGetProperty("authorAssociation", out var authorAssociation)
                ? authorAssociation.GetString() ?? "NONE"
                : "NONE");
    }

    private static PullRequestPriority CalculatePriority(
        JsonElement node,
        string author,
        DateTimeOffset createdAt,
        bool reviewRequestedFromUser,
        string? currentUserLogin,
        PrioritySettings prioritySettings,
        DateTimeOffset now)
    {
        return prioritySettings.Calculate(
            createdAt,
            author,
            currentUserLogin,
            CommentAuthors(node).Concat(ApprovalAuthors(node)),
            ReviewAuthors(node),
            reviewRequestedFromUser,
            now);
    }

    private static IEnumerable<string> CommentAuthors(JsonElement node)
    {
        if (node.TryGetProperty("comments", out var comments)
            && comments.TryGetProperty("nodes", out var commentNodes))
        {
            foreach (var comment in commentNodes.EnumerateArray())
            {
                if (TryGetLogin(comment, out var login))
                {
                    yield return login;
                }
            }
        }

        if (!node.TryGetProperty("reviews", out var reviews)
            || !reviews.TryGetProperty("nodes", out var reviewNodes))
        {
            yield break;
        }

        foreach (var review in reviewNodes.EnumerateArray())
        {
            if (!review.TryGetProperty("comments", out var reviewComments)
                || !reviewComments.TryGetProperty("nodes", out var reviewCommentNodes))
            {
                continue;
            }

            foreach (var comment in reviewCommentNodes.EnumerateArray())
            {
                if (TryGetLogin(comment, out var login))
                {
                    yield return login;
                }
            }
        }
    }

    private static IEnumerable<string> ReviewAuthors(JsonElement node)
    {
        if (!node.TryGetProperty("reviews", out var reviews)
            || !reviews.TryGetProperty("nodes", out var reviewNodes))
        {
            yield break;
        }

        foreach (var review in reviewNodes.EnumerateArray())
        {
            if (TryGetLogin(review, out var login))
            {
                yield return login;
            }
        }
    }

    private static IEnumerable<string> ApprovalAuthors(JsonElement node)
    {
        if (node.TryGetProperty("latestOpinionatedReviews", out var latestOpinionatedReviews)
            && latestOpinionatedReviews.TryGetProperty("nodes", out var latestReviewNodes))
        {
            foreach (var review in latestReviewNodes.EnumerateArray())
            {
                if (IsApprovedReview(review) && TryGetLogin(review, out var login))
                {
                    yield return login;
                }
            }
        }

        if (!node.TryGetProperty("reviews", out var reviews)
            || !reviews.TryGetProperty("nodes", out var reviewNodes))
        {
            yield break;
        }

        foreach (var review in reviewNodes.EnumerateArray())
        {
            if (IsApprovedReview(review) && TryGetLogin(review, out var login))
            {
                yield return login;
            }
        }
    }

    private static bool IsApprovedReview(JsonElement review)
    {
        return review.TryGetProperty("state", out var state)
            && string.Equals(state.GetString(), "APPROVED", StringComparison.Ordinal);
    }

    private static IEnumerable<string> ReviewRequestedUsers(JsonElement node)
    {
        if (!node.TryGetProperty("reviewRequests", out var reviewRequests)
            || !reviewRequests.TryGetProperty("nodes", out var requestNodes))
        {
            yield break;
        }

        foreach (var request in requestNodes.EnumerateArray())
        {
            if (!request.TryGetProperty("requestedReviewer", out var reviewer)
                || reviewer.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || !reviewer.TryGetProperty("login", out var login)
                || login.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = login.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static bool TryGetLogin(JsonElement element, out string login)
    {
        login = "";
        if (!element.TryGetProperty("author", out var author)
            || author.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || !author.TryGetProperty("login", out var loginProperty)
            || loginProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        login = loginProperty.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(login);
    }

    private static RepositoryRef ParseRepository(JsonElement repository)
    {
        var fullName = repository.GetProperty("nameWithOwner").GetString() ?? "";
        return RepositoryRef.TryParse(fullName, out var parsed)
            ? parsed
            : new RepositoryRef("unknown", "unknown");
    }

    private static async Task<JsonDocument> RunGraphQlPageAsync(
        string searchText,
        string? cursor,
        CancellationToken cancellationToken)
    {
        return await RunGraphQlSearchPageAsync(GraphQlQuery, searchText, cursor, cancellationToken);
    }

    private static async Task<JsonDocument> RunGraphQlSearchPageAsync(
        string query,
        string searchText,
        string? cursor,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] variables)
    {
        var variableValues = new Dictionary<string, object?>
        {
            ["searchText"] = searchText,
            ["after"] = string.IsNullOrWhiteSpace(cursor) ? null : cursor,
        };

        foreach (var variable in variables)
        {
            variableValues[variable.Name] = variable.Value;
        }

        return await GitHubApiClient.Shared.GraphQlAsync(query, variableValues, cancellationToken);
    }

    private static async Task<JsonDocument> RunGraphQlQueryAsync(
        string query,
        CancellationToken cancellationToken)
    {
        return await GitHubApiClient.Shared.GraphQlAsync(query, variables: null, cancellationToken);
    }
}

internal sealed class NotificationCleaner
{
    private readonly HashSet<string> _trackedRepositories;
    private readonly IReadOnlyList<IgnoredPullRequest> _ignoredPullRequests;
    private readonly IReadOnlyList<TopPullRequest> _topPullRequests;

    public NotificationCleaner(
        IReadOnlyList<RepositoryRef> trackedRepositories,
        IReadOnlyList<IgnoredPullRequest> ignoredPullRequests,
        IReadOnlyList<TopPullRequest> topPullRequests)
    {
        _trackedRepositories = trackedRepositories
            .Select(repo => repo.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ignoredPullRequests = ignoredPullRequests;
        _topPullRequests = topPullRequests;
    }

    public async Task<CleanupResult> CleanupAsync(CancellationToken cancellationToken)
    {
        if (_trackedRepositories.Count == 0
            && _ignoredPullRequests.Count == 0
            && _topPullRequests.Count == 0)
        {
            return new CleanupResult(0, 0, 0, [], []);
        }

        var notifications = Array.Empty<Notification>();
        var markedRead = 0;
        var skipped = 0;

        if (_trackedRepositories.Count > 0)
        {
            notifications = (await GetUnreadNotificationsAsync(cancellationToken))
                .Where(notification => _trackedRepositories.Contains(notification.RepositoryFullName))
                .ToArray();
        }

        var notificationTargets = notifications
            .Select(notification => TryCreateTarget(notification, out var target) ? target : null)
            .Where(target => target is not null)
            .Select(target => target!)
            .ToArray();
        var ignoredTargets = _ignoredPullRequests
            .Select(pullRequest => new SubjectTarget(
                pullRequest.Repository,
                pullRequest.Number))
            .ToArray();
        var topTargets = _topPullRequests
            .Select(pullRequest => new SubjectTarget(
                pullRequest.Repository,
                pullRequest.Number))
            .ToArray();
        var states = await GetSubjectStatesAsync(
            notificationTargets.Concat(ignoredTargets).Concat(topTargets).ToArray(),
            cancellationToken);

        for (var index = 0; index < notifications.Length; index++)
        {
            var notification = notifications[index];
            if (!TryCreateTarget(notification, out var target))
            {
                skipped++;
                continue;
            }

            var shouldMarkRead = !states.TryGetValue(target.Key, out var state)
                || ShouldMarkRead(notification, state);
            if (!shouldMarkRead)
            {
                skipped++;
                continue;
            }

            await MarkThreadReadAsync(notification.Id, cancellationToken);
            markedRead++;
            if (index < notifications.Length - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        var removedIgnoredPullRequests = _ignoredPullRequests
            .Where(pullRequest => states.TryGetValue(
                    SubjectTarget.KeyFor(pullRequest.Repository, pullRequest.Number),
                    out var state)
                && IsClosedPullRequest(state))
            .ToArray();
        var removedTopPullRequests = _topPullRequests
            .Where(pullRequest => states.TryGetValue(
                    SubjectTarget.KeyFor(pullRequest.Repository, pullRequest.Number),
                    out var state)
                && IsClosedPullRequest(state))
            .ToArray();

        return new CleanupResult(
            notifications.Length,
            markedRead,
            skipped,
            removedIgnoredPullRequests,
            removedTopPullRequests);
    }

    private static bool IsClosedPullRequest(SubjectState state)
    {
        return state.IsPullRequest && (state.IsClosed || state.IsMerged);
    }

    private static async Task<IReadOnlyList<Notification>> GetUnreadNotificationsAsync(CancellationToken cancellationToken)
    {
        var notifications = new List<Notification>();
        var items = await GitHubApiClient.Shared.GetPagedArrayAsync("notifications", cancellationToken);
        foreach (var item in items)
        {
            if (!item.TryGetProperty("unread", out var unread)
                || !unread.GetBoolean()
                || !item.TryGetProperty("subject", out var subject)
                || !item.TryGetProperty("repository", out var repository))
            {
                continue;
            }

            var id = GetString(item, "id");
            var subjectUrl = GetString(subject, "url");
            var subjectType = GetString(subject, "type");
            var repositoryFullName = GetString(repository, "full_name");
            if (id.Length > 0 && subjectUrl.Length > 0 && repositoryFullName.Length > 0)
            {
                notifications.Add(new Notification(id, subjectUrl, subjectType, repositoryFullName));
            }
        }

        return notifications;
    }

    private static async Task MarkThreadReadAsync(string threadId, CancellationToken cancellationToken)
    {
        await GitHubApiClient.Shared.PatchAsync($"notifications/threads/{threadId}", cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<string, SubjectState>> GetSubjectStatesAsync(
        IReadOnlyList<SubjectTarget> targets,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, SubjectState>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in targets
            .DistinctBy(target => target.Key, StringComparer.OrdinalIgnoreCase)
            .Chunk(50))
        {
            using var document = await GitHubApiClient.Shared.GraphQlAsync(
                BuildSubjectStatesQuery(chunk),
                variables: null,
                cancellationToken);
            var data = document.RootElement.GetProperty("data");
            for (var index = 0; index < chunk.Length; index++)
            {
                var alias = $"target{index.ToString(CultureInfo.InvariantCulture)}";
                if (!data.TryGetProperty(alias, out var repository)
                    || repository.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    || !repository.TryGetProperty("issueOrPullRequest", out var subject)
                    || subject.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    continue;
                }

                var typeName = GetString(subject, "__typename");
                var state = GetString(subject, "state");
                states[chunk[index].Key] = new SubjectState(
                    IsPullRequest: string.Equals(typeName, "PullRequest", StringComparison.Ordinal),
                    IsClosed: string.Equals(state, "CLOSED", StringComparison.Ordinal)
                        || string.Equals(state, "MERGED", StringComparison.Ordinal),
                    IsMerged: string.Equals(state, "MERGED", StringComparison.Ordinal)
                        || GetBool(subject, "merged"),
                    IsDraft: GetBool(subject, "isDraft"));
            }
        }

        return states;
    }

    private static string BuildSubjectStatesQuery(IReadOnlyList<SubjectTarget> targets)
    {
        var builder = new StringBuilder("query {\n");
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            builder
                .Append("  target")
                .Append(index.ToString(CultureInfo.InvariantCulture))
                .Append(": repository(owner: ")
                .Append(JsonSerializer.Serialize(target.Repository.Owner))
                .Append(", name: ")
                .Append(JsonSerializer.Serialize(target.Repository.Name))
                .AppendLine(") {");
            builder
                .Append("    issueOrPullRequest(number: ")
                .Append(target.Number.ToString(CultureInfo.InvariantCulture))
                .AppendLine(") {");
            builder.AppendLine("      __typename");
            builder.AppendLine("      ... on Issue { state }");
            builder.AppendLine("      ... on PullRequest { state isDraft merged }");
            builder.AppendLine("    }");
            builder.AppendLine("  }");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static bool TryCreateTarget(Notification notification, out SubjectTarget target)
    {
        target = default!;
        if ((!string.Equals(notification.SubjectType, "PullRequest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(notification.SubjectType, "Issue", StringComparison.OrdinalIgnoreCase))
            || !RepositoryRef.TryParse(notification.RepositoryFullName, out var repository)
            || !Uri.TryCreate(notification.SubjectUrl, UriKind.Absolute, out var subjectUri))
        {
            return false;
        }

        var segment = subjectUri.Segments.LastOrDefault()?.Trim('/');
        if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number <= 0)
        {
            return false;
        }

        target = new SubjectTarget(repository, number);
        return true;
    }

    private static bool ShouldMarkRead(Notification notification, SubjectState state)
    {
        if (string.Equals(notification.SubjectType, "PullRequest", StringComparison.OrdinalIgnoreCase))
        {
            return state.IsPullRequest && (state.IsClosed || state.IsMerged || state.IsDraft);
        }

        return string.Equals(notification.SubjectType, "Issue", StringComparison.OrdinalIgnoreCase)
            && !state.IsPullRequest
            && state.IsClosed;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return property.GetString() ?? string.Empty;
    }

    private static bool GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind == JsonValueKind.True
            || (property.ValueKind == JsonValueKind.String
                && bool.TryParse(property.GetString(), out var parsed)
                && parsed);
    }

    private sealed record SubjectTarget(RepositoryRef Repository, int Number)
    {
        public string Key => KeyFor(Repository, Number);

        public static string KeyFor(RepositoryRef repository, int number)
        {
            return $"{repository.FullName}#{number.ToString(CultureInfo.InvariantCulture)}";
        }
    }

    private sealed record SubjectState(bool IsPullRequest, bool IsClosed, bool IsMerged, bool IsDraft);
}

internal static class PullRequests
{
    public static async Task OpenAsync(
        IReadOnlyList<PullRequestTarget> pullRequests,
        IReadOnlyList<RepositoryRef> trackedRepositories,
        CancellationToken cancellationToken)
    {
        foreach (var pullRequest in pullRequests)
        {
            await OpenAsync(pullRequest, trackedRepositories, cancellationToken);
        }
    }

    public static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url)
        {
            UseShellExecute = true,
        });
    }

    public static bool TryParseNumber(string value, out int prNumber)
    {
        var rawPrNumber = value.Trim().TrimStart('#');
        return int.TryParse(rawPrNumber, NumberStyles.None, CultureInfo.InvariantCulture, out prNumber) && prNumber > 0;
    }

    private static async Task OpenAsync(
        PullRequestTarget pullRequest,
        IReadOnlyList<RepositoryRef> trackedRepositories,
        CancellationToken cancellationToken)
    {
        if (pullRequest.Repository is not null)
        {
            OpenUrl(pullRequest.GetUrl(pullRequest.Repository));
            return;
        }

        if (trackedRepositories.Count == 0)
        {
            Console.Error.WriteLine($"Cannot open #{pullRequest.Number}: no repositories are tracked.");
            Console.Error.WriteLine("Add one with: pr https://github.com/OWNER/REPO");
            return;
        }

        if (trackedRepositories.Count == 1)
        {
            OpenUrl(pullRequest.GetUrl(trackedRepositories[0]));
            return;
        }

        var matches = await FindPullRequestMatchesAsync(pullRequest.Number, trackedRepositories, cancellationToken);
        if (matches.Count == 1)
        {
            OpenUrl(matches[0]);
            return;
        }

        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"Could not find PR #{pullRequest.Number} in tracked repositories.");
            Console.Error.WriteLine("Use OWNER/REPO#NUMBER or a full pull request URL to open a specific repo.");
            return;
        }

        Console.Error.WriteLine($"PR #{pullRequest.Number} exists in multiple tracked repositories:");
        foreach (var match in matches)
        {
            Console.Error.WriteLine($"  {match}");
        }

        Console.Error.WriteLine("Use OWNER/REPO#NUMBER or a full pull request URL.");
    }

    private static async Task<IReadOnlyList<string>> FindPullRequestMatchesAsync(
        int prNumber,
        IReadOnlyList<RepositoryRef> trackedRepositories,
        CancellationToken cancellationToken)
    {
        var matches = new List<string>();

        foreach (var repo in trackedRepositories)
        {
            var result = await GhCommand.RunAsync(
                cancellationToken,
                TimeSpan.FromSeconds(20),
                "api",
                $"repos/{repo.FullName}/pulls/{prNumber}");

            if (result.ExitCode != 0)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(result.StandardOutput);
            if (doc.RootElement.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                matches.Add(url.GetString() ?? repo.GetPullRequestUrl(prNumber));
            }
        }

        return matches;
    }
}

internal static class GhCommand
{
    public static async Task<GhResult> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        return await RunAsync(cancellationToken, TimeSpan.FromSeconds(60), args);
    }

    public static async Task<GhResult> RunAsync(CancellationToken cancellationToken, TimeSpan timeout, params string[] args)
    {
        var startInfo = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start gh. Install GitHub CLI and run 'gh auth login'.");

        using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandTimeout.CancelAfter(timeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(commandTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return new GhResult(124, "", $"gh timed out after {timeout.TotalSeconds:0} seconds");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new GhResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}

internal static class NotificationSound
{
    public static void Ding()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Console.Beep(880, 160);
                return;
            }
            catch
            {
            }
        }

        try
        {
            Console.Write('\a');
        }
        catch
        {
        }
    }
}

internal sealed class AppSettings
{
    private const int DefaultRequiredApprovals = 2;
    private static StringComparer LocalPathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly List<RepositoryRef> _repositories;
    private readonly List<string> _localReviewDirectories;
    private readonly List<TopPullRequest> _topPullRequests;
    private readonly List<IgnoredPullRequest> _ignoredPullRequests;

    private AppSettings(
        string settingsPath,
        int requiredApprovals,
        IEnumerable<RepositoryRef> repositories,
        IEnumerable<string> localReviewDirectories,
        IEnumerable<TopPullRequest> topPullRequests,
        IEnumerable<IgnoredPullRequest> ignoredPullRequests,
        PrioritySettings priority,
        CodexReviewSettings codexReview)
    {
        SettingsPath = settingsPath;
        RequiredApprovals = Math.Max(1, requiredApprovals);
        Priority = priority;
        CodexReview = codexReview;
        _repositories = repositories
            .DistinctBy(repo => repo.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _localReviewDirectories = localReviewDirectories
            .Distinct(LocalPathComparer)
            .ToList();
        _topPullRequests = topPullRequests
            .DistinctBy(pr => pr.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _ignoredPullRequests = ignoredPullRequests
            .DistinctBy(pr => pr.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string SettingsPath { get; }

    public int RequiredApprovals { get; private set; }

    public PrioritySettings Priority { get; private set; }

    public CodexReviewSettings CodexReview { get; private set; }

    public IReadOnlyList<RepositoryRef> Repositories => _repositories;

    public IReadOnlyList<string> LocalReviewDirectories => _localReviewDirectories;

    public IReadOnlyList<TopPullRequest> TopPullRequests => _topPullRequests;

    public IReadOnlyList<IgnoredPullRequest> IgnoredPullRequests => _ignoredPullRequests;

    public HashSet<string> TopPullRequestKeys => _topPullRequests
        .Select(pr => pr.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> IgnoredPullRequestKeys => _ignoredPullRequests
        .Select(pr => pr.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static AppSettings Load()
    {
        return Load(GetSettingsPath());
    }

    internal static AppSettings Load(string settingsPath)
    {
        settingsPath = Path.GetFullPath(settingsPath);
        if (!File.Exists(settingsPath))
        {
            return new AppSettings(
                settingsPath,
                DefaultRequiredApprovals,
                [],
                [],
                [],
                [],
                PrioritySettings.Default,
                CodexReviewSettings.Default);
        }

        var repositories = new List<RepositoryRef>();
        var localReviewDirectories = new List<string>();
        var topPullRequests = new List<TopPullRequest>();
        var ignoredPullRequests = new List<IgnoredPullRequest>();
        var requiredApprovals = DefaultRequiredApprovals;
        var priority = PrioritySettings.Default.ToBuilder();
        var codexReview = CodexReviewSettings.Default.ToBuilder();
        codexReview.HasAgentPipeline = false;
        codexReview.Agents.Clear();
        var activeList = SettingsList.None;
        var activeSection = SettingsSection.None;

        var lines = File.ReadAllLines(settingsPath);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var rawLine = lines[lineIndex];
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Equals("repositories:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("repos:", StringComparison.OrdinalIgnoreCase))
            {
                activeList = SettingsList.Repositories;
                activeSection = SettingsSection.None;
                continue;
            }

            if (line.Equals("localReviewDirectories:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("localDirectories:", StringComparison.OrdinalIgnoreCase))
            {
                activeList = SettingsList.LocalReviewDirectories;
                activeSection = SettingsSection.None;
                continue;
            }

            if (line.Equals("ignoredPullRequests:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("ignoredPrs:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("ignored:", StringComparison.OrdinalIgnoreCase))
            {
                activeList = SettingsList.IgnoredPullRequests;
                activeSection = SettingsSection.None;
                continue;
            }

            if (line.Equals("topPullRequests:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("topPrs:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("top:", StringComparison.OrdinalIgnoreCase))
            {
                activeList = SettingsList.TopPullRequests;
                activeSection = SettingsSection.None;
                continue;
            }

            if (line.StartsWith("requiredApprovals:", StringComparison.OrdinalIgnoreCase))
            {
                activeList = SettingsList.None;
                activeSection = SettingsSection.None;
                var value = Unquote(line["requiredApprovals:".Length..].Trim());
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                {
                    requiredApprovals = parsed;
                }

                continue;
            }

            if (line.Equals("priority:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("priorityScoring:", StringComparison.OrdinalIgnoreCase))
            {
                activeList = SettingsList.None;
                activeSection = SettingsSection.Priority;
                continue;
            }

            if (line.Equals("codexReview:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("codexReviewer:", StringComparison.OrdinalIgnoreCase))
            {
                activeList = SettingsList.None;
                activeSection = SettingsSection.CodexReview;
                continue;
            }

            if (activeSection == SettingsSection.Priority
                && (line.Equals("ignoredCommentAuthors:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("ignoredCommentAuthorPatterns:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("ignoredAuthors:", StringComparison.OrdinalIgnoreCase)))
            {
                activeList = SettingsList.PriorityIgnoredCommentAuthors;
                priority.IgnoredCommentAuthorPatterns.Clear();
                continue;
            }

            if (activeSection == SettingsSection.CodexReview
                && (line.Equals("ignoredAuthorPatterns:", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("ignoredAuthors:", StringComparison.OrdinalIgnoreCase)))
            {
                activeList = SettingsList.CodexReviewIgnoredAuthors;
                codexReview.IgnoredAuthorPatterns.Clear();
                continue;
            }

            if (activeSection == SettingsSection.CodexReview
                && (line.Equals("contexts:", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("repositoryContexts:", StringComparison.OrdinalIgnoreCase)))
            {
                activeList = SettingsList.None;
                ReadCodexReviewContexts(
                    lines,
                    ref lineIndex,
                    GetIndentation(rawLine),
                    codexReview.Contexts);
                continue;
            }

            if (activeSection == SettingsSection.CodexReview
                && (line.Equals("agents:", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("reviewers:", StringComparison.OrdinalIgnoreCase)))
            {
                activeList = SettingsList.None;
                ReadCodexReviewAgents(
                    lines,
                    ref lineIndex,
                    GetIndentation(rawLine),
                    codexReview);
                continue;
            }

            if (activeSection == SettingsSection.CodexReview
                && CodexReviewSettings.TryApply(codexReview, line))
            {
                activeList = SettingsList.None;
                continue;
            }

            if (activeSection == SettingsSection.Priority
                && PrioritySettings.TryApply(priority, line))
            {
                activeList = SettingsList.None;
                continue;
            }

            if (activeList != SettingsList.None && line.StartsWith("-", StringComparison.Ordinal))
            {
                var rawValue = line[1..].Trim();
                var value = activeList == SettingsList.LocalReviewDirectories
                    ? UnquoteJsonScalar(rawValue)
                    : Unquote(rawValue);
                if (activeList == SettingsList.Repositories && RepositoryRef.TryParse(value, out var repository))
                {
                    repositories.Add(repository);
                }
                else if (activeList == SettingsList.LocalReviewDirectories
                    && TryResolveLocalReviewDirectory(settingsPath, value, out var localDirectory))
                {
                    localReviewDirectories.Add(localDirectory);
                }
                else if (activeList == SettingsList.TopPullRequests && TopPullRequest.TryParse(value, out var topPullRequest))
                {
                    topPullRequests.Add(topPullRequest);
                }
                else if (activeList == SettingsList.IgnoredPullRequests && IgnoredPullRequest.TryParse(value, out var ignoredPullRequest))
                {
                    ignoredPullRequests.Add(ignoredPullRequest);
                }
                else if (activeList == SettingsList.PriorityIgnoredCommentAuthors && value.Length > 0)
                {
                    priority.IgnoredCommentAuthorPatterns.Add(value);
                }
                else if (activeList == SettingsList.CodexReviewIgnoredAuthors && value.Length > 0)
                {
                    codexReview.IgnoredAuthorPatterns.Add(value);
                }
            }
        }

        return new AppSettings(
            settingsPath,
            requiredApprovals,
            repositories,
            localReviewDirectories,
            topPullRequests,
            ignoredPullRequests,
            priority.Build(),
            codexReview.Build());
    }

    public SettingsUpdateResult AddRepositories(IReadOnlyList<RepositoryRef> repositories)
    {
        var added = new List<RepositoryRef>();
        var alreadyTracked = new List<RepositoryRef>();

        foreach (var repository in repositories)
        {
            if (_repositories.Any(existing => string.Equals(existing.FullName, repository.FullName, StringComparison.OrdinalIgnoreCase)))
            {
                alreadyTracked.Add(repository);
                continue;
            }

            _repositories.Add(repository);
            added.Add(repository);
        }

        return new SettingsUpdateResult(added, alreadyTracked);
    }

    public LocalDirectorySettingsUpdate AddLocalReviewDirectory(string directory)
    {
        var normalized = ResolveLocalReviewDirectory(SettingsPath, directory);
        if (_localReviewDirectories.Contains(normalized, LocalPathComparer))
        {
            return new LocalDirectorySettingsUpdate(normalized, Added: false);
        }

        _localReviewDirectories.Add(normalized);
        return new LocalDirectorySettingsUpdate(normalized, Added: true);
    }

    public bool RemoveLocalReviewDirectory(string directory)
    {
        var normalized = ResolveLocalReviewDirectory(SettingsPath, directory);
        return _localReviewDirectories.RemoveAll(candidate => LocalPathComparer.Equals(candidate, normalized)) > 0;
    }

    public bool ReplaceLocalReviewDirectories(IReadOnlyList<string> directories)
    {
        var replacement = directories
            .Distinct(LocalPathComparer)
            .ToArray();
        if (_localReviewDirectories.SequenceEqual(replacement, LocalPathComparer))
        {
            return false;
        }

        _localReviewDirectories.Clear();
        _localReviewDirectories.AddRange(replacement);
        return true;
    }

    public bool AddIgnoredPullRequest(IgnoredPullRequest pullRequest)
    {
        if (_ignoredPullRequests.Any(existing => string.Equals(existing.Key, pullRequest.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _ignoredPullRequests.Add(pullRequest);
        return true;
    }

    public bool AddTopPullRequest(TopPullRequest pullRequest)
    {
        if (_topPullRequests.Any(existing => string.Equals(existing.Key, pullRequest.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _topPullRequests.Insert(0, pullRequest);
        return true;
    }

    public bool RemoveTopPullRequests(IReadOnlyList<TopPullRequest> pullRequests)
    {
        if (pullRequests.Count == 0)
        {
            return false;
        }

        var keysToRemove = pullRequests
            .Select(pr => pr.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldCount = _topPullRequests.Count;
        _topPullRequests.RemoveAll(pr => keysToRemove.Contains(pr.Key));
        return _topPullRequests.Count != oldCount;
    }

    public bool RemoveIgnoredPullRequests(IReadOnlyList<IgnoredPullRequest> pullRequests)
    {
        if (pullRequests.Count == 0)
        {
            return false;
        }

        var keysToRemove = pullRequests
            .Select(pr => pr.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldCount = _ignoredPullRequests.Count;
        _ignoredPullRequests.RemoveAll(pr => keysToRemove.Contains(pr.Key));
        return _ignoredPullRequests.Count != oldCount;
    }

    public bool ReplaceIgnoredPullRequests(IReadOnlyList<IgnoredPullRequest> pullRequests)
    {
        var current = IgnoredPullRequestKeys;
        var replacement = pullRequests
            .Select(pr => pr.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (current.SetEquals(replacement))
        {
            return false;
        }

        _ignoredPullRequests.Clear();
        _ignoredPullRequests.AddRange(pullRequests.DistinctBy(pr => pr.Key, StringComparer.OrdinalIgnoreCase));
        return true;
    }

    public bool ReplaceTopPullRequests(IReadOnlyList<TopPullRequest> pullRequests)
    {
        var replacement = pullRequests
            .DistinctBy(pr => pr.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentKeys = _topPullRequests.Select(pr => pr.Key);
        var replacementKeys = replacement.Select(pr => pr.Key);
        if (currentKeys.SequenceEqual(replacementKeys, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _topPullRequests.Clear();
        _topPullRequests.AddRange(replacement);
        return true;
    }

    public bool ReplacePriority(PrioritySettings priority)
    {
        if (Priority.SemanticallyEquals(priority))
        {
            return false;
        }

        Priority = priority;
        return true;
    }

    public bool ReplaceCodexReview(CodexReviewSettings codexReview)
    {
        if (CodexReview.SemanticallyEquals(codexReview))
        {
            return false;
        }

        CodexReview = codexReview;
        return true;
    }

    public bool SetCodexReviewEnabled(bool enabled)
    {
        if (CodexReview.Enabled == enabled)
        {
            return false;
        }

        CodexReview = CodexReview with { Enabled = enabled };
        return true;
    }

    public bool SetCodexReviewAutoSubmit(bool autoSubmit)
    {
        if (CodexReview.AutoSubmit == autoSubmit)
        {
            return false;
        }

        CodexReview = CodexReview with { AutoSubmit = autoSubmit };
        return true;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new StringBuilder();
        builder.AppendLine("# pr user settings");
        builder.AppendLine($"requiredApprovals: {RequiredApprovals.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine("repositories:");

        foreach (var repo in _repositories.OrderBy(repo => repo.FullName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"  - {repo.Url}");
        }

        builder.AppendLine("localReviewDirectories:");
        foreach (var localPath in _localReviewDirectories.OrderBy(path => path, LocalPathComparer))
        {
            builder.AppendLine($"  - {JsonSerializer.Serialize(localPath)}");
        }

        builder.AppendLine("topPullRequests:");
        foreach (var pr in _topPullRequests)
        {
            builder.AppendLine($"  - {pr.Url}");
        }

        builder.AppendLine("ignoredPullRequests:");
        foreach (var pr in _ignoredPullRequests.OrderBy(pr => pr.Repository.FullName, StringComparer.OrdinalIgnoreCase).ThenBy(pr => pr.Number))
        {
            builder.AppendLine($"  - {pr.Url}");
        }

        builder.AppendLine("priority:");
        builder.AppendLine($"  superHotThreshold: {Priority.SuperHotThreshold.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  hotThreshold: {Priority.HotThreshold.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  noComments: {Priority.NoCommentsPoints.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  oneCommenter: {Priority.OneCommenterPoints.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  twoOrMoreCommenters: {Priority.TwoOrMoreCommentersPoints.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  tenDaysNoReviews: {Priority.TenDaysNoReviewsPoints.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  tenDaysOneReview: {Priority.TenDaysOneReviewPoints.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  reviewRequestedFromUser: {Priority.ReviewRequestedFromUserPoints.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  fiveDaysNoReviews: {Priority.FiveDaysNoReviewsPoints.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  fiveDaysOneReview: {Priority.FiveDaysOneReviewPoints.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  noReviewsNoComments: {Priority.NoReviewsNoCommentsPoints.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  twentyDays: {Priority.TwentyDaysPoints.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  lessThanThreeHoursNoComments: {Priority.LessThanThreeHoursNoCommentsPoints.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine("  ignoredCommentAuthors:");
        foreach (var pattern in Priority.IgnoredCommentAuthorPatterns)
        {
            builder.AppendLine($"    - {pattern}");
        }

        builder.AppendLine("codexReview:");
        builder.AppendLine($"  enabled: {CodexReview.Enabled.ToString().ToLowerInvariant()}");
        builder.AppendLine($"  pollIntervalSeconds: {CodexReview.PollIntervalSeconds.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  readyDelayMinutes: {CodexReview.ReadyDelayMinutes.ToString("0.##", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  startupScanDays: {CodexReview.StartupScanDays.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  eligibilityCheckSeconds: {CodexReview.EligibilityCheckSeconds.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  maxOpenPullRequests: {CodexReview.MaxOpenPullRequests.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine("  agents:");
        foreach (var agent in CodexReview.AvailableAgents)
        {
            builder.AppendLine($"    {agent.AgentName}:");
            builder.AppendLine($"      enabled: {agent.Enabled.ToString().ToLowerInvariant()}");
            if (!string.IsNullOrWhiteSpace(agent.Model))
            {
                builder.AppendLine($"      model: {agent.Model}");
            }

            if (!string.IsNullOrWhiteSpace(agent.Effort))
            {
                builder.AppendLine($"      effort: {agent.Effort}");
            }

            if (!string.IsNullOrWhiteSpace(agent.Command))
            {
                builder.AppendLine($"      command: {agent.Command}");
            }
        }

        builder.AppendLine($"  sandbox: {CodexReview.Sandbox}");
        builder.AppendLine($"  ephemeral: {CodexReview.Ephemeral.ToString().ToLowerInvariant()}");
        builder.AppendLine($"  ignoreUserConfig: {CodexReview.IgnoreUserConfig.ToString().ToLowerInvariant()}");
        builder.AppendLine($"  maxFindings: {CodexReview.MaxFindings.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  skipWhenApprovalCountAtLeast: {CodexReview.SkipWhenApprovalCountAtLeast.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  skipWhenUniqueCommentersAtLeast: {CodexReview.SkipWhenUniqueCommentersAtLeast.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  skipOwnPullRequests: {CodexReview.SkipOwnPullRequests.ToString().ToLowerInvariant()}");
        builder.AppendLine($"  timeoutMinutes: {CodexReview.TimeoutMinutes.ToString("0.##", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  failureRetryMinutes: {CodexReview.FailureRetryMinutes.ToString("0.##", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"  postNoFindingsComment: {CodexReview.PostNoFindingsComment.ToString().ToLowerInvariant()}");
        builder.AppendLine($"  autoSubmit: {CodexReview.AutoSubmit.ToString().ToLowerInvariant()}");
        builder.AppendLine($"  processExistingOnFirstRun: {CodexReview.ProcessExistingOnFirstRun.ToString().ToLowerInvariant()}");
        builder.AppendLine($"  dryRun: {CodexReview.DryRun.ToString().ToLowerInvariant()}");
        builder.AppendLine($"  dataDirectory: {CodexReview.DataDirectory}");
        if (!string.IsNullOrWhiteSpace(CodexReview.WorkspaceDirectory))
        {
            builder.AppendLine($"  workspaceDirectory: {CodexReview.WorkspaceDirectory}");
        }

        builder.AppendLine($"  contextDirectory: {CodexReview.ContextDirectory}");
        builder.AppendLine("  contexts:");
        foreach (var context in CodexReview.Contexts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"    {JsonSerializer.Serialize(context.Key)}: |-");
            foreach (var contextLine in NormalizeLineEndings(context.Value).Split('\n'))
            {
                builder.Append("      ");
                builder.AppendLine(contextLine);
            }
        }

        builder.AppendLine("  ignoredAuthorPatterns:");
        foreach (var pattern in CodexReview.IgnoredAuthorPatterns)
        {
            builder.AppendLine($"    - {pattern}");
        }

        File.WriteAllText(SettingsPath, builder.ToString(), Encoding.UTF8);
    }

    private static void ReadCodexReviewAgents(
        IReadOnlyList<string> lines,
        ref int lineIndex,
        int agentsIndent,
        CodexReviewSettingsBuilder settings)
    {
        settings.HasAgentPipeline = true;
        settings.Agents.Clear();
        ReviewAgentSettingsBuilder? activeAgent = null;
        var activeAgentIndent = -1;
        var index = lineIndex + 1;
        while (index < lines.Count)
        {
            var rawLine = lines[index];
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                index++;
                continue;
            }

            var indentation = GetIndentation(rawLine);
            if (indentation <= agentsIndent)
            {
                break;
            }

            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                index++;
                continue;
            }

            if (line.EndsWith(':') && indentation == agentsIndent + 2)
            {
                var agentName = ParseYamlKey(line[..^1].Trim());
                if (CodexReviewSettings.TryParseAgent(agentName, out var agent))
                {
                    activeAgent = ReviewAgentSettings.DefaultFor(agent).ToBuilder();
                    activeAgent.Enabled = true;
                    activeAgentIndent = indentation;
                    settings.Agents.RemoveAll(existing => existing.Agent == agent);
                    settings.Agents.Add(activeAgent);
                }
                else
                {
                    activeAgent = null;
                    activeAgentIndent = -1;
                }

                index++;
                continue;
            }

            if (activeAgent is not null && indentation > activeAgentIndent)
            {
                ReviewAgentSettingsBuilder.TryApply(activeAgent, line);
            }

            index++;
        }

        lineIndex = index - 1;
    }

    private static void ReadCodexReviewContexts(
        IReadOnlyList<string> lines,
        ref int lineIndex,
        int contextsIndent,
        IDictionary<string, string> contexts)
    {
        contexts.Clear();
        var index = lineIndex + 1;
        while (index < lines.Count)
        {
            var rawHeader = lines[index];
            if (string.IsNullOrWhiteSpace(rawHeader))
            {
                index++;
                continue;
            }

            var headerIndent = GetIndentation(rawHeader);
            if (headerIndent <= contextsIndent)
            {
                break;
            }

            var header = StripComment(rawHeader).Trim();
            if (header.Length == 0 || !TryParseLiteralBlockHeader(header, out var key))
            {
                index++;
                continue;
            }

            index++;
            var rawContent = new List<string>();
            while (index < lines.Count)
            {
                var contentLine = lines[index];
                if (string.IsNullOrWhiteSpace(contentLine))
                {
                    rawContent.Add(string.Empty);
                    index++;
                    continue;
                }

                if (GetIndentation(contentLine) <= headerIndent)
                {
                    break;
                }

                rawContent.Add(contentLine);
                index++;
            }

            var contentIndent = rawContent
                .Where(contentLine => contentLine.Length > 0)
                .Select(GetIndentation)
                .DefaultIfEmpty(headerIndent + 2)
                .Min();
            var context = string.Join(
                    "\n",
                    rawContent.Select(contentLine => contentLine.Length >= contentIndent
                        ? contentLine[contentIndent..]
                        : string.Empty))
                .TrimEnd('\n');
            if (key.Length > 0 && !string.IsNullOrWhiteSpace(context))
            {
                contexts[key] = context;
            }
        }

        lineIndex = index - 1;
    }

    private static bool TryParseLiteralBlockHeader(string line, out string key)
    {
        key = string.Empty;
        var separator = FindYamlSeparator(line);
        if (separator <= 0)
        {
            return false;
        }

        var marker = line[(separator + 1)..].Trim();
        if (marker is not ("|" or "|-" or "|+"))
        {
            return false;
        }

        key = ParseYamlKey(line[..separator].Trim());
        return key.Length > 0;
    }

    private static int FindYamlSeparator(string value)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\\' && inDoubleQuote)
            {
                index++;
                continue;
            }

            if (character == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (character == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (character == ':' && !inSingleQuote && !inDoubleQuote)
            {
                return index;
            }
        }

        return -1;
    }

    private static string ParseYamlKey(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        return value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? value[1..^1].Replace("''", "'", StringComparison.Ordinal)
            : value;
    }

    private static int GetIndentation(string line)
    {
        var indentation = 0;
        while (indentation < line.Length && line[indentation] is ' ' or '\t')
        {
            indentation++;
        }

        return indentation;
    }

    private static string NormalizeLineEndings(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');
    }

    internal static string ResolveLocalReviewDirectory(string settingsPath, string configuredPath)
    {
        if (!TryResolveLocalReviewDirectory(settingsPath, configuredPath, out var directory))
        {
            throw new ArgumentException("Local review directory is empty or invalid", nameof(configuredPath));
        }

        return directory;
    }

    private static bool TryResolveLocalReviewDirectory(
        string settingsPath,
        string configuredPath,
        out string directory)
    {
        directory = string.Empty;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (expanded.Length == 0)
            {
                return false;
            }

            if (expanded.StartsWith("~/", StringComparison.Ordinal)
                || expanded.StartsWith("~\\", StringComparison.Ordinal))
            {
                expanded = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    expanded[2..]);
            }

            var settingsDirectory = Path.GetDirectoryName(Path.GetFullPath(settingsPath))
                ?? AppContext.BaseDirectory;
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(settingsDirectory, expanded)));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string GetSettingsPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable(UiLauncher.SettingsPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath.Trim());
        }

        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        return Path.Combine(baseDirectory, ".pr.yml");
    }

    private static string StripComment(string line)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (character == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (character == '#'
                && !inSingleQuote
                && !inDoubleQuote
                && (index == 0 || char.IsWhiteSpace(line[index - 1])))
            {
                return line[..index];
            }
        }

        return line;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string UnquoteJsonScalar(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            }
            catch (JsonException)
            {
            }
        }

        return Unquote(value);
    }
}

internal enum SettingsList
{
    None,
    Repositories,
    LocalReviewDirectories,
    TopPullRequests,
    IgnoredPullRequests,
    PriorityIgnoredCommentAuthors,
    CodexReviewIgnoredAuthors,
}

internal enum SettingsSection
{
    None,
    Priority,
    CodexReview,
}

internal sealed record PrioritySettings(
    int SuperHotThreshold,
    int HotThreshold,
    int NoCommentsPoints,
    int OneCommenterPoints,
    int TwoOrMoreCommentersPoints,
    int TenDaysNoReviewsPoints,
    int TenDaysOneReviewPoints,
    int ReviewRequestedFromUserPoints,
    int FiveDaysNoReviewsPoints,
    int FiveDaysOneReviewPoints,
    int NoReviewsNoCommentsPoints,
    int TwentyDaysPoints,
    int LessThanThreeHoursNoCommentsPoints,
    IReadOnlyList<string> IgnoredCommentAuthorPatterns)
{
    public static PrioritySettings Default { get; } = new(
        SuperHotThreshold: 40,
        HotThreshold: 25,
        NoCommentsPoints: 30,
        OneCommenterPoints: 15,
        TwoOrMoreCommentersPoints: -30,
        TenDaysNoReviewsPoints: 15,
        TenDaysOneReviewPoints: 10,
        ReviewRequestedFromUserPoints: 30,
        FiveDaysNoReviewsPoints: 10,
        FiveDaysOneReviewPoints: 5,
        NoReviewsNoCommentsPoints: 10,
        TwentyDaysPoints: 35,
        LessThanThreeHoursNoCommentsPoints: -30,
        IgnoredCommentAuthorPatterns: ["[bot]", "bot", "codex", "claude", "kimi", "deepseek", "deepcode", "opencode", "copilot"]);

    public PrioritySettingsBuilder ToBuilder()
    {
        return new PrioritySettingsBuilder
        {
            SuperHotThreshold = SuperHotThreshold,
            HotThreshold = HotThreshold,
            NoCommentsPoints = NoCommentsPoints,
            OneCommenterPoints = OneCommenterPoints,
            TwoOrMoreCommentersPoints = TwoOrMoreCommentersPoints,
            TenDaysNoReviewsPoints = TenDaysNoReviewsPoints,
            TenDaysOneReviewPoints = TenDaysOneReviewPoints,
            ReviewRequestedFromUserPoints = ReviewRequestedFromUserPoints,
            FiveDaysNoReviewsPoints = FiveDaysNoReviewsPoints,
            FiveDaysOneReviewPoints = FiveDaysOneReviewPoints,
            NoReviewsNoCommentsPoints = NoReviewsNoCommentsPoints,
            TwentyDaysPoints = TwentyDaysPoints,
            LessThanThreeHoursNoCommentsPoints = LessThanThreeHoursNoCommentsPoints,
            IgnoredCommentAuthorPatterns = IgnoredCommentAuthorPatterns.ToList(),
        };
    }

    public bool SemanticallyEquals(PrioritySettings other)
    {
        return SuperHotThreshold == other.SuperHotThreshold
            && HotThreshold == other.HotThreshold
            && NoCommentsPoints == other.NoCommentsPoints
            && OneCommenterPoints == other.OneCommenterPoints
            && TwoOrMoreCommentersPoints == other.TwoOrMoreCommentersPoints
            && TenDaysNoReviewsPoints == other.TenDaysNoReviewsPoints
            && TenDaysOneReviewPoints == other.TenDaysOneReviewPoints
            && ReviewRequestedFromUserPoints == other.ReviewRequestedFromUserPoints
            && FiveDaysNoReviewsPoints == other.FiveDaysNoReviewsPoints
            && FiveDaysOneReviewPoints == other.FiveDaysOneReviewPoints
            && NoReviewsNoCommentsPoints == other.NoReviewsNoCommentsPoints
            && TwentyDaysPoints == other.TwentyDaysPoints
            && LessThanThreeHoursNoCommentsPoints == other.LessThanThreeHoursNoCommentsPoints
            && IgnoredCommentAuthorPatterns.SequenceEqual(other.IgnoredCommentAuthorPatterns, StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryApply(PrioritySettingsBuilder builder, string line)
    {
        var separator = line.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var key = NormalizeKey(line[..separator]);
        var value = Unquote(line[(separator + 1)..].Trim());
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var points))
        {
            return false;
        }

        switch (key)
        {
            case "superhotthreshold":
                builder.SuperHotThreshold = points;
                return true;
            case "hotthreshold":
                builder.HotThreshold = points;
                return true;
            case "nocomments":
                builder.NoCommentsPoints = points;
                return true;
            case "onecommenter":
            case "onecommentercomments":
                builder.OneCommenterPoints = points;
                return true;
            case "twoormorecommenters":
            case "twocommenters":
                builder.TwoOrMoreCommentersPoints = points;
                return true;
            case "tendaysnoreviews":
                builder.TenDaysNoReviewsPoints = points;
                return true;
            case "tendaysonereview":
            case "tendaysonereviews":
                builder.TenDaysOneReviewPoints = points;
                return true;
            case "reviewrequestedfromuser":
            case "reviewrequested":
                builder.ReviewRequestedFromUserPoints = points;
                return true;
            case "fivedaysnoreviews":
                builder.FiveDaysNoReviewsPoints = points;
                return true;
            case "fivedaysonereview":
            case "fivedaysonereviews":
                builder.FiveDaysOneReviewPoints = points;
                return true;
            case "noreviewsnocomments":
                builder.NoReviewsNoCommentsPoints = points;
                return true;
            case "twentydays":
                builder.TwentyDaysPoints = points;
                return true;
            case "lessthanthreehoursnocomments":
                builder.LessThanThreeHoursNoCommentsPoints = points;
                return true;
            default:
                return false;
        }
    }

    public PullRequestPriority Calculate(
        DateTimeOffset createdAt,
        string author,
        string? currentUserLogin,
        IEnumerable<string> commentAuthors,
        IEnumerable<string> reviewAuthors,
        bool reviewRequestedFromUser,
        DateTimeOffset now)
    {
        var humanCommenters = FilterHumanAuthors(commentAuthors, author, currentUserLogin)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var humanReviewers = FilterHumanAuthors(reviewAuthors, author, currentUserLogin)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commentCount = humanCommenters.Count;
        var reviewCount = humanReviewers.Count;
        var age = now - createdAt;
        var score = 0;
        var rules = new List<PriorityRuleScore>
        {
            new("No human comments or approvals", NoCommentsPoints, commentCount == 0),
            new("One human commenter or approver", OneCommenterPoints, commentCount == 1),
            new("Two or more human commenters/approvers", TwoOrMoreCommentersPoints, commentCount >= 2),
            new("10+ days old and no reviews", TenDaysNoReviewsPoints, age.TotalDays >= 10 && reviewCount == 0),
            new("10+ days old and one reviewer", TenDaysOneReviewPoints, age.TotalDays >= 10 && reviewCount == 1),
            new("Review requested from you", ReviewRequestedFromUserPoints, reviewRequestedFromUser),
            new("5+ days old and no reviews", FiveDaysNoReviewsPoints, age.TotalDays >= 5 && reviewCount == 0),
            new("5+ days old and one reviewer", FiveDaysOneReviewPoints, age.TotalDays >= 5 && reviewCount == 1),
            new("No reviews and no human comments/approvals", NoReviewsNoCommentsPoints, reviewCount == 0 && commentCount == 0),
            new("20+ days old", TwentyDaysPoints, age.TotalDays >= 20),
            new("Less than 3 hours old and no comments/approvals", LessThanThreeHoursNoCommentsPoints, age.TotalHours < 3 && commentCount == 0),
        };

        score = rules
            .Where(rule => rule.Applied)
            .Sum(rule => rule.Points);

        var heat = score > SuperHotThreshold
            ? PullRequestHeat.SuperHot
            : score >= HotThreshold
                ? PullRequestHeat.Hot
                : PullRequestHeat.Green;

        return new PullRequestPriority(score, heat, commentCount, reviewCount, reviewRequestedFromUser, rules);
    }

    private IEnumerable<string> FilterHumanAuthors(IEnumerable<string> authors, string pullRequestAuthor, string? currentUserLogin)
    {
        return authors
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Select(author => author.Trim())
            .Where(author => !string.Equals(author, pullRequestAuthor, StringComparison.OrdinalIgnoreCase))
            .Where(author => string.IsNullOrWhiteSpace(currentUserLogin)
                || !string.Equals(author, currentUserLogin, StringComparison.OrdinalIgnoreCase))
            .Where(author => !IgnoredCommentAuthorPatterns.Any(pattern =>
                author.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeKey(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}

internal sealed class PrioritySettingsBuilder
{
    public int SuperHotThreshold { get; set; }
    public int HotThreshold { get; set; }
    public int NoCommentsPoints { get; set; }
    public int OneCommenterPoints { get; set; }
    public int TwoOrMoreCommentersPoints { get; set; }
    public int TenDaysNoReviewsPoints { get; set; }
    public int TenDaysOneReviewPoints { get; set; }
    public int ReviewRequestedFromUserPoints { get; set; }
    public int FiveDaysNoReviewsPoints { get; set; }
    public int FiveDaysOneReviewPoints { get; set; }
    public int NoReviewsNoCommentsPoints { get; set; }
    public int TwentyDaysPoints { get; set; }
    public int LessThanThreeHoursNoCommentsPoints { get; set; }
    public List<string> IgnoredCommentAuthorPatterns { get; set; } = [];

    public PrioritySettings Build()
    {
        return new PrioritySettings(
            SuperHotThreshold,
            HotThreshold,
            NoCommentsPoints,
            OneCommenterPoints,
            TwoOrMoreCommentersPoints,
            TenDaysNoReviewsPoints,
            TenDaysOneReviewPoints,
            ReviewRequestedFromUserPoints,
            FiveDaysNoReviewsPoints,
            FiveDaysOneReviewPoints,
            NoReviewsNoCommentsPoints,
            TwentyDaysPoints,
            LessThanThreeHoursNoCommentsPoints,
            IgnoredCommentAuthorPatterns
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => pattern.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }
}

internal static class LegacyProtocolHandler
{
    private const string Scheme = "pr-ignore";

    public static void TryUnregister()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Scheme}", throwOnMissingSubKey: false);
        }
        catch
        {
        }
    }
}

internal sealed record CommandLine(
    bool ShowHelp,
    bool LaunchUi,
    bool PrintOnce,
    bool CleanupOnce,
    IReadOnlyList<RepositoryRef> RepositoriesToAdd,
    IReadOnlyList<PullRequestTarget> PullRequests,
    string? Error)
{
    public bool HasRuntimeAction => LaunchUi || PrintOnce || CleanupOnce || PullRequests.Count > 0;

    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return Empty();
        }

        if (args.Length == 1 && IsHelp(args[0]))
        {
            return Empty() with { ShowHelp = true };
        }

        var printOnce = false;
        var cleanupOnce = false;
        var launchUi = false;
        var repositories = new List<RepositoryRef>();
        var pullRequests = new List<PullRequestTarget>();

        foreach (var rawToken in ExpandTokens(args))
        {
            if (IsHelp(rawToken))
            {
                return Empty() with { ShowHelp = true };
            }

            if (string.Equals(rawToken, "--once", StringComparison.OrdinalIgnoreCase))
            {
                printOnce = true;
                continue;
            }

            if (string.Equals(rawToken, "--cleanup-once", StringComparison.OrdinalIgnoreCase))
            {
                cleanupOnce = true;
                continue;
            }

            if (string.Equals(rawToken, "ui", StringComparison.OrdinalIgnoreCase))
            {
                launchUi = true;
                continue;
            }

            if (rawToken.StartsWith("-", StringComparison.Ordinal))
            {
                return Empty() with { Error = $"Unknown option: '{rawToken}'." };
            }

            if (PullRequestTarget.TryParse(rawToken, out var pullRequest))
            {
                pullRequests.Add(pullRequest);
                continue;
            }

            if (RepositoryRef.TryParse(rawToken, out var repository))
            {
                repositories.Add(repository);
                continue;
            }

            return Empty() with { Error = $"Invalid repository, PR number, or option: '{rawToken}'." };
        }

        if (printOnce && cleanupOnce)
        {
            return Empty() with { Error = "--once and --cleanup-once cannot be combined." };
        }

        if (pullRequests.Count > 0 && (printOnce || cleanupOnce))
        {
            return Empty() with { Error = "PR opening cannot be combined with --once or --cleanup-once." };
        }

        if (launchUi && (printOnce || cleanupOnce || pullRequests.Count > 0))
        {
            return Empty() with { Error = "ui cannot be combined with PR opening, --once, or --cleanup-once." };
        }

        return new CommandLine(false, launchUi, printOnce, cleanupOnce, repositories, pullRequests, null);
    }

    private static CommandLine Empty()
    {
        return new CommandLine(false, false, false, false, [], [], null);
    }

    private static IEnumerable<string> ExpandTokens(string[] args)
    {
        foreach (var arg in args)
        {
            foreach (var token in arg.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return token;
            }
        }
    }

    private static bool IsHelp(string value)
    {
        return string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "/?", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record PullRequestTarget(RepositoryRef? Repository, int Number)
{
    public string GetUrl(RepositoryRef repository) => repository.GetPullRequestUrl(Number);

    public static bool TryParse(string value, out PullRequestTarget target)
    {
        target = default!;
        var raw = value.Trim();

        if (PullRequests.TryParseNumber(raw, out var bareNumber))
        {
            target = new PullRequestTarget(null, bareNumber);
            return true;
        }

        if (TryParseUrl(raw, out target))
        {
            return true;
        }

        var hashIndex = raw.LastIndexOf('#');
        if (hashIndex > 0)
        {
            var repoValue = raw[..hashIndex];
            var numberValue = raw[(hashIndex + 1)..];
            if (RepositoryRef.TryParse(repoValue, out var repository)
                && PullRequests.TryParseNumber(numberValue, out var number))
            {
                target = new PullRequestTarget(repository, number);
                return true;
            }
        }

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 4
            && string.Equals(parts[^2], "pull", StringComparison.OrdinalIgnoreCase)
            && PullRequests.TryParseNumber(parts[^1], out var pathNumber)
            && RepositoryRef.TryParse(string.Join('/', parts.Take(parts.Length - 2)), out var pathRepository))
        {
            target = new PullRequestTarget(pathRepository, pathNumber);
            return true;
        }

        return false;
    }

    private static bool TryParseUrl(string value, out PullRequestTarget target)
    {
        target = default!;
        var normalized = value.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)
            ? $"https://{value}"
            : value;

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || !RepositoryRef.IsGitHubHost(uri.Host))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 4
            || !string.Equals(segments[2], "pull", StringComparison.OrdinalIgnoreCase)
            || !PullRequests.TryParseNumber(segments[3], out var number))
        {
            return false;
        }

        var repository = new RepositoryRef(segments[0], RepositoryRef.NormalizeRepositoryName(segments[1]));
        target = new PullRequestTarget(repository, number);
        return true;
    }
}

internal sealed record TopPullRequest(RepositoryRef Repository, int Number)
{
    public string Key => $"{Repository.FullName}#{Number.ToString(CultureInfo.InvariantCulture)}";

    public string Url => Repository.GetPullRequestUrl(Number);

    public static TopPullRequest From(PullRequestInfo pullRequest)
    {
        return new TopPullRequest(pullRequest.Repository, pullRequest.Number);
    }

    public static bool TryParse(string value, out TopPullRequest pullRequest)
    {
        pullRequest = default!;
        if (!PullRequestTarget.TryParse(value, out var target) || target.Repository is null)
        {
            return false;
        }

        pullRequest = new TopPullRequest(target.Repository, target.Number);
        return true;
    }
}

internal sealed record IgnoredPullRequest(RepositoryRef Repository, int Number)
{
    public string Key => $"{Repository.FullName}#{Number.ToString(CultureInfo.InvariantCulture)}";

    public string Url => Repository.GetPullRequestUrl(Number);

    public static IgnoredPullRequest From(PullRequestInfo pullRequest)
    {
        return new IgnoredPullRequest(pullRequest.Repository, pullRequest.Number);
    }

    public static bool TryParse(string value, out IgnoredPullRequest pullRequest)
    {
        pullRequest = default!;
        if (!PullRequestTarget.TryParse(value, out var target) || target.Repository is null)
        {
            return false;
        }

        pullRequest = new IgnoredPullRequest(target.Repository, target.Number);
        return true;
    }
}

internal sealed record RepositoryRef(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    public string Url => $"https://github.com/{Owner}/{Name}";

    public string GetPullRequestUrl(int number) => $"{Url}/pull/{number.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string value, out RepositoryRef repository)
    {
        repository = default!;
        var raw = value.Trim();
        if (raw.Length == 0 || raw.Contains('#', StringComparison.Ordinal))
        {
            return false;
        }

        if (raw.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw["git@github.com:".Length..];
        }
        else if (raw.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            raw = $"https://{raw}";
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (!IsGitHubHost(uri.Host))
            {
                return false;
            }

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length < 2)
            {
                return false;
            }

            return TryCreate(segments[0], segments[1], out repository);
        }

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        return TryCreate(parts[0], parts[1], out repository);
    }

    public static bool IsGitHubHost(string host)
    {
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "www.github.com", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeRepositoryName(string name)
    {
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }

    private static bool TryCreate(string owner, string name, out RepositoryRef repository)
    {
        repository = default!;
        owner = owner.Trim();
        name = NormalizeRepositoryName(name.Trim());

        if (!IsValidPathPart(owner) || !IsValidPathPart(name))
        {
            return false;
        }

        repository = new RepositoryRef(owner, name);
        return true;
    }

    private static bool IsValidPathPart(string value)
    {
        return value.Length > 0
            && !value.Contains('/', StringComparison.Ordinal)
            && !value.Contains('\\', StringComparison.Ordinal)
            && !value.Any(char.IsWhiteSpace);
    }
}

internal sealed record SettingsUpdateResult(
    IReadOnlyList<RepositoryRef> Added,
    IReadOnlyList<RepositoryRef> AlreadyTracked);

internal sealed record LocalDirectorySettingsUpdate(string Directory, bool Added);

internal sealed record FetchResult(
    IReadOnlyList<PullRequestInfo> Items,
    int OpenNonDraftCount,
    int ApiCalls,
    int TrackedRepositoryCount,
    string? ExcludedAuthor,
    bool IsPartial);

internal sealed record WeeklyStats(
    string UserLogin,
    DateTimeOffset WeekStartLocal,
    DateTimeOffset WeekEndLocal,
    int TrackedRepositoryCount,
    int CreatedPullRequestCount,
    int ReviewCount,
    int ApiCalls);

internal readonly record struct WeeklyCount(int Count, int ApiCalls);

internal sealed record PullRequestInfo(
    string NodeId,
    RepositoryRef Repository,
    int Number,
    string Title,
    string Author,
    string Url,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Approvers,
    PullRequestPriority Priority,
    string AuthorAssociation = "NONE")
{
    public int ApprovalCount => Approvers.Count;

    public string Key => $"{Repository.FullName}#{Number.ToString(CultureInfo.InvariantCulture)}";

    public bool IsExternalContributor => GitHubAuthorAssociation.IsExternal(AuthorAssociation);

    public bool MatchesSearch(string searchText)
    {
        var search = searchText.Trim();
        if (search.Length == 0)
        {
            return true;
        }

        var number = Number.ToString(CultureInfo.InvariantCulture);
        return Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || Author.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || $"@{Author}".Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || number.Contains(search, StringComparison.Ordinal)
            || $"#{number}".Contains(search, StringComparison.Ordinal);
    }
}

internal sealed record PriorityDetail(
    string NodeId,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> CommentAuthors,
    IReadOnlyList<string> ReviewAuthors,
    IReadOnlyList<string> RequestedReviewers);

internal sealed record PullRequestPriority(
    int Score,
    PullRequestHeat Heat,
    int HumanCommenterCount,
    int HumanReviewCount,
    bool ReviewRequestedFromUser,
    IReadOnlyList<PriorityRuleScore> Rules)
{
    public string Label => Heat switch
    {
        PullRequestHeat.SuperHot => $"{Score.ToString(CultureInfo.InvariantCulture)} super",
        PullRequestHeat.Hot => $"{Score.ToString(CultureInfo.InvariantCulture)} hot",
        _ => $"{Score.ToString(CultureInfo.InvariantCulture)} green",
    };
}

internal sealed record PriorityRuleScore(
    string Label,
    int Points,
    bool Applied);

internal enum PullRequestHeat
{
    Green,
    Hot,
    SuperHot,
}

internal sealed record Notification(string Id, string SubjectUrl, string SubjectType, string RepositoryFullName);

internal sealed record CleanupResult(
    int Scanned,
    int MarkedRead,
    int Skipped,
    IReadOnlyList<IgnoredPullRequest> RemovedIgnoredPullRequests,
    IReadOnlyList<TopPullRequest> RemovedTopPullRequests);

internal sealed record GhResult(int ExitCode, string StandardOutput, string StandardError);

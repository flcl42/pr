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

    if (commandLine.PrintOnce)
    {
        await PrintOnceAsync(settings, shutdown.Token);
        return;
    }

    if (commandLine.CleanupOnce)
    {
        if (settings.Repositories.Count == 0 && settings.IgnoredPullRequests.Count == 0)
        {
            Console.WriteLine("No tracked repositories. Add one with: pr https://github.com/OWNER/REPO");
            return;
        }

        var result = await new NotificationCleaner(settings.Repositories, settings.IgnoredPullRequests).CleanupAsync(shutdown.Token);
        if (settings.RemoveIgnoredPullRequests(result.RemovedIgnoredPullRequests))
        {
            settings.Save();
        }

        Console.WriteLine($"Scanned {result.Scanned} unread notification(s) from tracked repos; marked {result.MarkedRead} as read; removed {result.RemovedIgnoredPullRequests.Count} closed ignored PR(s).");
        return;
    }

    await new DashboardApp(settings).RunAsync(shutdown.Token);
}
catch (OperationCanceledException)
{
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
    private readonly IReadOnlyList<RepositoryRef> _repositories;
    private readonly int _requiredApprovals;
    private readonly string _settingsPath;
    private readonly GhClient _client = new();
    private IReadOnlyList<PullRequestInfo> _items = [];
    private Dictionary<string, PullRequestPriority> _lastSuccessfulPriorities = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _ignoredPullRequestKeys;
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

    public DashboardApp(AppSettings settings)
    {
        _settings = settings;
        _repositories = settings.Repositories;
        _requiredApprovals = settings.RequiredApprovals;
        _settingsPath = settings.SettingsPath;
        _ignoredPullRequestKeys = settings.IgnoredPullRequestKeys;
        _ignoredPullRequestCount = settings.IgnoredPullRequests.Count;
        _settingsLastWriteUtc = GetSettingsLastWriteUtc();
        _nextCleanup = DateTimeOffset.UtcNow.AddSeconds(20);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Tui.Application.Init();
        using var appCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _appCancellation = appCancellation;
        var dashboard = new DashboardView(this);

        try
        {
            Tui.Application.Driver.SetCursorVisibility(Tui.CursorVisibility.Invisible);
            dashboard.Build(Tui.Application.Top);
            dashboard.Refresh(isRefreshing: _repositories.Count > 0, isCleaning: false);

            var worker = RunBackgroundLoopAsync(dashboard, appCancellation.Token);
            using var cancelRegistration = cancellationToken.Register(RequestStop);

            Tui.Application.Run();
            appCancellation.Cancel();

            try
            {
                await worker;
            }
            catch (OperationCanceledException) when (appCancellation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            _appCancellation = null;
            Tui.Application.Shutdown();
        }
    }

    private async Task RunBackgroundLoopAsync(DashboardView dashboard, CancellationToken cancellationToken)
    {
        Task<FetchResult>? refresh = null;
        Task<FetchResult>? priorityRefresh = null;
        Task<CleanupResult>? cleanup = null;
        var refreshIsFirstPageOnly = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var startedWork = false;

            if (refresh is null && priorityRefresh is null && DateTimeOffset.UtcNow >= _nextRefresh)
            {
                var firstPaintOnly = _lastRefresh is null;
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

            if ((_repositories.Count > 0 || _settings.IgnoredPullRequests.Count > 0) && cleanup is null && DateTimeOffset.UtcNow >= _nextCleanup)
            {
                cleanup = new NotificationCleaner(_repositories, _settings.IgnoredPullRequests).CleanupAsync(cancellationToken);
                startedWork = true;
            }

            if (startedWork)
            {
                RefreshOnUi(dashboard, refresh is not null || priorityRefresh is not null, cleanup is not null);
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
                    refresh = null;
                    refreshIsFirstPageOnly = false;
                }

                RefreshOnUi(dashboard, isRefreshing: refresh is not null || priorityRefresh is not null, cleanup is not null);
            }

            if (priorityRefresh is not null && priorityRefresh.IsCompleted)
            {
                try
                {
                    ApplyFetchResult(await priorityRefresh, dingOnNewItems: false, updateSuccessfulPriorities: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _error = $"priority refresh failed: {ex.Message}";
                    _nextRefresh = DateTimeOffset.UtcNow.Add(FailedRefreshRetryInterval);
                }

                priorityRefresh = null;
                RefreshOnUi(dashboard, refresh is not null, cleanup is not null);
            }

            if (cleanup is not null && cleanup.IsCompleted)
            {
                try
                {
                    ApplyCleanupResult(await cleanup);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _cleanupError = ex.Message;
                    _nextCleanup = DateTimeOffset.UtcNow.Add(FailedCleanupRetryInterval);
                }

                cleanup = null;
                RefreshOnUi(dashboard, refresh is not null || priorityRefresh is not null, isCleaning: false);
            }

            if (ReloadSettingsIfChanged())
            {
                RefreshOnUi(dashboard, refresh is not null || priorityRefresh is not null, cleanup is not null);
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
        if (_settings.RemoveIgnoredPullRequests(result.RemovedIgnoredPullRequests))
        {
            _settings.Save();
            _settingsLastWriteUtc = GetSettingsLastWriteUtc();
            RefreshIgnoredPullRequests();
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

        if (_settings.ReplacePriority(latest.Priority))
        {
            RequestRefresh();
            changed = true;
        }

        return changed;
    }

    private IReadOnlyList<PullRequestInfo> GetVisiblePullRequests(string titleSearch = "")
    {
        var search = titleSearch.Trim();
        var includeIgnored = search.Length > 0 || _showIgnoredPullRequests;
        var source = includeIgnored
            ? _items
            : _items.Where(item => !IsIgnored(item));

        if (search.Length == 0)
        {
            return source.ToArray();
        }

        return source
            .Where(item => item.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
    }

    private bool IsIgnored(PullRequestInfo pullRequest)
    {
        return _ignoredPullRequestKeys.Contains(pullRequest.Key);
    }

    private bool ToggleIgnored(PullRequestInfo pullRequest)
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
        return true;
    }

    private bool ToggleIgnoredVisibility()
    {
        _showIgnoredPullRequests = !_showIgnoredPullRequests;
        return true;
    }

    private void RequestRefresh()
    {
        _nextRefresh = DateTimeOffset.MinValue;
        _error = null;
    }

    private void RequestCleanup()
    {
        _nextCleanup = DateTimeOffset.MinValue;
        _cleanupError = null;
    }

    private bool CanCleanup()
    {
        return _repositories.Count > 0 || _settings.IgnoredPullRequests.Count > 0;
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
        private string _titleSearch = "";

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
                Width = Tui.Dim.Percent(45),
                Height = 1,
            };
            _status = new Tui.Label
            {
                X = Tui.Pos.Percent(45),
                Y = 0,
                Width = Tui.Dim.Fill(),
                Height = 1,
                TextAlignment = Tui.TextAlignment.Right,
            };
            _summary = new Tui.Label
            {
                X = 1,
                Y = 1,
                Width = Tui.Dim.Percent(45),
                Height = 1,
            };
            _scan = new Tui.Label
            {
                X = Tui.Pos.Percent(45),
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

                _titleSearch = _searchField.Text.ToString() ?? "";
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
            _visiblePullRequests = _owner.GetVisiblePullRequests(_titleSearch);
            _columns = CalculateColumnLayout();
            _table.MaxCellWidth = Math.Max(200, _columns.Title);

            _status.Text = $"{_owner.RepositorySummary()} - {(isRefreshing ? "refreshing" : "watching")}";
            _status.ColorScheme = isRefreshing ? YellowScheme : GreenScheme;
            _summary.Text = SummaryText();
            _scan.Text = ScanText();
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
            var table = BuildDataTable();
            _table.Table = table;
            _table.Style = BuildTableStyle(table);

            if (_visiblePullRequests.Count > 0)
            {
                _table.SelectedRow = Math.Clamp(previousRow, 0, _visiblePullRequests.Count - 1);
                _table.SelectedColumn = Math.Clamp(previousColumn, 0, IgnoreColumn);
            }
            else
            {
                _table.SelectedRow = 0;
                _table.SelectedColumn = 0;
            }

            _table.Update();
            _table.SetNeedsDisplay();
            Tui.Application.Refresh();
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
                return table;
            }

            foreach (var pullRequest in _visiblePullRequests)
            {
                var approvers = pullRequest.Approvers.Count == 0
                    ? "none"
                    : Truncate(string.Join(", ", pullRequest.Approvers), 36);
                var title = _owner.IsIgnored(pullRequest)
                    ? $"(ignored) {pullRequest.Title}"
                    : pullRequest.Title;

                table.Rows.Add(
                    FitCell(pullRequest.Repository.Name, _columns.Repo),
                    FitCell($"#{pullRequest.Number.ToString(CultureInfo.InvariantCulture)}", _columns.PullRequest),
                    FitCell(title, _columns.Title, underline: true),
                    FitCell("@" + pullRequest.Author, _columns.Author),
                    FitCell(pullRequest.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture), _columns.Created),
                    FitCell($"{pullRequest.ApprovalCount.ToString(CultureInfo.InvariantCulture)}/{_owner._requiredApprovals.ToString(CultureInfo.InvariantCulture)} {approvers}", _columns.Approval),
                    FitCell(_owner.IsIgnored(pullRequest) ? "unignore" : "ignore", _columns.Ignore, Tui.TextAlignment.Centered));
            }

            return table;
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
                RowColorGetter = args => args.RowIndex >= 0
                    && args.RowIndex < _visiblePullRequests.Count
                    && _owner.IsIgnored(_visiblePullRequests[args.RowIndex])
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
            if (args.RowIndex < 0 || args.RowIndex >= _visiblePullRequests.Count)
            {
                return GrayScheme;
            }

            return _visiblePullRequests[args.RowIndex].ApprovalCount == 0
                ? RedScheme
                : YellowScheme;
        }

        private Tui.ColorScheme LinkColor(Tui.TableView.CellColorGetterArgs args)
        {
            if (args.RowIndex < 0)
            {
                return GrayScheme;
            }

            return args.RowIndex < _visiblePullRequests.Count
                && _owner.IsIgnored(_visiblePullRequests[args.RowIndex])
                    ? IgnoredScheme
                    : LinkScheme;
        }

        private Tui.ColorScheme PullRequestColor(Tui.TableView.CellColorGetterArgs args)
        {
            if (args.RowIndex < 0 || args.RowIndex >= _visiblePullRequests.Count)
            {
                return GrayScheme;
            }

            if (_owner.IsIgnored(_visiblePullRequests[args.RowIndex]))
            {
                return IgnoredScheme;
            }

            return HeatColor(_visiblePullRequests[args.RowIndex].Priority.Heat);
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
            var scanText = _owner._currentUserLogin is null
                ? $"{_owner._openNonDraftCount} open non-draft PRs matched, {_owner._apiCalls} request page(s)"
                : $"{_owner._openNonDraftCount} open non-draft PRs matched, excluding @{_owner._currentUserLogin}, {_owner._apiCalls} request page(s)";
            if (_owner._ignoredPullRequestCount > 0)
            {
                scanText += $", {_owner._ignoredPullRequestCount} ignored";
            }

            return scanText;
        }

        private string SummaryText()
        {
            var search = _titleSearch.Trim();
            if (search.Length > 0)
            {
                return $"{_visiblePullRequests.Count} title match(es) for \"{Truncate(search, 32)}\"; ignored included";
            }

            return $"{_visiblePullRequests.Count} visible PRs below {_owner._requiredApprovals} approvals";
        }

        private string FooterText()
        {
            var ignore = _visiblePullRequests.Count == 0
                ? "I ignore disabled"
                : SelectedPullRequest() is { } selected && _owner.IsIgnored(selected)
                    ? "I unignore"
                    : "I ignore";
            var reveal = _owner._showIgnoredPullRequests ? "Ctrl+I hide ignored" : "Ctrl+I show ignored";
            var refresh = _isRefreshing ? "refreshing..." : "R refresh";
            var cleanup = !_owner.CanCleanup()
                ? "C clean disabled"
                : _isCleaning
                    ? "cleaning..."
                    : "C clean";
            var errors = string.Join(" ", new[] { _owner._error, _owner._cleanupError }.Where(error => !string.IsNullOrWhiteSpace(error)));
            var suffix = string.IsNullOrWhiteSpace(errors) ? "" : $"  {errors}";
            var search = string.IsNullOrWhiteSpace(_titleSearch)
                ? (_isSearchActive ? "Esc cancel search" : "F1 search")
                : $"F1 search '{Truncate(_titleSearch, 20)}'  Esc cancel search";
            return $"Up/Down scroll  Enter open  Click Title open  Click Ignore toggle  {ignore}  {reveal}  {search}  {refresh}  {cleanup}  Q quit{suffix}";
        }

        private PullRequestInfo? SelectedPullRequest()
        {
            var row = _table.SelectedRow;
            return row >= 0 && row < _visiblePullRequests.Count
                ? _visiblePullRequests[row]
                : null;
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
            _searchField.Text = _titleSearch;
            _suppressSearchChanged = false;
            Refresh(_isRefreshing, _isCleaning);
            _searchField.SetFocus();
        }

        private void CancelSearch()
        {
            _isSearchActive = false;
            _titleSearch = "";
            _suppressSearchChanged = true;
            _searchField.Text = "";
            _suppressSearchChanged = false;
            Refresh(_isRefreshing, _isCleaning);
            _table.SetFocus();
        }

        private void ActivateCell(int column, int row)
        {
            if (row < 0 || row >= _visiblePullRequests.Count)
            {
                return;
            }

            var pullRequest = _visiblePullRequests[row];
            switch (column)
            {
                case PullRequestColumn:
                    ShowPriorityDialog(pullRequest);
                    break;

                case TitleColumn:
                    PullRequests.OpenUrl(pullRequest.Url);
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
                    + $"Human commenters: {priority.HumanCommenterCount.ToString(CultureInfo.InvariantCulture)}; human reviewers: {priority.HumanReviewCount.ToString(CultureInfo.InvariantCulture)}; requested from you: {(priority.ReviewRequestedFromUser ? "yes" : "no")}",
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

        private static Tui.Key BaseKey(Tui.Key key)
        {
            return key & ~(Tui.Key.CtrlMask | Tui.Key.AltMask | Tui.Key.ShiftMask);
        }

        private static bool HasModifier(Tui.Key key, Tui.Key modifier)
        {
            return (key & modifier) == modifier;
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
    private static readonly TimeSpan GhApiTimeout = TimeSpan.FromMinutes(3);
    private string? _currentUserLogin;
    private bool _currentUserLoginResolved;

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

    public async Task<FetchResult> EnrichPullRequestPrioritiesAsync(
        FetchResult result,
        PrioritySettings priority,
        CancellationToken cancellationToken)
    {
        if (result.Items.Count == 0)
        {
            return result;
        }

        var chunks = result.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.NodeId))
            .Chunk(20)
            .ToArray();
        if (chunks.Length == 0)
        {
            return result;
        }

        var priorities = new ConcurrentDictionary<string, PullRequestPriority>(StringComparer.Ordinal);
        var apiCalls = 0;
        var now = DateTimeOffset.UtcNow;

        await Parallel.ForEachAsync(
            chunks,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 4,
            },
            async (chunk, token) =>
            {
                var details = await FetchPriorityDetailsChunkAsync(chunk, priority, result.ExcludedAuthor, now, token);
                Interlocked.Increment(ref apiCalls);
                foreach (var detail in details)
                {
                    priorities[detail.NodeId] = detail.Priority;
                }
            });

        var items = result.Items
            .Select(item => priorities.TryGetValue(item.NodeId, out var updatedPriority)
                ? item with { Priority = updatedPriority }
                : item)
            .ToArray();

        return result with
        {
            Items = items,
            ApiCalls = result.ApiCalls + apiCalls,
        };
    }

    private static async Task<IReadOnlyList<PriorityDetail>> FetchPriorityDetailsChunkAsync(
        PullRequestInfo[] pullRequests,
        PrioritySettings priority,
        string? currentUserLogin,
        DateTimeOffset now,
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
            var reviewRequestedFromUser = pullRequest.Priority.ReviewRequestedFromUser
                || (!string.IsNullOrWhiteSpace(currentUserLogin)
                    && ReviewRequestedUsers(node).Contains(currentUserLogin, StringComparer.OrdinalIgnoreCase));
            var updatedPriority = CalculatePriority(
                node,
                pullRequest.Author,
                pullRequest.CreatedAt,
                reviewRequestedFromUser,
                currentUserLogin,
                priority,
                now);
            details.Add(new PriorityDetail(nodeId, updatedPriority));
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
            builder.AppendLine("      comments(first: 50) {");
            builder.AppendLine("        nodes {");
            builder.AppendLine("          author {");
            builder.AppendLine("            login");
            builder.AppendLine("          }");
            builder.AppendLine("        }");
            builder.AppendLine("      }");
            builder.AppendLine("      reviews(first: 50) {");
            builder.AppendLine("        nodes {");
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
        var batches = new List<IReadOnlyList<RepositoryRef>>();
        var current = new List<RepositoryRef>();

        foreach (var repo in repositories)
        {
            current.Add(repo);
            if (BuildSearchText(current, excludedAuthor).Length <= MaxSearchQueryLength)
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

    private async Task<string?> GetCurrentUserLoginAsync(CancellationToken cancellationToken)
    {
        if (_currentUserLoginResolved)
        {
            return _currentUserLogin;
        }

        var result = await GhCommand.RunAsync(cancellationToken, TimeSpan.FromSeconds(20), "api", "user", "--jq", ".login");
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException($"failed to resolve GitHub user: {detail.Trim()}");
        }

        _currentUserLogin = result.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(_currentUserLogin))
        {
            throw new InvalidOperationException("failed to resolve GitHub user: gh returned an empty login");
        }

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
            approvers,
            priority);
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
            CommentAuthors(node),
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
        var args = new List<string>
        {
            "api",
            "graphql",
            "-f",
            $"query={GraphQlQuery}",
            "-F",
            $"searchText={searchText}",
        };

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            args.Add("-F");
            args.Add($"after={cursor}");
        }

        return await RunGraphQlAsync(args, cancellationToken);
    }

    private static async Task<JsonDocument> RunGraphQlQueryAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "api",
            "graphql",
            "-f",
            $"query={query}",
        };

        return await RunGraphQlAsync(args, cancellationToken);
    }

    private static async Task<JsonDocument> RunGraphQlAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var result = await GhCommand.RunAsync(cancellationToken, GhApiTimeout, args.ToArray());
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException($"gh api failed: {detail.Trim()}");
        }

        return JsonDocument.Parse(result.StandardOutput);
    }
}

internal sealed class NotificationCleaner
{
    private readonly HashSet<string> _trackedRepositories;
    private readonly IReadOnlyList<IgnoredPullRequest> _ignoredPullRequests;

    public NotificationCleaner(
        IReadOnlyList<RepositoryRef> trackedRepositories,
        IReadOnlyList<IgnoredPullRequest> ignoredPullRequests)
    {
        _trackedRepositories = trackedRepositories
            .Select(repo => repo.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ignoredPullRequests = ignoredPullRequests;
    }

    public async Task<CleanupResult> CleanupAsync(CancellationToken cancellationToken)
    {
        if (_trackedRepositories.Count == 0 && _ignoredPullRequests.Count == 0)
        {
            return new CleanupResult(0, 0, 0, []);
        }

        var notifications = Array.Empty<Notification>();
        var markedRead = 0;
        var skipped = 0;

        if (_trackedRepositories.Count > 0)
        {
            notifications = (await GetUnreadNotificationsAsync(cancellationToken))
                .Where(notification => _trackedRepositories.Contains(notification.RepositoryFullName))
                .ToArray();

            await Parallel.ForEachAsync(
                notifications,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = 6,
                },
                async (notification, token) =>
                {
                    var result = await HandleNotificationAsync(notification, token);
                    if (result == CleanupAction.MarkedRead)
                    {
                        Interlocked.Increment(ref markedRead);
                    }
                    else
                    {
                        Interlocked.Increment(ref skipped);
                    }
                });
        }

        var removedIgnoredPullRequests = await FindClosedIgnoredPullRequestsAsync(cancellationToken);

        return new CleanupResult(notifications.Length, markedRead, skipped, removedIgnoredPullRequests);
    }

    private static async Task<IReadOnlyList<Notification>> GetUnreadNotificationsAsync(CancellationToken cancellationToken)
    {
        const string jqFilter = ".[] | select(.unread == true) | [.id, .subject.url, .subject.type, .repository.full_name] | @tsv";
        var result = await GhCommand.RunAsync(cancellationToken, TimeSpan.FromMinutes(3), "api", "/notifications", "--paginate", "--jq", jqFilter);

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException($"failed to fetch notifications: {detail.Trim()}");
        }

        var notifications = new List<Notification>();
        var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length < 4)
            {
                continue;
            }

            notifications.Add(new Notification(parts[0], parts[1], parts[2], parts[3]));
        }

        return notifications;
    }

    private static async Task<CleanupAction> HandleNotificationAsync(Notification notification, CancellationToken cancellationToken)
    {
        if (string.Equals(notification.SubjectType, "PullRequest", StringComparison.OrdinalIgnoreCase))
        {
            return await HandlePullRequestAsync(notification, cancellationToken);
        }

        if (string.Equals(notification.SubjectType, "Issue", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleIssueAsync(notification, cancellationToken);
        }

        return CleanupAction.Skipped;
    }

    private static async Task<CleanupAction> HandlePullRequestAsync(Notification notification, CancellationToken cancellationToken)
    {
        var prResult = await GhCommand.RunAsync(cancellationToken, TimeSpan.FromSeconds(30), "api", notification.SubjectUrl);

        if (prResult.ExitCode == 0)
        {
            using var doc = JsonDocument.Parse(prResult.StandardOutput);
            var state = GetString(doc.RootElement, "state");
            var merged = GetBool(doc.RootElement, "merged");
            var draft = GetBool(doc.RootElement, "draft");

            if (string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase) || merged || draft)
            {
                await MarkThreadReadAsync(notification.Id, cancellationToken);
                return CleanupAction.MarkedRead;
            }

            return CleanupAction.Skipped;
        }

        await MarkThreadReadAsync(notification.Id, cancellationToken);
        return CleanupAction.MarkedRead;
    }

    private static async Task<CleanupAction> HandleIssueAsync(Notification notification, CancellationToken cancellationToken)
    {
        var issueResult = await GhCommand.RunAsync(cancellationToken, TimeSpan.FromSeconds(30), "api", notification.SubjectUrl);

        if (issueResult.ExitCode == 0)
        {
            using var doc = JsonDocument.Parse(issueResult.StandardOutput);
            var state = GetString(doc.RootElement, "state");

            if (string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase))
            {
                await MarkThreadReadAsync(notification.Id, cancellationToken);
                return CleanupAction.MarkedRead;
            }

            return CleanupAction.Skipped;
        }

        await MarkThreadReadAsync(notification.Id, cancellationToken);
        return CleanupAction.MarkedRead;
    }

    private static async Task MarkThreadReadAsync(string threadId, CancellationToken cancellationToken)
    {
        var result = await GhCommand.RunAsync(cancellationToken, TimeSpan.FromSeconds(30), "api", "--method", "PATCH", $"/notifications/threads/{threadId}");
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException($"failed to mark notification thread {threadId} as read: {detail.Trim()}");
        }
    }

    private async Task<IReadOnlyList<IgnoredPullRequest>> FindClosedIgnoredPullRequestsAsync(CancellationToken cancellationToken)
    {
        if (_ignoredPullRequests.Count == 0)
        {
            return [];
        }

        var closedPullRequests = new List<IgnoredPullRequest>();

        foreach (var pullRequest in _ignoredPullRequests)
        {
            var result = await GhCommand.RunAsync(
                cancellationToken,
                TimeSpan.FromSeconds(30),
                "api",
                $"repos/{pullRequest.Repository.FullName}/pulls/{pullRequest.Number.ToString(CultureInfo.InvariantCulture)}");

            if (result.ExitCode != 0)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(result.StandardOutput);
            var state = GetString(doc.RootElement, "state");
            var merged = GetBool(doc.RootElement, "merged");
            if (string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase) || merged)
            {
                closedPullRequests.Add(pullRequest);
            }
        }

        return closedPullRequests;
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
    private readonly List<RepositoryRef> _repositories;
    private readonly List<IgnoredPullRequest> _ignoredPullRequests;

    private AppSettings(
        string settingsPath,
        int requiredApprovals,
        IEnumerable<RepositoryRef> repositories,
        IEnumerable<IgnoredPullRequest> ignoredPullRequests,
        PrioritySettings priority)
    {
        SettingsPath = settingsPath;
        RequiredApprovals = Math.Max(1, requiredApprovals);
        Priority = priority;
        _repositories = repositories
            .DistinctBy(repo => repo.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _ignoredPullRequests = ignoredPullRequests
            .DistinctBy(pr => pr.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string SettingsPath { get; }

    public int RequiredApprovals { get; private set; }

    public PrioritySettings Priority { get; private set; }

    public IReadOnlyList<RepositoryRef> Repositories => _repositories;

    public IReadOnlyList<IgnoredPullRequest> IgnoredPullRequests => _ignoredPullRequests;

    public HashSet<string> IgnoredPullRequestKeys => _ignoredPullRequests
        .Select(pr => pr.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static AppSettings Load()
    {
        var settingsPath = GetSettingsPath();
        if (!File.Exists(settingsPath))
        {
            return new AppSettings(settingsPath, DefaultRequiredApprovals, [], [], PrioritySettings.Default);
        }

        var repositories = new List<RepositoryRef>();
        var ignoredPullRequests = new List<IgnoredPullRequest>();
        var requiredApprovals = DefaultRequiredApprovals;
        var priority = PrioritySettings.Default.ToBuilder();
        var activeList = SettingsList.None;

        foreach (var rawLine in File.ReadLines(settingsPath))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Equals("repositories:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("repos:", StringComparison.OrdinalIgnoreCase))
            {
                activeList = SettingsList.Repositories;
                continue;
            }

            if (line.Equals("ignoredPullRequests:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("ignoredPrs:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("ignored:", StringComparison.OrdinalIgnoreCase))
            {
                activeList = SettingsList.IgnoredPullRequests;
                continue;
            }

            if (line.StartsWith("requiredApprovals:", StringComparison.OrdinalIgnoreCase))
            {
                activeList = SettingsList.None;
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
                continue;
            }

            if (line.Equals("ignoredCommentAuthors:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("ignoredCommentAuthorPatterns:", StringComparison.OrdinalIgnoreCase)
                || line.Equals("ignoredAuthors:", StringComparison.OrdinalIgnoreCase))
            {
                activeList = SettingsList.PriorityIgnoredCommentAuthors;
                continue;
            }

            if (PrioritySettings.TryApply(priority, line))
            {
                activeList = SettingsList.None;
                continue;
            }

            if (activeList != SettingsList.None && line.StartsWith("-", StringComparison.Ordinal))
            {
                var value = Unquote(line[1..].Trim());
                if (activeList == SettingsList.Repositories && RepositoryRef.TryParse(value, out var repository))
                {
                    repositories.Add(repository);
                }
                else if (activeList == SettingsList.IgnoredPullRequests && IgnoredPullRequest.TryParse(value, out var ignoredPullRequest))
                {
                    ignoredPullRequests.Add(ignoredPullRequest);
                }
                else if (activeList == SettingsList.PriorityIgnoredCommentAuthors && value.Length > 0)
                {
                    priority.IgnoredCommentAuthorPatterns.Add(value);
                }
            }
        }

        return new AppSettings(settingsPath, requiredApprovals, repositories, ignoredPullRequests, priority.Build());
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

    public bool AddIgnoredPullRequest(IgnoredPullRequest pullRequest)
    {
        if (_ignoredPullRequests.Any(existing => string.Equals(existing.Key, pullRequest.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _ignoredPullRequests.Add(pullRequest);
        return true;
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

    public bool ReplacePriority(PrioritySettings priority)
    {
        if (Priority.SemanticallyEquals(priority))
        {
            return false;
        }

        Priority = priority;
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

        File.WriteAllText(SettingsPath, builder.ToString(), Encoding.UTF8);
    }

    private static string GetSettingsPath()
    {
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
}

internal enum SettingsList
{
    None,
    Repositories,
    IgnoredPullRequests,
    PriorityIgnoredCommentAuthors,
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
        IgnoredCommentAuthorPatterns: ["[bot]", "bot", "codex", "claude", "copilot"]);

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
            new("No human comments", NoCommentsPoints, commentCount == 0),
            new("One human commenter", OneCommenterPoints, commentCount == 1),
            new("Two or more human commenters", TwoOrMoreCommentersPoints, commentCount >= 2),
            new("10+ days old and no reviews", TenDaysNoReviewsPoints, age.TotalDays >= 10 && reviewCount == 0),
            new("10+ days old and one reviewer", TenDaysOneReviewPoints, age.TotalDays >= 10 && reviewCount == 1),
            new("Review requested from you", ReviewRequestedFromUserPoints, reviewRequestedFromUser),
            new("5+ days old and no reviews", FiveDaysNoReviewsPoints, age.TotalDays >= 5 && reviewCount == 0),
            new("5+ days old and one reviewer", FiveDaysOneReviewPoints, age.TotalDays >= 5 && reviewCount == 1),
            new("No reviews and no human comments", NoReviewsNoCommentsPoints, reviewCount == 0 && commentCount == 0),
            new("20+ days old", TwentyDaysPoints, age.TotalDays >= 20),
            new("Less than 3 hours old and no comments", LessThanThreeHoursNoCommentsPoints, age.TotalHours < 3 && commentCount == 0),
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
    bool PrintOnce,
    bool CleanupOnce,
    IReadOnlyList<RepositoryRef> RepositoriesToAdd,
    IReadOnlyList<PullRequestTarget> PullRequests,
    string? Error)
{
    public bool HasRuntimeAction => PrintOnce || CleanupOnce || PullRequests.Count > 0;

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

        return new CommandLine(false, printOnce, cleanupOnce, repositories, pullRequests, null);
    }

    private static CommandLine Empty()
    {
        return new CommandLine(false, false, false, [], [], null);
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

internal sealed record FetchResult(
    IReadOnlyList<PullRequestInfo> Items,
    int OpenNonDraftCount,
    int ApiCalls,
    int TrackedRepositoryCount,
    string? ExcludedAuthor,
    bool IsPartial);

internal sealed record PullRequestInfo(
    string NodeId,
    RepositoryRef Repository,
    int Number,
    string Title,
    string Author,
    string Url,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Approvers,
    PullRequestPriority Priority)
{
    public int ApprovalCount => Approvers.Count;

    public string Key => $"{Repository.FullName}#{Number.ToString(CultureInfo.InvariantCulture)}";
}

internal sealed record PriorityDetail(
    string NodeId,
    PullRequestPriority Priority);

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
    IReadOnlyList<IgnoredPullRequest> RemovedIgnoredPullRequests);

internal enum CleanupAction
{
    MarkedRead,
    Skipped,
}

internal sealed record GhResult(int ExitCode, string StandardOutput, string StandardError);

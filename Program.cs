using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

Console.OutputEncoding = Encoding.UTF8;
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
        if (settings.Repositories.Count == 0)
        {
            Console.WriteLine("No tracked repositories. Add one with: pr https://github.com/OWNER/REPO");
            return;
        }

        var result = await new NotificationCleaner(settings.Repositories).CleanupAsync(shutdown.Token);
        Console.WriteLine($"Scanned {result.Scanned} unread notification(s) from tracked repos; marked {result.MarkedRead} as read.");
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
            $"No open non-draft PRs from other authors are currently below {settings.RequiredApprovals} approvals.",
            "",
            "",
            "");
    }
    else
    {
        foreach (var pr in result.Items)
        {
            table.AddRow(
                Markup.Escape(pr.Repository.FullName),
                LinkCell($"#{pr.Number}", pr.Url),
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
}

internal sealed class DashboardApp
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private const int FixedLayoutRows = 16;

    private readonly IReadOnlyList<RepositoryRef> _repositories;
    private readonly int _requiredApprovals;
    private readonly string _settingsPath;
    private readonly GhClient _client = new();
    private readonly NotificationCleaner _notificationCleaner;
    private IReadOnlyList<PullRequestInfo> _items = [];
    private HashSet<string>? _knownPullRequestKeys;
    private DateTimeOffset? _lastRefresh;
    private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastCleanup;
    private DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;
    private string? _error;
    private string? _cleanupError;
    private string? _currentUserLogin;
    private int _apiCalls;
    private int _openNonDraftCount;
    private int _lastCleanupScanned;
    private int _lastCleanupMarkedRead;
    private int _selectedIndex;
    private int _scrollOffset;
    private bool _quit;

    public DashboardApp(AppSettings settings)
    {
        _repositories = settings.Repositories;
        _requiredApprovals = settings.RequiredApprovals;
        _settingsPath = settings.SettingsPath;
        _notificationCleaner = new NotificationCleaner(_repositories);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var originalCursorVisible = TryGetCursorVisible();
        if (originalCursorVisible.HasValue)
        {
            TrySetCursorVisible(false);
        }

        try
        {
            Task<FetchResult>? refresh = null;
            Task<CleanupResult>? cleanup = null;
            var dirty = true;

            while (!_quit && !cancellationToken.IsCancellationRequested)
            {
                if (refresh is null && DateTimeOffset.UtcNow >= _nextRefresh)
                {
                    refresh = _client.FetchPullRequestsAsync(_repositories, _requiredApprovals, cancellationToken);
                    _error = null;
                    dirty = true;
                }

                if (_repositories.Count > 0 && cleanup is null && DateTimeOffset.UtcNow >= _nextCleanup)
                {
                    cleanup = _notificationCleaner.CleanupAsync(cancellationToken);
                    dirty = true;
                }

                if (refresh is not null && refresh.IsCompleted)
                {
                    try
                    {
                        ApplyFetchResult(await refresh);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _error = ex.Message;
                        _nextRefresh = DateTimeOffset.UtcNow.AddSeconds(30);
                    }

                    refresh = null;
                    dirty = true;
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
                        _nextCleanup = DateTimeOffset.UtcNow.AddMinutes(5);
                    }

                    cleanup = null;
                    dirty = true;
                }

                dirty |= HandleKeys(refresh is not null, cleanup is not null);

                if (dirty)
                {
                    Render(refresh is not null, cleanup is not null);
                    dirty = false;
                }

                try
                {
                    await Task.Delay(100, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            AnsiConsole.Clear();
        }
        finally
        {
            if (originalCursorVisible.HasValue)
            {
                TrySetCursorVisible(originalCursorVisible.Value);
            }
        }
    }

    private void ApplyFetchResult(FetchResult result)
    {
        DingOnNewItems(result.Items);
        _items = result.Items;
        _lastRefresh = DateTimeOffset.UtcNow;
        _nextRefresh = _lastRefresh.Value.Add(RefreshInterval);
        _apiCalls = result.ApiCalls;
        _openNonDraftCount = result.OpenNonDraftCount;
        _currentUserLogin = result.ExcludedAuthor;
        _error = null;
        ClampSelection();
    }

    private void ApplyCleanupResult(CleanupResult result)
    {
        _lastCleanup = DateTimeOffset.UtcNow;
        _nextCleanup = _lastCleanup.Value.Add(CleanupInterval);
        _lastCleanupScanned = result.Scanned;
        _lastCleanupMarkedRead = result.MarkedRead;
        _cleanupError = null;
    }

    private void DingOnNewItems(IReadOnlyList<PullRequestInfo> items)
    {
        var latestKeys = items.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_knownPullRequestKeys is not null && latestKeys.Any(key => !_knownPullRequestKeys.Contains(key)))
        {
            NotificationSound.Ding();
        }

        _knownPullRequestKeys = latestKeys;
    }

    private bool HandleKeys(bool isRefreshing, bool isCleaning)
    {
        if (Console.IsInputRedirected)
        {
            return false;
        }

        var dirty = false;

        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    _quit = true;
                    dirty = true;
                    break;

                case ConsoleKey.R when !isRefreshing:
                    _nextRefresh = DateTimeOffset.MinValue;
                    dirty = true;
                    break;

                case ConsoleKey.C when _repositories.Count > 0 && !isCleaning:
                    _nextCleanup = DateTimeOffset.MinValue;
                    dirty = true;
                    break;

                case ConsoleKey.UpArrow:
                case ConsoleKey.K:
                    MoveSelection(-1);
                    dirty = true;
                    break;

                case ConsoleKey.DownArrow:
                case ConsoleKey.J:
                    MoveSelection(1);
                    dirty = true;
                    break;

                case ConsoleKey.PageUp:
                    MoveSelection(-VisibleRowCount());
                    dirty = true;
                    break;

                case ConsoleKey.PageDown:
                    MoveSelection(VisibleRowCount());
                    dirty = true;
                    break;

                case ConsoleKey.Home:
                    _selectedIndex = 0;
                    ClampSelection();
                    dirty = true;
                    break;

                case ConsoleKey.End:
                    _selectedIndex = Math.Max(0, _items.Count - 1);
                    ClampSelection();
                    dirty = true;
                    break;

                case ConsoleKey.Enter:
                    OpenSelectedPullRequest();
                    dirty = true;
                    break;
            }
        }

        return dirty;
    }

    private void MoveSelection(int delta)
    {
        if (_items.Count == 0)
        {
            _selectedIndex = 0;
            _scrollOffset = 0;
            return;
        }

        _selectedIndex = Math.Clamp(_selectedIndex + delta, 0, _items.Count - 1);
        ClampSelection();
    }

    private void ClampSelection()
    {
        if (_items.Count == 0)
        {
            _selectedIndex = 0;
            _scrollOffset = 0;
            return;
        }

        _selectedIndex = Math.Clamp(_selectedIndex, 0, _items.Count - 1);
        var visibleRows = VisibleRowCount();

        if (_selectedIndex < _scrollOffset)
        {
            _scrollOffset = _selectedIndex;
        }
        else if (_selectedIndex >= _scrollOffset + visibleRows)
        {
            _scrollOffset = _selectedIndex - visibleRows + 1;
        }

        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, _items.Count - visibleRows));
    }

    private void OpenSelectedPullRequest()
    {
        if (_items.Count == 0)
        {
            return;
        }

        PullRequests.OpenUrl(_items[_selectedIndex].Url);
    }

    private void Render(bool isRefreshing, bool isCleaning)
    {
        ClampSelection();
        AnsiConsole.Clear();
        AnsiConsole.Write(BuildHeader(isRefreshing, isCleaning));
        AnsiConsole.Write(BuildPullRequestPanel());
        AnsiConsole.Write(BuildFooter(isRefreshing, isCleaning));
    }

    private IRenderable BuildHeader(bool isRefreshing, bool isCleaning)
    {
        var grid = new Grid().Expand();
        grid.AddColumn();
        grid.AddColumn();

        var status = isRefreshing ? "[yellow]refreshing[/]" : "[green]watching[/]";
        var lastRefresh = _lastRefresh is null
            ? "never"
            : _lastRefresh.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        var nextRefresh = isRefreshing
            ? "now"
            : _nextRefresh.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        var lastCleanup = _lastCleanup is null
            ? "never"
            : _lastCleanup.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        var nextCleanup = _repositories.Count == 0
            ? "disabled"
            : isCleaning
                ? "now"
                : _nextCleanup.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        var cleanupText = _repositories.Count == 0
            ? "no tracked repositories"
            : _cleanupError is null
                ? $"{_lastCleanupScanned} scanned, {_lastCleanupMarkedRead} marked read"
                : $"failed: {_cleanupError}";

        var scanText = _currentUserLogin is null
            ? $"{_openNonDraftCount} open non-draft PRs scanned, {_apiCalls} request page(s)"
            : $"{_openNonDraftCount} open non-draft PRs scanned, excluding @{_currentUserLogin}, {_apiCalls} request page(s)";

        grid.AddRow(
            new Markup("[bold aqua]GitHub PR Control Panel[/]"),
            new Markup($"[grey]{Markup.Escape(RepositorySummary())} - {status}[/]"));
        grid.AddRow(
            new Markup($"[bold]{_items.Count}[/] PRs below {_requiredApprovals} approvals"),
            new Markup($"[grey]{Markup.Escape(scanText)}[/]"));
        grid.AddRow(
            new Markup($"[grey]Last refresh: {Markup.Escape(lastRefresh)}[/]"),
            new Markup($"[grey]Next refresh: {Markup.Escape(nextRefresh)}[/]"));
        grid.AddRow(
            new Markup($"[grey]Last cleanup: {Markup.Escape(lastCleanup)}[/]"),
            new Markup($"[grey]Next cleanup: {Markup.Escape(nextCleanup)} - {Markup.Escape(cleanupText)}[/]"));

        return new Panel(grid)
            .Border(BoxBorder.Rounded)
            .Expand();
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

    private IRenderable BuildPullRequestPanel()
    {
        var visibleRows = VisibleRowCount();
        var repoWidth = Math.Clamp(TerminalWidth() / 5, 16, 34);
        var titleWidth = Math.Clamp(TerminalWidth() - repoWidth - 86, 20, 88);
        var approverWidth = Math.Clamp(TerminalWidth() - repoWidth - 104, 10, 32);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Expand();

        table.AddColumn(new TableColumn("").NoWrap());
        table.AddColumn(new TableColumn("[bold]Repo[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]PR[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Title[/]"));
        table.AddColumn(new TableColumn("[bold]Author[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Created[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Approvals[/]").NoWrap());

        if (_items.Count == 0)
        {
            var message = EmptyListMessage();
            table.AddRow(
                TextCell(""),
                TextCell(""),
                TextCell(""),
                TextCell(message),
                TextCell(""),
                TextCell(""),
                TextCell(""));
        }
        else
        {
            foreach (var row in VisibleItems(visibleRows))
            {
                var isSelected = row.Index == _selectedIndex;
                var pr = row.Item;
                var marker = isSelected ? "[black on aqua]>[/]" : "[grey] [/]";
                table.AddRow(
                    new Markup(marker),
                    LinkCell(Truncate(pr.Repository.FullName, repoWidth), pr.Repository.Url),
                    LinkCell($"#{pr.Number}", pr.Url),
                    LinkCell(Truncate(pr.Title, titleWidth), pr.Url),
                    TextCell("@" + pr.Author),
                    TextCell(pr.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)),
                    ApprovalCell(pr, approverWidth));
            }
        }

        var range = _items.Count == 0
            ? "0/0"
            : $"{_scrollOffset + 1}-{Math.Min(_scrollOffset + visibleRows, _items.Count)}/{_items.Count}";
        return new Panel(table)
            .Header($" [bold]Pull requests[/] [grey]{range}[/] ")
            .Border(BoxBorder.Rounded)
            .Expand();
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

        return $"No open non-draft PRs from other authors are currently below {_requiredApprovals} approvals.";
    }

    private IRenderable BuildFooter(bool isRefreshing, bool isCleaning)
    {
        var refresh = isRefreshing ? "[grey]refreshing...[/]" : "[bold]R[/] refresh";
        var cleanup = _repositories.Count == 0
            ? "[grey]C clean disabled[/]"
            : isCleaning
                ? "[grey]cleaning...[/]"
                : "[bold]C[/] clean";
        var error = _error is null ? "" : $" [red]{Markup.Escape(_error)}[/]";
        var cleanupError = _cleanupError is null ? "" : $" [red]{Markup.Escape(_cleanupError)}[/]";
        return new Panel(new Markup(
                $"[bold]Up/Down[/] scroll  [bold]PgUp/PgDn[/] page  [bold]Enter[/] open PR  {refresh}  {cleanup}  [bold]Q[/] quit{error}{cleanupError}"))
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private IEnumerable<(int Index, PullRequestInfo Item)> VisibleItems(int visibleRows)
    {
        var count = Math.Min(visibleRows, Math.Max(0, _items.Count - _scrollOffset));
        for (var index = 0; index < count; index++)
        {
            var itemIndex = _scrollOffset + index;
            yield return (itemIndex, _items[itemIndex]);
        }
    }

    private int VisibleRowCount()
    {
        return Math.Max(1, TerminalHeight() - FixedLayoutRows);
    }

    private static bool? TryGetCursorVisible()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return Console.CursorVisible;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void TrySetCursorVisible(bool visible)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Console.CursorVisible = visible;
        }
        catch (IOException)
        {
        }
    }

    private static int TerminalWidth()
    {
        try
        {
            return Math.Max(80, Console.WindowWidth);
        }
        catch (IOException)
        {
            return 120;
        }
    }

    private static int TerminalHeight()
    {
        try
        {
            return Math.Max(20, Console.WindowHeight);
        }
        catch (IOException)
        {
            return 30;
        }
    }

    private static Markup TextCell(string value)
    {
        return new Markup(Markup.Escape(value));
    }

    private static Markup LinkCell(string value, string url)
    {
        return new Markup($"[link={url}][underline blue]{Markup.Escape(value)}[/][/]");
    }

    private Markup ApprovalCell(PullRequestInfo pr, int approverWidth)
    {
        var color = pr.ApprovalCount == 0 ? "red" : "yellow";
        var approvers = pr.Approvers.Count == 0
            ? "none"
            : Truncate(string.Join(", ", pr.Approvers), approverWidth);
        return new Markup($"[{color}]{pr.ApprovalCount}/{_requiredApprovals}[/] [grey]{Markup.Escape(approvers)}[/]");
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
}

internal sealed class GhClient
{
    private const int MaxSearchQueryLength = 1_800;
    private string? _currentUserLogin;
    private bool _currentUserLoginResolved;

    private const string GraphQlQuery = """
        query($searchText: String!, $after: String) {
          search(query: $searchText, type: ISSUE, first: 100, after: $after) {
            issueCount
            pageInfo {
              hasNextPage
              endCursor
            }
            nodes {
              ... on PullRequest {
                number
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
                latestOpinionatedReviews(first: 100) {
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
        CancellationToken cancellationToken)
    {
        if (repositories.Count == 0)
        {
            return new FetchResult([], 0, 0, 0, null);
        }

        var excludedAuthor = await GetCurrentUserLoginAsync(cancellationToken);
        var items = new List<PullRequestInfo>();
        var apiCalls = 0;
        var openNonDraftCount = 0;

        foreach (var batch in CreateSearchBatches(repositories, excludedAuthor))
        {
            var batchResult = await FetchPullRequestBatchAsync(batch, requiredApprovals, excludedAuthor, cancellationToken);
            items.AddRange(batchResult.Items);
            apiCalls += batchResult.ApiCalls;
            openNonDraftCount += batchResult.OpenNonDraftCount;
        }

        return new FetchResult(
            items.OrderByDescending(item => item.CreatedAt).ToArray(),
            openNonDraftCount,
            apiCalls,
            repositories.Count,
            excludedAuthor);
    }

    private static async Task<FetchResult> FetchPullRequestBatchAsync(
        IReadOnlyList<RepositoryRef> repositories,
        int requiredApprovals,
        string? excludedAuthor,
        CancellationToken cancellationToken)
    {
        var searchText = BuildSearchText(repositories, excludedAuthor);
        var items = new List<PullRequestInfo>();
        string? cursor = null;
        var hasNextPage = true;
        var apiCalls = 0;
        var openNonDraftCount = 0;

        while (hasNextPage)
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
                var pr = ParsePullRequest(node);
                if (string.Equals(pr.Author, excludedAuthor, StringComparison.OrdinalIgnoreCase))
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

        return new FetchResult(items, openNonDraftCount, apiCalls, repositories.Count, excludedAuthor);
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

    private static PullRequestInfo ParsePullRequest(JsonElement node)
    {
        var repository = ParseRepository(node.GetProperty("repository"));
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

        return new PullRequestInfo(
            repository,
            node.GetProperty("number").GetInt32(),
            node.GetProperty("title").GetString() ?? "(untitled)",
            node.GetProperty("author").GetProperty("login").GetString() ?? "unknown",
            node.GetProperty("url").GetString() ?? "",
            DateTimeOffset.Parse(
                node.GetProperty("createdAt").GetString() ?? "",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal),
            approvers);
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

        var result = await GhCommand.RunAsync(cancellationToken, args.ToArray());
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

    public NotificationCleaner(IReadOnlyList<RepositoryRef> trackedRepositories)
    {
        _trackedRepositories = trackedRepositories
            .Select(repo => repo.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<CleanupResult> CleanupAsync(CancellationToken cancellationToken)
    {
        if (_trackedRepositories.Count == 0)
        {
            return new CleanupResult(0, 0, 0);
        }

        var notifications = await GetUnreadNotificationsAsync(cancellationToken);
        notifications = notifications
            .Where(notification => _trackedRepositories.Contains(notification.RepositoryFullName))
            .ToArray();

        var markedRead = 0;
        var skipped = 0;

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

        return new CleanupResult(notifications.Count, markedRead, skipped);
    }

    private static async Task<IReadOnlyList<Notification>> GetUnreadNotificationsAsync(CancellationToken cancellationToken)
    {
        const string jqFilter = ".[] | select(.unread == true) | [.id, .subject.url, .subject.type, .repository.full_name] | @tsv";
        var result = await GhCommand.RunAsync(cancellationToken, TimeSpan.FromSeconds(90), "api", "/notifications", "--paginate", "--jq", jqFilter);

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

    private AppSettings(string settingsPath, int requiredApprovals, IEnumerable<RepositoryRef> repositories)
    {
        SettingsPath = settingsPath;
        RequiredApprovals = Math.Max(1, requiredApprovals);
        _repositories = repositories
            .DistinctBy(repo => repo.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string SettingsPath { get; }

    public int RequiredApprovals { get; private set; }

    public IReadOnlyList<RepositoryRef> Repositories => _repositories;

    public static AppSettings Load()
    {
        var settingsPath = GetSettingsPath();
        if (!File.Exists(settingsPath))
        {
            return new AppSettings(settingsPath, DefaultRequiredApprovals, []);
        }

        var repositories = new List<RepositoryRef>();
        var requiredApprovals = DefaultRequiredApprovals;
        var inRepositories = false;

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
                inRepositories = true;
                continue;
            }

            if (line.StartsWith("requiredApprovals:", StringComparison.OrdinalIgnoreCase))
            {
                inRepositories = false;
                var value = Unquote(line["requiredApprovals:".Length..].Trim());
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                {
                    requiredApprovals = parsed;
                }

                continue;
            }

            if (inRepositories && line.StartsWith("-", StringComparison.Ordinal))
            {
                var value = Unquote(line[1..].Trim());
                if (RepositoryRef.TryParse(value, out var repository))
                {
                    repositories.Add(repository);
                }
            }
        }

        return new AppSettings(settingsPath, requiredApprovals, repositories);
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

        File.WriteAllText(SettingsPath, builder.ToString(), Encoding.UTF8);
    }

    private static string GetSettingsPath()
    {
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        return Path.Combine(baseDirectory, ".pr.yml");
    }

    private static string StripComment(string line)
    {
        var hashIndex = line.IndexOf('#', StringComparison.Ordinal);
        return hashIndex >= 0 ? line[..hashIndex] : line;
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
    string? ExcludedAuthor);

internal sealed record PullRequestInfo(
    RepositoryRef Repository,
    int Number,
    string Title,
    string Author,
    string Url,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Approvers)
{
    public int ApprovalCount => Approvers.Count;

    public string Key => $"{Repository.FullName}#{Number.ToString(CultureInfo.InvariantCulture)}";
}

internal sealed record Notification(string Id, string SubjectUrl, string SubjectType, string RepositoryFullName);

internal sealed record CleanupResult(int Scanned, int MarkedRead, int Skipped);

internal enum CleanupAction
{
    MarkedRead,
    Skipped,
}

internal sealed record GhResult(int ExitCode, string StandardOutput, string StandardError);

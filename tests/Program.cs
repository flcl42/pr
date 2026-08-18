using System.Diagnostics;
using System.Text.Json;

if (args is ["--single-instance-holder", var scope, var readyPath, var lockDirectory])
{
    using var singleInstance = SingleInstanceLease.Acquire(scope, lockDirectory);
    File.WriteAllText(readyPath, Environment.ProcessId.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (args is ["--dashboard-instance-holder", var settingsPath, var dashboardReadyPath, var dashboardLockDirectory])
{
    using var singleInstance = SingleInstanceLease.AcquireDashboard(settingsPath, dashboardLockDirectory);
    File.WriteAllText(dashboardReadyPath, Environment.ProcessId.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (args is ["--deepcode-update-prompt-holder"])
{
    Console.Write("\u001b[32mDeep Code latest version has been released: 0.1.34 -> 0.2.0\u001b[0m\nEsc to ignore once.");
    return Console.ReadKey(intercept: true).Key == ConsoleKey.Escape ? 42 : 43;
}

var failures = new List<string>();
await RunAsync("new process replaces previous instance", TestSingleInstanceReplacementAsync);
await RunAsync("shared dashboard process replaces previous instance", TestDashboardSingleInstanceReplacementAsync);
await RunAsync("settings default disabled and round trip", TestSettingsRoundTripAsync);
await RunAsync("UI command and companion discovery", TestUiCommandAsync);
await RunAsync("desktop dashboard repository action", TestDashboardRepositoryActionAsync);
await RunAsync("PR search matches title number and author", TestPullRequestSearchAsync);
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
await RunAsync("DeepSeek structured review output", TestDeepSeekStructuredOutputAsync);
await RunAsync("collaborative review ledger and failure isolation", TestCollaborativeReviewPipelineAsync);
await RunAsync("Deep Code safety settings", TestDeepCodeSafetySettingsAsync);
await RunAsync("Deep Code hidden update prompt", TestDeepCodeHiddenUpdatePromptAsync);
await RunAsync("startup review scan lookback", TestStartupScanLookbackAsync);
await RunAsync("manual review enqueue persistence", TestManualReviewEnqueueAsync);
await RunAsync("manual review bypasses policy gates", TestManualReviewPolicyAsync);
await RunAsync("external contributors cannot be reviewed", TestExternalContributorSafetyAsync);
await RunAsync("activity excludes author and bots", TestActivityFilteringAsync);
await RunAsync("latest decisive review controls approvals", TestLatestApprovalStateAsync);
await RunAsync("inline repository context precedence", TestContextPrecedenceAsync);
await RunAsync("embedded repository context", TestEmbeddedContextAsync);
await RunAsync("Windows review agent command wrappers", TestReviewAgentCommandsAsync);
if (string.Equals(Environment.GetEnvironmentVariable("PR_LIVE_AGENT_TESTS"), "1", StringComparison.Ordinal))
{
    await RunAsync("live Deep Code PTY", TestLiveDeepCodePtyAsync);
}

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
    await TestInstanceReplacementAsync(sharedDashboard: false);
}

async Task TestDashboardSingleInstanceReplacementAsync()
{
    await TestInstanceReplacementAsync(sharedDashboard: true);
}

async Task TestInstanceReplacementAsync(bool sharedDashboard)
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-instance-tests-" + Guid.NewGuid().ToString("N"));
    var lockDirectory = Path.Combine(directory, "locks");
    var firstReady = Path.Combine(directory, "first.ready");
    var secondReady = Path.Combine(directory, "second.ready");
    var scope = sharedDashboard
        ? Path.Combine(directory, ".pr.yml")
        : Guid.NewGuid().ToString("N");
    Directory.CreateDirectory(directory);
    Process? first = null;
    Process? second = null;
    try
    {
        first = StartInstanceHolder(scope, firstReady, lockDirectory, sharedDashboard);
        await WaitUntilReadyAsync(first, firstReady);

        second = StartInstanceHolder(scope, secondReady, lockDirectory, sharedDashboard);
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

Process StartInstanceHolder(string scope, string readyPath, string lockDirectory, bool sharedDashboard = false)
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

    startInfo.ArgumentList.Add(sharedDashboard ? "--dashboard-instance-holder" : "--single-instance-holder");
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
        Equal(4, settings.CodexReview.Agents.Count, "default review pipeline size");
        var defaultCodex = settings.CodexReview.EnabledAgents.Single();
        Equal(ReviewAgent.Codex, defaultCodex.Agent, "default enabled review agent");
        Equal(CodexReviewSettings.DefaultCodexModel, defaultCodex.Model, "default Codex model");
        settings.SetCodexReviewEnabled(true);
        settings.SetCodexReviewAutoSubmit(true);
        settings.Save();
        Assert(
            File.ReadAllText(path).Contains(
                $"      model: {CodexReviewSettings.DefaultCodexModel}",
                StringComparison.Ordinal),
            "saved settings did not pin the default Codex model");

        var loaded = AppSettings.Load(path);
        Equal(true, loaded.CodexReview.Enabled, "enabled value did not round trip");
        Equal(true, loaded.CodexReview.AutoSubmit, "auto-submit value did not round trip");
        Equal(CodexReviewSettings.DefaultCodexModel, loaded.CodexReview.EnabledAgents.Single().Model, "saved default Codex model");
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
        var customizedAgent = customized.CodexReview.EnabledAgents.Single();
        Equal(ReviewAgent.Claude, customizedAgent.Agent, "custom review agent");
        Equal(true, customized.CodexReview.AutoSubmit, "custom auto-submit mode");
        Equal("opus", customizedAgent.Model, "custom review model");
        Equal<string?>(null, customizedAgent.Command, "built-in command should migrate to agent selection");
        Equal("review-cache", customized.CodexReview.WorkspaceDirectory, "custom workspace directory");
        Equal(expectedContext, customized.CodexReview.Contexts["owner/repo"], "inline context");
        SetEqual(["custom-review-bot"], customized.CodexReview.IgnoredAuthorPatterns, "custom ignored authors");

        customized.Save();
        var roundTripped = AppSettings.Load(path);
        Equal(expectedContext, roundTripped.CodexReview.Contexts["OWNER/REPO"], "round-tripped context");
        Equal(false, roundTripped.CodexReview.Enabled, "round-tripped enabled value");
        Equal(3, roundTripped.CodexReview.StartupScanDays, "round-tripped startup scan");
        var roundTrippedAgent = roundTripped.CodexReview.EnabledAgents.Single();
        Equal(ReviewAgent.Claude, roundTrippedAgent.Agent, "round-tripped review agent");
        Equal(true, roundTripped.CodexReview.AutoSubmit, "round-tripped auto-submit mode");
        Equal("opus", roundTrippedAgent.Model, "round-tripped review model");
        Equal("review-cache", roundTripped.CodexReview.WorkspaceDirectory, "round-tripped workspace directory");
        Assert(
            customized.CodexReview.SemanticallyEquals(roundTripped.CodexReview),
            "legacy migration changed the effective review settings");

        File.WriteAllText(path, "codexReview:\n  agent: kimi\n");
        var kimi = AppSettings.Load(path);
        var legacyKimi = kimi.CodexReview.EnabledAgents.Single();
        Equal(ReviewAgent.Kimi, legacyKimi.Agent, "Kimi review agent parsing");
        Equal<string?>(null, legacyKimi.Model, "Kimi inherited the Codex default model");

        File.WriteAllText(
            path,
            """
            codexReview:
              agents:
                codex:
                  enabled: true
                  model: gpt-collaborative
                  effort: high
                claude:
                  enabled: false
                  model: sonnet
                  effort: medium
                kimi:
                  enabled: true
                  model: kimi-code/k3
                  effort: max
                deepcode:
                  enabled: true
                  model: deepseek-v4-pro
                  effort: high
            """);
        var collaborative = AppSettings.Load(path).CodexReview;
        Equal(
            "Codex,Kimi,DeepSeek",
            string.Join(',', collaborative.EnabledAgents.Select(agent => agent.Agent)),
            "enabled collaborative agents");
        Equal<string?>(null, collaborative.Agents.Single(agent => agent.Agent == ReviewAgent.Kimi).Effort, "Kimi effort must be ignored");
        Equal("high", collaborative.Agents.Single(agent => agent.Agent == ReviewAgent.DeepSeek).Effort, "DeepSeek effort");
        var collaborativeSettings = AppSettings.Load(path);
        collaborativeSettings.Save();
        var collaborativeRoundTrip = AppSettings.Load(path).CodexReview;
        Equal(
            "Codex,Claude,Kimi,DeepSeek",
            string.Join(',', collaborativeRoundTrip.Agents.Select(agent => agent.Agent)),
            "collaborative pipeline order after save");
        Assert(
            collaborative.SemanticallyEquals(collaborativeRoundTrip),
            "collaborative pipeline changed after save");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

Task TestPullRequestSearchAsync()
{
    var pullRequest = new PullRequestInfo(
        "PR_node",
        new RepositoryRef("owner", "repo"),
        12_758,
        "Fix execution request validation",
        "SomeContributor",
        "https://github.com/owner/repo/pull/12758",
        DateTimeOffset.UtcNow.AddHours(-1),
        DateTimeOffset.UtcNow,
        [],
        new PullRequestPriority(0, PullRequestHeat.Green, 0, 0, false, []));

    Assert(pullRequest.MatchesSearch("execution request"), "title search did not match");
    Assert(pullRequest.MatchesSearch("12758"), "numeric PR search did not match");
    Assert(pullRequest.MatchesSearch("#12758"), "prefixed PR search did not match");
    Assert(pullRequest.MatchesSearch("somecontributor"), "author search was case-sensitive");
    Assert(pullRequest.MatchesSearch("@SomeContributor"), "prefixed author search did not match");
    Assert(pullRequest.MatchesSearch("  12758  "), "search whitespace was not ignored");
    Assert(!pullRequest.MatchesSearch("other-author"), "unrelated search unexpectedly matched");
    return Task.CompletedTask;
}

Task TestUiCommandAsync()
{
    var parsed = CommandLine.Parse(["ui"]);
    Equal(true, parsed.LaunchUi, "UI command was not recognized");
    Equal<string?>(null, parsed.Error, "UI command parse error");

    var combined = CommandLine.Parse(["ui", "--once"]);
    Assert(combined.Error is not null, "UI command accepted an incompatible action");

    var directory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    var nestedDirectory = Path.Combine(directory, "pr-ui");
    Directory.CreateDirectory(nestedDirectory);
    try
    {
        var nestedExecutable = Path.Combine(nestedDirectory, "pr-ui.exe");
        File.WriteAllText(nestedExecutable, "test");
        Equal(Path.GetFullPath(nestedExecutable), UiLauncher.FindExecutable(directory), "nested UI companion discovery");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

Task TestDashboardRepositoryActionAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var settingsPath = Path.Combine(directory, ".pr.yml");
        var dashboard = new DashboardApp(AppSettings.Load(settingsPath));
        var repository = new RepositoryRef("owner", "repo");

        var update = dashboard.AddRepositories([repository]);
        Equal(1, update.Added.Count, "dashboard repository add count");
        Equal("owner/repo", dashboard.GetSnapshot().Repositories.Single().FullName, "dashboard tracked repository");
        Assert(File.ReadAllText(settingsPath).Contains(repository.Url, StringComparison.Ordinal), "dashboard repository was not saved");

        var duplicate = dashboard.AddRepositories([repository]);
        Equal(0, duplicate.Added.Count, "duplicate dashboard repository was added");
        Equal(1, duplicate.AlreadyTracked.Count, "duplicate dashboard repository was not reported");
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

    var globalSettings = CodexReviewSettings.Default;
    var defaultCodexAgent = globalSettings.Agents.Single(agent => agent.Agent == ReviewAgent.Codex);
    var defaultCodex = LocalReviewAgent.CreateInvocation(
        defaultCodexAgent,
        CodexReviewSettings.Default,
        paths,
        settingsDirectory);
    AssertArgumentPair(
        defaultCodex.Arguments,
        "--model",
        CodexReviewSettings.DefaultCodexModel,
        "default Codex model");

    var codexAgent = defaultCodexAgent with { Model = "gpt-test" };
    var codex = LocalReviewAgent.CreateInvocation(codexAgent, globalSettings, paths, settingsDirectory);
    Equal("codex", codex.Command, "Codex executable");
    AssertArgumentPair(codex.Arguments, "--model", "gpt-test", "Codex model");
    AssertArgumentPair(codex.Arguments, "--sandbox", "read-only", "Codex sandbox");
    Assert(codex.Arguments.Contains("--output-schema"), "Codex output schema was omitted");

    var defaultClaudeAgent = ReviewAgentSettings.DefaultFor(ReviewAgent.Claude) with { Model = null };
    var defaultClaude = LocalReviewAgent.CreateInvocation(defaultClaudeAgent, globalSettings, paths, settingsDirectory);
    Assert(!defaultClaude.Arguments.Contains("--model"), "Claude inherited the Codex default model");
    var claudeAgent = defaultClaudeAgent with { Model = "opus", Command = null };
    var claude = LocalReviewAgent.CreateInvocation(claudeAgent, globalSettings, paths, settingsDirectory);
    Equal("claude", claude.Command, "Claude executable");
    AssertArgumentPair(claude.Arguments, "--model", "opus", "Claude model");
    AssertArgumentPair(claude.Arguments, "--permission-mode", "plan", "Claude permission mode");
    AssertArgumentPair(claude.Arguments, "--output-format", "json", "Claude output format");
    Assert(claude.Arguments.Contains("--json-schema"), "Claude JSON schema was omitted");
    Assert(claude.Arguments.Contains("--no-session-persistence"), "Claude session persistence was not disabled");
    Assert(claude.Arguments.Contains("--safe-mode"), "Claude user configuration was not disabled");
    Assert(!claude.Arguments.Contains("exec"), "Claude received Codex arguments");

    var kimiAgent = ReviewAgentSettings.DefaultFor(ReviewAgent.Kimi) with { Model = null, Command = null };
    var kimi = LocalReviewAgent.CreateInvocation(kimiAgent, globalSettings, paths, settingsDirectory);
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

    var kimiBuilder = kimiAgent.ToBuilder();
    kimiBuilder.Command = "kimi";
    Equal<string?>(null, kimiBuilder.Build().Command, "built-in Kimi command was persisted as an override");

    var customCommand = OperatingSystem.IsWindows() ? @"C:\Tools\claude-custom.exe" : "/opt/claude-custom";
    var custom = claudeAgent with { Command = customCommand };
    Equal(customCommand, custom.ResolvedCommand, "custom agent command");

    var deepSeekAgent = ReviewAgentSettings.DefaultFor(ReviewAgent.DeepSeek) with { Enabled = true };
    var deepSeek = LocalReviewAgent.CreateInvocation(deepSeekAgent, globalSettings, paths, settingsDirectory);
    Equal("deepcode", deepSeek.Command, "Deep Code executable");
    Assert(deepSeek.RequiresPseudoTerminal, "Deep Code did not request a PTY");
    Assert(!deepSeek.PromptInStandardInput, "Deep Code prompt was sent through stdin");
    Equal("deepseek-v4-pro", deepSeek.EnvironmentVariables?["DEEPCODE_MODEL"], "Deep Code model environment");
    Equal("max", deepSeek.EnvironmentVariables?["DEEPCODE_REASONING_EFFORT"], "Deep Code effort environment");
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
        Assert(
            !document.RootElement.TryGetProperty("$schema", out _),
            "shared schema declares a draft rejected by Claude");
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
    Assert(
        prompt.Contains("shared collaborative ledger", StringComparison.Ordinal)
            && prompt.Contains("Do not repeat, rephrase, or relocate", StringComparison.Ordinal),
        "review prompt did not require collaborative deduplication");
    Assert(
        prompt.Contains("severity`, `title`, `body`, `path`, `line`, and `side`", StringComparison.Ordinal),
        "review prompt did not define the common finding format");
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

Task TestDeepSeekStructuredOutputAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var resultPath = Path.Combine(directory, "deepseek-result.json");
        var review = LocalReviewAgent.ParseDeepSeekResult(
            """
            ```json
            {"summary":"No additional issues","findings":[]}
            ```
            """,
            resultPath);
        Equal("No additional issues", review.Summary, "DeepSeek review summary");
        Equal(0, review.Findings.Count, "DeepSeek review findings");
        Assert(File.Exists(resultPath), "DeepSeek structured result was not persisted");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

async Task TestCollaborativeReviewPipelineAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var ledgerPath = Path.Combine(directory, "pr-42.review.jsonl");
        var artifactPath = Path.Combine(directory, "pr-42.collaboration.jsonl");
        var ledger = CollaborativeReviewLedger.CreateForTest(ledgerPath, artifactPath);
        var pipeline = new[]
        {
            ReviewAgentSettings.DefaultFor(ReviewAgent.Codex) with { Enabled = true },
            ReviewAgentSettings.DefaultFor(ReviewAgent.Claude) with { Enabled = true },
            ReviewAgentSettings.DefaultFor(ReviewAgent.Kimi) with { Enabled = true },
        };
        var started = new List<string>();
        var stageUpdates = new List<ReviewAgentStageUpdate>();
        var outcome = await LocalReviewAgent.RunPipelineAsync(
            pipeline,
            ledger,
            maxFindings: 5,
            (agent, _) => agent.Agent switch
            {
                ReviewAgent.Claude => throw new InvalidOperationException("provider unavailable"),
                ReviewAgent.Kimi => Task.FromResult(new CodexReviewResult
                {
                    Summary = "Found one additional issue",
                    Findings =
                    [
                        PipelineFinding("high", "Existing defect", "Duplicate evidence", "src/a.cs", 12),
                        PipelineFinding("low", "Second defect", "Independent evidence", "src/b.cs", 30),
                    ],
                }),
                _ => Task.FromResult(new CodexReviewResult
                {
                    Summary = "Found the first issue",
                    Findings = [PipelineFinding("high", "Existing defect", "Initial evidence", "src/a.cs", 12)],
                }),
            },
            (agent, index, count) => started.Add($"{index}/{count}:{agent.AgentName}"),
            CancellationToken.None,
            stageUpdates.Add);

        Equal(2, outcome.SuccessfulAgentCount, "successful collaborative agents");
        Equal("codex+kimi", outcome.SuccessfulAgentNames, "successful collaborative agent names");
        Equal("CK", CodexReviewedPullRequest.AgentLettersFor(outcome.SuccessfulAgentNames), "successful agent title letters");
        Equal("CcKD", CodexReviewedPullRequest.AgentLettersFor("codex+claude+kimi+deepcode"), "all agent title letters");
        Equal(1, outcome.FailedAgentCount, "failed collaborative agents");
        Equal(2, outcome.Result.Findings.Count, "deduplicated collaborative findings");
        Equal("1/3:codex,2/3:claude,3/3:kimi", string.Join(',', started), "pipeline order");
        Equal(
            "codex:Running,codex:Completed,claude:Running,claude:Failed,kimi:Running,kimi:Completed",
            string.Join(',', stageUpdates.Select(update => $"{update.Agent.AgentName}:{update.Status}")),
            "pipeline stage updates");
        Equal(1, stageUpdates[1].AcceptedFindings, "Codex accepted finding count");
        Equal(1, stageUpdates[5].ReturnedFindings - stageUpdates[5].AcceptedFindings, "Kimi duplicate accounting");

        var progress = new ReviewPipelineProgress(
            PullRequestKey: "owner/repo#42",
            Repository: "owner/repo",
            PullRequestNumber: 42,
            PullRequestTitle: "Pipeline progress",
            PullRequestUrl: "https://github.com/owner/repo/pull/42",
            IsManual: true,
            Status: ReviewPipelineStatus.Running,
            StartedAt: stageUpdates[0].Timestamp,
            CompletedAt: null,
            FindingCount: 0,
            Detail: null,
            Stages: pipeline.Select((agent, index) => ReviewStageProgress.Pending(agent, index + 1)).ToArray());
        foreach (var update in stageUpdates)
        {
            progress = progress.Apply(update);
        }

        progress = progress.Finish(ReviewPipelineStatus.CompletedWithErrors, 2, "draft ready", DateTimeOffset.UtcNow);
        Equal(ReviewAgentStageStatus.Completed, progress.Stages[0].Status, "completed stage progress");
        Equal(ReviewAgentStageStatus.Failed, progress.Stages[1].Status, "failed stage progress");
        Equal(ReviewPipelineStatus.CompletedWithErrors, progress.Status, "pipeline completion status");
        Equal(2, progress.FindingCount, "pipeline finding count");
        var ledgerText = File.ReadAllText(ledgerPath);
        Assert(ledgerText.Contains("\"agent\":\"claude\"", StringComparison.Ordinal), "failed agent was not recorded");
        Assert(ledgerText.Contains("\"status\":\"failed\"", StringComparison.Ordinal), "agent failure status was not recorded");
        foreach (var line in File.ReadLines(ledgerPath))
        {
            using var _ = JsonDocument.Parse(line);
        }
        Equal(ledgerText, File.ReadAllText(artifactPath), "ledger artifact copy");

        var failedLedger = CollaborativeReviewLedger.CreateForTest(
            Path.Combine(directory, "pr-43.review.jsonl"),
            Path.Combine(directory, "pr-43.collaboration.jsonl"));
        try
        {
            await LocalReviewAgent.RunPipelineAsync(
                pipeline,
                failedLedger,
                maxFindings: 5,
                (agent, _) => throw new InvalidOperationException($"{agent.AgentName} unavailable"),
                agentStarted: null,
                CancellationToken.None);
            throw new InvalidOperationException("an all-agent failure was accepted as a completed review");
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("All enabled review agents failed:", StringComparison.Ordinal))
        {
            Assert(ex.Message.Contains("codex", StringComparison.Ordinal), "all-agent error omitted Codex");
            Assert(ex.Message.Contains("kimi", StringComparison.Ordinal), "all-agent error omitted Kimi");
        }

        var canceledUpdates = new List<ReviewAgentStageUpdate>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await LocalReviewAgent.RunPipelineAsync(
                [pipeline[0]],
                CollaborativeReviewLedger.CreateForTest(
                    Path.Combine(directory, "pr-44.review.jsonl"),
                    Path.Combine(directory, "pr-44.collaboration.jsonl")),
                maxFindings: 5,
                (_, token) => Task.FromCanceled<CodexReviewResult>(token),
                agentStarted: null,
                cancellation.Token,
                canceledUpdates.Add);
            throw new InvalidOperationException("a canceled agent pipeline completed");
        }
        catch (OperationCanceledException)
        {
            Equal(ReviewAgentStageStatus.Canceled, canceledUpdates[^1].Status, "canceled agent stage state");
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

Task TestDeepCodeSafetySettingsAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-tests-" + Guid.NewGuid().ToString("N"));
    var settingsDirectory = Path.Combine(directory, ".deepcode");
    var settingsPath = Path.Combine(settingsDirectory, "settings.json");
    Directory.CreateDirectory(settingsDirectory);
    const string original = "{\"model\":\"project-model\"}";
    File.WriteAllText(settingsPath, original);
    try
    {
        using (DeepCodeSafetySettings.Create(directory))
        {
            var safe = File.ReadAllText(settingsPath);
            Assert(safe.Contains("\"read-in-cwd\"", StringComparison.Ordinal), "Deep Code repository reads were not allowed");
            Assert(safe.Contains("\"write-in-cwd\"", StringComparison.Ordinal), "Deep Code writes were not denied");
            Assert(safe.Contains("\"network\"", StringComparison.Ordinal), "Deep Code network access was not denied");
        }

        Equal(original, File.ReadAllText(settingsPath), "Deep Code project settings were not restored");
        var code = DeepCodeSessions.ProjectCode(directory);
        Assert(code.Length <= 64, "Deep Code project code exceeded its storage limit");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

async Task TestDeepCodeHiddenUpdatePromptAsync()
{
    Assert(
        DeepCodeProcessRunner.IsPendingUpdatePrompt(
            "\u001b[32mDeep Code latest version has been released: 0.1.34 -> 0.2.0\u001b[0m\nEsc to ignore once."),
        "Deep Code update prompt was not recognized through ANSI output");
    Assert(
        !DeepCodeProcessRunner.IsPendingUpdatePrompt("Deep Code is reviewing the pull request"),
        "ordinary Deep Code output was mistaken for an update prompt");
    Assert(
        DeepCodeProcessRunner.SessionStartupTimeout <= TimeSpan.FromMinutes(2),
        "Deep Code session startup watchdog is too long");

    var directory = Path.Combine(Path.GetTempPath(), "pr-deepcode-update-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var result = await DeepCodeProcessRunner.RunAsync(
            Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path is unavailable"),
            ["--deepcode-update-prompt-holder"],
            directory,
            timeout: TimeSpan.FromSeconds(15),
            eligibilityCheckInterval: TimeSpan.FromSeconds(1),
            _ => Task.FromResult(true),
            CancellationToken.None,
            environmentVariables: null);
        Assert(
            result.StandardError.Contains("exit 42", StringComparison.Ordinal),
            "Deep Code updater prompt did not receive Escape through the PTY");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static CodexReviewFinding PipelineFinding(string severity, string title, string body, string path, int line)
{
    return new CodexReviewFinding
    {
        Severity = severity,
        Title = title,
        Body = body,
        Path = path,
        Line = line,
        Side = "RIGHT",
    };
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
    await CheckVersionAsync("deepcode", expectedText: null);

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

async Task TestLiveDeepCodePtyAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), "pr-deepcode-live-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        using var safety = DeepCodeSafetySettings.Create(directory);
        var result = await DeepCodeProcessRunner.RunAsync(
            "deepcode",
            ["-p", "Return only this exact JSON object with no Markdown: {\"summary\":\"PTY ready\",\"findings\":[]}"],
            directory,
            timeout: TimeSpan.FromMinutes(5),
            eligibilityCheckInterval: TimeSpan.FromSeconds(2),
            _ => Task.FromResult(true),
            CancellationToken.None,
            new Dictionary<string, string>
            {
                ["DEEPCODE_MODEL"] = "deepseek-v4-pro",
                ["DEEPCODE_THINKING_ENABLED"] = "true",
                ["DEEPCODE_REASONING_EFFORT"] = "high",
            });
        Equal(0, result.ExitCode, $"Deep Code PTY exit: {result.StandardError}");
        var parsed = LocalReviewAgent.ParseDeepSeekResult(
            result.StandardOutput,
            Path.Combine(directory, "result.json"));
        Equal("PTY ready", parsed.Summary, "Deep Code PTY structured output");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
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

using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Porta.Pty;

internal sealed class DeepCodeSafetySettings : IDisposable
{
    private static readonly byte[] SafeSettings = Encoding.UTF8.GetBytes("""
        {
          "permissions": {
            "allow": ["read-in-cwd", "query-git-log"],
            "deny": [
              "read-out-cwd",
              "write-in-cwd",
              "write-out-cwd",
              "delete-in-cwd",
              "delete-out-cwd",
              "mutate-git-log",
              "network",
              "mcp"
            ],
            "ask": [],
            "defaultMode": "askAll"
          }
        }
        """);

    private readonly string _directory;
    private readonly string _path;
    private readonly byte[]? _original;
    private bool _disposed;

    private DeepCodeSafetySettings(string worktree)
    {
        _directory = Path.Combine(worktree, ".deepcode");
        _path = Path.Combine(_directory, "settings.json");
        _original = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(_path, SafeSettings);
    }

    public static DeepCodeSafetySettings Create(string worktree)
    {
        return new DeepCodeSafetySettings(worktree);
    }

    internal static string SafeSettingsJson => Encoding.UTF8.GetString(SafeSettings);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_original is not null)
        {
            File.WriteAllBytes(_path, _original);
            return;
        }

        try
        {
            File.Delete(_path);
            if (Directory.Exists(_directory) && !Directory.EnumerateFileSystemEntries(_directory).Any())
            {
                Directory.Delete(_directory);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static partial class DeepCodeProcessRunner
{
    private static readonly TimeSpan SessionPollInterval = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan SessionStartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly byte[] EscapeKey = [0x1b];

    public static async Task<ProcessResult> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        string cwd,
        TimeSpan timeout,
        TimeSpan eligibilityCheckInterval,
        Func<CancellationToken, Task<bool>> stillEligible,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        var resolvedCommand = ProcessRunner.ResolveCommand(command);
        var (application, commandLine) = BuildPtyCommand(resolvedCommand, arguments);
        var existingSessionIds = DeepCodeSessions.SessionIdsForWorktree(cwd);
        var environment = CurrentEnvironment();
        if (environmentVariables is not null)
        {
            foreach (var variable in environmentVariables)
            {
                environment[variable.Key] = variable.Value;
            }
        }

        var options = new PtyOptions
        {
            Name = "pr-deepcode-review",
            Cols = 140,
            Rows = 40,
            Cwd = cwd,
            App = application,
            CommandLine = commandLine.ToArray(),
            Environment = environment,
        };

        var terminal = await PtyProvider.SpawnAsync(options, cancellationToken);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.ProcessExited += (_, args) => exited.TrySetResult(args.ExitCode);
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var transcript = new BoundedTextBuffer(128_000);
        var reader = ReadOutputAsync(terminal.ReaderStream, transcript, readerCancellation.Token);
        var startedAt = DateTimeOffset.UtcNow;
        var deadline = startedAt + timeout;
        var sessionStartupTimeout = timeout < SessionStartupTimeout ? timeout : SessionStartupTimeout;
        var sessionStartupDeadline = startedAt + sessionStartupTimeout;
        var nextEligibilityCheck = DateTimeOffset.UtcNow;
        var exitedAt = (DateTimeOffset?)null;
        string? sessionId = null;
        string? completedMessage = null;
        string? failure = null;
        var updatePromptDismissed = false;
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= nextEligibilityCheck)
                {
                    if (!await stillEligible(cancellationToken))
                    {
                        throw new CodexReviewEligibilityException("PR is no longer eligible");
                    }

                    nextEligibilityCheck = DateTimeOffset.UtcNow + eligibilityCheckInterval;
                }

                if (!updatePromptDismissed
                    && sessionId is null
                    && IsPendingUpdatePrompt(transcript.ToString()))
                {
                    await terminal.WriterStream.WriteAsync(EscapeKey, cancellationToken);
                    await terminal.WriterStream.FlushAsync(cancellationToken);
                    updatePromptDismissed = true;
                }

                sessionId ??= DeepCodeSessions.FindNewSessionId(cwd, existingSessionIds);
                if (sessionId is not null)
                {
                    var state = DeepCodeSessions.ReadState(cwd, sessionId);
                    if (state.Status == "completed")
                    {
                        completedMessage = state.FinalAssistantMessage;
                        if (!string.IsNullOrWhiteSpace(completedMessage))
                        {
                            break;
                        }
                    }
                    else if (state.Status is "failed" or "interrupted" or "permission_denied"
                        or "ask_permission" or "waiting_for_user")
                    {
                        failure = $"Deep Code session {state.Status}: {state.Detail}".TrimEnd();
                        break;
                    }
                }
                else if (DateTimeOffset.UtcNow >= sessionStartupDeadline)
                {
                    failure = $"Deep Code did not start a review session within {sessionStartupTimeout.TotalMinutes:0.#} minutes";
                    break;
                }

                if (!exited.Task.IsCompleted && terminal.WaitForExit(0))
                {
                    exited.TrySetResult(terminal.ExitCode);
                }

                if (exited.Task.IsCompleted)
                {
                    exitedAt ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - exitedAt > TimeSpan.FromSeconds(3))
                    {
                        failure = $"Deep Code exited before producing a completed review (exit {await exited.Task})";
                        break;
                    }
                }

                await Task.Delay(SessionPollInterval, cancellationToken);
            }

            if (completedMessage is null && failure is null)
            {
                throw new TimeoutException($"Deep Code timed out after {timeout.TotalMinutes:0.#} minutes");
            }
        }
        finally
        {
            try
            {
                terminal.Kill();
                terminal.WaitForExit(10_000);
            }
            catch
            {
            }

            readerCancellation.Cancel();
            try
            {
                await reader.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            terminal.Dispose();
            await Task.Delay(200, CancellationToken.None);
        }

        var terminalOutput = StripAnsi(transcript.ToString());
        return completedMessage is not null
            ? new ProcessResult(0, completedMessage, terminalOutput)
            : new ProcessResult(1, string.Empty, $"{failure}{Environment.NewLine}{terminalOutput}".Trim());
    }

    internal static (string Application, IReadOnlyList<string> Arguments) BuildPtyCommand(
        string resolvedCommand,
        IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (resolvedCommand, arguments);
        }

        if (resolvedCommand.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || resolvedCommand.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return (
                Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                new[] { "/d", "/s", "/c", resolvedCommand }.Concat(arguments).ToArray());
        }

        if (resolvedCommand.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return (
                ProcessRunner.ResolveCommand("pwsh"),
                new[] { "-NoProfile", "-File", resolvedCommand }.Concat(arguments).ToArray());
        }

        return (resolvedCommand, arguments);
    }

    internal static bool IsPendingUpdatePrompt(string output)
    {
        var text = StripAnsi(output);
        return text.Contains("Deep Code latest version has been released:", StringComparison.OrdinalIgnoreCase)
            && text.Contains("Esc to ignore once", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> CurrentEnvironment()
    {
        var environment = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        return environment;
    }

    private static async Task ReadOutputAsync(
        Stream stream,
        BoundedTextBuffer output,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8_192];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return;
                }

                output.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }

    private static string StripAnsi(string value)
    {
        return AnsiEscapeRegex().Replace(value, string.Empty).Replace('\r', '\n');
    }

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscapeRegex();
}

internal static class DeepCodeSessions
{
    public static HashSet<string> SessionIdsForWorktree(string worktree)
    {
        return ReadEntries(worktree)
            .Select(entry => ReadString(entry, "id"))
            .Where(IsSessionId)
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static string? FindNewSessionId(string worktree, IReadOnlySet<string> existingSessionIds)
    {
        return ReadEntries(worktree)
            .Select(entry => ReadString(entry, "id"))
            .LastOrDefault(id => IsSessionId(id) && !existingSessionIds.Contains(id!));
    }

    public static DeepCodeSessionState ReadState(string worktree, string sessionId)
    {
        JsonElement? entry = null;
        foreach (var candidate in ReadEntries(worktree))
        {
            if (string.Equals(ReadString(candidate, "id"), sessionId, StringComparison.OrdinalIgnoreCase))
            {
                entry = candidate;
            }
        }

        var status = entry is null ? string.Empty : ReadString(entry.Value, "status") ?? string.Empty;
        var detail = entry is null
            ? string.Empty
            : ReadString(entry.Value, "failReason")
                ?? ReadString(entry.Value, "assistantReply")
                ?? string.Empty;
        var finalMessage = FinalAssistantMessage(worktree, sessionId);
        if (string.IsNullOrWhiteSpace(finalMessage) && entry is not null)
        {
            finalMessage = ReadString(entry.Value, "assistantReply") ?? string.Empty;
        }

        return new DeepCodeSessionState(status.Trim().ToLowerInvariant(), finalMessage, detail);
    }

    internal static string ProjectCode(string worktree)
    {
        var legacy = worktree.Replace("\\", "-", StringComparison.Ordinal)
            .Replace("/", "-", StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal);
        if (legacy.Length <= 64)
        {
            return legacy;
        }

        var normalized = Path.GetFullPath(worktree);
        var hashInput = OperatingSystem.IsWindows() ? normalized.ToLowerInvariant() : normalized;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))
            .ToLowerInvariant()[..16];
        var prefix = SanitizeProjectCodePart(Path.GetFileName(normalized));
        prefix = prefix[..Math.Min(prefix.Length, 47)].TrimEnd('-', '.');
        return $"{(prefix.Length == 0 ? "project" : prefix)}-{digest}";
    }

    private static string IndexPath(string worktree)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".deepcode",
            "projects",
            ProjectCode(worktree),
            "sessions-index.json");
    }

    private static string SessionPath(string worktree, string sessionId)
    {
        return Path.Combine(Path.GetDirectoryName(IndexPath(worktree))!, sessionId + ".jsonl");
    }

    private static IReadOnlyList<JsonElement> ReadEntries(string worktree)
    {
        try
        {
            var path = IndexPath(worktree);
            if (!File.Exists(path))
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return entries.EnumerateArray().Select(entry => entry.Clone()).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string FinalAssistantMessage(string worktree, string sessionId)
    {
        try
        {
            var path = SessionPath(worktree, sessionId);
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            foreach (var line in File.ReadLines(path).Reverse())
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!string.Equals(ReadString(root, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var content = ReadString(root, "content");
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool IsSessionId(string? value)
    {
        return value is not null && Guid.TryParse(value, out _);
    }

    private static string SanitizeProjectCodePart(string value)
    {
        var sanitized = Regex.Replace(value, "[^A-Za-z0-9._-]", "-");
        sanitized = Regex.Replace(sanitized, "-+", "-");
        return sanitized.Trim('-', '.');
    }
}

internal sealed record DeepCodeSessionState(string Status, string FinalAssistantMessage, string Detail);

internal sealed class BoundedTextBuffer
{
    private readonly int _capacity;
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();

    public BoundedTextBuffer(int capacity)
    {
        _capacity = capacity;
    }

    public void Append(string value)
    {
        lock (_gate)
        {
            _buffer.Append(value);
            if (_buffer.Length > _capacity)
            {
                _buffer.Remove(0, _buffer.Length - _capacity);
            }
        }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            return _buffer.ToString();
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class SingleInstanceLease : IDisposable
{
    private static readonly TimeSpan DefaultTakeoverTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly string _mutexName;
    private readonly string _ownerPath;
    private readonly InstanceOwner _owner;
    private readonly TimeSpan _takeoverTimeout;
    private readonly bool _requireSameExecutable;
    private readonly ManualResetEventSlim _release = new(initialState: false);
    private readonly TaskCompletionSource _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _ownerThread;
    private int _disposed;

    private SingleInstanceLease(
        string mutexName,
        string ownerPath,
        InstanceOwner owner,
        TimeSpan takeoverTimeout,
        bool requireSameExecutable)
    {
        _mutexName = mutexName;
        _ownerPath = ownerPath;
        _owner = owner;
        _takeoverTimeout = takeoverTimeout;
        _requireSameExecutable = requireSameExecutable;
        _ownerThread = new Thread(OwnMutex)
        {
            IsBackground = true,
            Name = "pr-single-instance",
        };
    }

    public static SingleInstanceLease Acquire(
        string? scope = null,
        string? lockDirectory = null,
        TimeSpan? takeoverTimeout = null,
        bool shareAcrossExecutables = false)
    {
        var executablePath = CurrentExecutablePath();
        var owner = new InstanceOwner(
            Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            executablePath);
        var paths = GetPaths(executablePath, scope, lockDirectory, shareAcrossExecutables);
        Directory.CreateDirectory(paths.Directory);
        var lease = new SingleInstanceLease(
            paths.MutexName,
            paths.OwnerPath,
            owner,
            takeoverTimeout ?? DefaultTakeoverTimeout,
            requireSameExecutable: !shareAcrossExecutables);
        lease._ownerThread.Start();
        try
        {
            lease._acquired.Task.GetAwaiter().GetResult();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public static SingleInstanceLease AcquireDashboard(
        string settingsPath,
        string? lockDirectory = null,
        TimeSpan? takeoverTimeout = null)
    {
        var comparison = OperatingSystem.IsWindows()
            ? Path.GetFullPath(settingsPath).ToUpperInvariant()
            : Path.GetFullPath(settingsPath);
        return Acquire(
            scope: "dashboard\n" + comparison,
            lockDirectory,
            takeoverTimeout,
            shareAcrossExecutables: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _release.Set();
        _ownerThread.Join();
        _release.Dispose();
    }

    private void OwnMutex()
    {
        var ownsMutex = false;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, _mutexName);
            var timer = Stopwatch.StartNew();
            while (!_release.IsSet)
            {
                try
                {
                    ownsMutex = mutex.WaitOne(RetryDelay);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (ownsMutex)
                {
                    WriteOwner(_ownerPath, _owner);
                    _acquired.TrySetResult();
                    _release.Wait();
                    return;
                }

                TryTerminatePreviousOwner(
                    _ownerPath,
                    _owner.ExecutablePath,
                    _requireSameExecutable);
                if (timer.Elapsed >= _takeoverTimeout)
                {
                    throw new SingleInstanceException(
                        $"Could not replace the previous pr instance within {_takeoverTimeout.TotalSeconds:0.#} seconds.");
                }
            }

            _acquired.TrySetCanceled();
        }
        catch (Exception ex)
        {
            _acquired.TrySetException(ex);
        }
        finally
        {
            if (ownsMutex)
            {
                TryDeleteOwner(_ownerPath, _owner);
                try
                {
                    mutex?.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }

            mutex?.Dispose();
        }
    }

    private static InstancePaths GetPaths(
        string executablePath,
        string? scope,
        string? lockDirectory,
        bool shareAcrossExecutables)
    {
        var directory = string.IsNullOrWhiteSpace(lockDirectory)
            ? Path.Combine(Path.GetTempPath(), "pr-single-instance")
            : Path.GetFullPath(lockDirectory);
        var comparisonPath = OperatingSystem.IsWindows()
            ? executablePath.ToUpperInvariant()
            : executablePath;
        var identity = shareAcrossExecutables
            ? string.Join("\n", Environment.UserName, scope?.Trim())
            : string.Join(
                "\n",
                Environment.UserName,
                comparisonPath,
                Path.GetFullPath(AppContext.BaseDirectory),
                Assembly.GetEntryAssembly()?.GetName().Name,
                scope?.Trim());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return new InstancePaths(
            directory,
            "pr-" + hash,
            Path.Combine(directory, hash + ".owner.json"));
    }

    private static void TryTerminatePreviousOwner(
        string ownerPath,
        string executablePath,
        bool requireSameExecutable)
    {
        var owner = ReadOwner(ownerPath);
        if (owner is null
            || owner.ProcessId == Environment.ProcessId
            || (requireSameExecutable && !PathsEqual(owner.ExecutablePath, executablePath)))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            if (process.HasExited
                || process.StartTime.ToUniversalTime().Ticks != owner.StartTimeUtcTicks)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(milliseconds: 5_000);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static void WriteOwner(string ownerPath, InstanceOwner owner)
    {
        var temporaryPath = ownerPath + "." + owner.ProcessId.ToString() + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(owner), Encoding.UTF8);
            File.Move(temporaryPath, ownerPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static InstanceOwner? ReadOwner(string ownerPath)
    {
        try
        {
            return JsonSerializer.Deserialize<InstanceOwner>(File.ReadAllText(ownerPath, Encoding.UTF8));
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryDeleteOwner(string ownerPath, InstanceOwner expectedOwner)
    {
        var owner = ReadOwner(ownerPath);
        if (owner != expectedOwner)
        {
            return;
        }

        try
        {
            File.Delete(ownerPath);
        }
        catch (IOException)
        {
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string CurrentExecutablePath()
    {
        var path = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        return string.IsNullOrWhiteSpace(path)
            ? throw new SingleInstanceException("Could not determine the pr executable path.")
            : Path.GetFullPath(path);
    }

    private sealed record InstanceOwner(int ProcessId, long StartTimeUtcTicks, string ExecutablePath);

    private sealed record InstancePaths(string Directory, string MutexName, string OwnerPath);
}

internal sealed class SingleInstanceException : Exception
{
    public SingleInstanceException(string message)
        : base(message)
    {
    }
}

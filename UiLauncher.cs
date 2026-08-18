using System.Diagnostics;

internal static class UiLauncher
{
    public const string SettingsPathEnvironmentVariable = "PR_SETTINGS_PATH";
    public const string ExecutablePathEnvironmentVariable = "PR_UI_PATH";

    public static bool TryLaunch(string settingsPath, out string error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "The optional MAUI interface is currently available for Windows only; use the console dashboard on this platform.";
            return false;
        }

        var executablePath = FindExecutable();
        if (executablePath is null)
        {
            error = "The MAUI interface is not installed. Build Pr.Maui.slnx and place its publish output in the 'pr-ui' directory beside pr.exe.";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                UseShellExecute = false,
            };
            startInfo.Environment[SettingsPathEnvironmentVariable] = Path.GetFullPath(settingsPath);
            Process.Start(startInfo)?.Dispose();
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = $"Could not start the MAUI interface: {ex.Message}";
            return false;
        }
    }

    internal static string? FindExecutable(string? baseDirectory = null)
    {
        var configuredPath = Environment.GetEnvironmentVariable(ExecutablePathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (File.Exists(expanded))
            {
                return Path.GetFullPath(expanded);
            }
        }

        var root = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var candidates = new[]
        {
            Path.Combine(root, "pr-ui.exe"),
            Path.Combine(root, "pr-ui", "pr-ui.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

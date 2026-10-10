using System.Diagnostics;

namespace Sweep;

public sealed record RunningProcessInfo(int ProcessId, string Name, string Path, string WindowTitle)
{
    public string Display => WindowTitle.Length > 0 ? $"{Name} - {WindowTitle}" : Name;
}

/// <summary>
/// Finds and stops the processes belonging to a program. An uninstaller that cannot
/// delete its own files because something still has them open is one of the most common
/// reasons a removal leaves junk behind, so this runs before and after an uninstall.
/// </summary>
public static class ProcessHunter
{
    /// <summary>
    /// Matches by install folder first, then by executable identity. Matching on a bare
    /// file name is deliberately avoided: "Update.exe" would match dozens of unrelated
    /// programs.
    /// </summary>
    public static List<RunningProcessInfo> FindFor(InstalledApp app)
    {
        var results = new List<RunningProcessInfo>();
        string? installLocation = Normalise(app.InstallLocation);
        string? exe = Normalise(app.ExecutablePath);

        foreach (var process in SafeProcesses())
        {
            string? path = Normalise(TryGetPath(process));
            if (path is null)
            {
                continue;
            }

            bool matches =
                (installLocation is not null && path.StartsWith(installLocation, StringComparison.OrdinalIgnoreCase)) ||
                (exe is not null && string.Equals(path, exe, StringComparison.OrdinalIgnoreCase));

            if (!matches)
            {
                continue;
            }

            results.Add(new RunningProcessInfo(
                process.Id,
                SafeName(process),
                path,
                SafeWindowTitle(process)));
        }

        return results;
    }

    public static int Kill(IEnumerable<RunningProcessInfo> processes)
    {
        int killed = 0;

        foreach (var info in processes)
        {
            try
            {
                using var process = Process.GetProcessById(info.ProcessId);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                killed++;
            }
            catch (ArgumentException)
            {
                // Already exited between listing and killing.
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Could not stop process {info.Name} (PID {info.ProcessId}): {ex.Message}");
            }
        }

        return killed;
    }

    private static IEnumerable<Process> SafeProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return [];
        }

        return processes;
    }

    private static string? Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return expanded.Length > 3 ? expanded.TrimEnd('\\') : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return "?";
        }
    }

    private static string SafeWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? TryGetPath(Process process)
    {
        try
        {
            // Throws for processes owned by other users unless elevated.
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    public static string DescribePath(Process process) => TryGetPath(process) ?? string.Empty;
}
using System.Diagnostics;

namespace Sweep;

/// <summary>How an item must be removed. Drives a single dispatch point in the remover.</summary>
public abstract record RemovalTarget
{
    public abstract string Describe();
}

public sealed record FileTarget(string Path) : RemovalTarget
{
    public override string Describe() => "file";
}

public sealed record RegistryKeyTarget(RegLocation Location) : RemovalTarget
{
    public override string Describe() => "registry key";
}

public sealed record RegistryValueTarget(RegLocation Location, string ValueName) : RemovalTarget
{
    public override string Describe() => "registry value";
}

public sealed record ServiceTarget(string ServiceName) : RemovalTarget
{
    public override string Describe() => "service";
}

public sealed record ScheduledTaskTarget(string TaskName) : RemovalTarget
{
    public override string Describe() => "scheduled task";
}

/// <summary>
/// Confidence in a match, borrowed from the "do not delete this blindly" convention that
/// every serious uninstaller uses. Risky items start unchecked and require a deliberate
/// action.
/// </summary>
public enum Confidence
{
    /// <summary>Belongs to the product: its install folder, AppData tree, own registry keys.</summary>
    Safe,

    /// <summary>Almost certainly the product's, but could belong to something else.</summary>
    Moderate,

    /// <summary>Shared, system-owned, or could break other software. Starts unchecked.</summary>
    Risky,
}

public static class Removal
{
    /// <summary>Executes a removal, returning success and whether it was deferred to reboot.</summary>
    public static DeletionResult Execute(ResidueItem item, Quarantine? quarantine)
    {
        return item.Target switch
        {
            // Files and folders share FileTarget, so the item's kind decides which primitive
            // runs. Handing a directory to File.Delete silently succeeds without doing
            // anything, which is why leftovers used to "vanish" from the list and stay on disk.
            FileTarget file => item.Kind == ResidueKind.Folder
                ? (quarantine is not null
                    ? quarantine.QuarantineDirectory(file.Path)
                    : FileOps.DeleteDirectory(file.Path))
                : (quarantine is not null && item.Kind != ResidueKind.Shortcut
                    ? quarantine.QuarantineFile(file.Path)
                    : FileOps.DeleteFile(file.Path)),

            RegistryKeyTarget key => RegistryResidueOps.DeleteKey(key.Location),

            RegistryValueTarget value => RegistryResidueOps.DeleteValue(value.Location, value.ValueName),

            ServiceTarget service => DeleteService(service.ServiceName),

            ScheduledTaskTarget task => DeleteScheduledTask(task.TaskName),

            _ => new DeletionResult(false, item.Path, false, "No removal method for this item."),
        };
    }

    private static DeletionResult DeleteService(string name)
    {
        try
        {
            using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);

            // Only the service's own key is touched. Never the whole Services tree.
            root.DeleteSubKeyTree($@"SYSTEM\CurrentControlSet\Services\{name}", throwOnMissingSubKey: false);
            return new DeletionResult(true, name, false, null);
        }
        catch (Exception ex)
        {
            return new DeletionResult(false, name, false, ex.Message);
        }
    }

    private static DeletionResult DeleteScheduledTask(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Native.SystemExecutable("schtasks.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/Delete");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(taskName);
            psi.ArgumentList.Add("/F");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new DeletionResult(false, taskName, false, "Could not start schtasks.exe.");
            }

            process.WaitForExit(30_000);
            return process.ExitCode == 0
                ? new DeletionResult(true, taskName, false, null)
                : new DeletionResult(false, taskName, false, $"schtasks exited with {process.ExitCode}");
        }
        catch (Exception ex)
        {
            return new DeletionResult(false, taskName, false, ex.Message);
        }
    }
}
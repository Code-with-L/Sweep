using System.Text.Json;

namespace Sweep;

/// <summary>
/// A removal that started but did not finish, so it can be picked up later.
///
/// This exists because uninstallers ask the machine to reboot and go away mid-run. Without
/// it, the leftovers of a half-finished removal are simply never looked at.
///
/// The record deliberately stores the identifying details rather than a live object: by the
/// time it is read again the program is gone from the registry and the object is stale.
/// </summary>
internal sealed record PendingUninstall(
    string Name,
    string UninstallString,
    string InstallLocation,
    string Publisher,
    DateTime StartedUtc,
    string Reason)
{
    /// <summary>
    /// How long ago the removal was interrupted.
    ///
    /// Surfaced to the user before anything is deleted. A leftover list computed hours ago
    /// describes a machine that has changed since, so acting on it silently would be unsafe.
    /// </summary>
    public TimeSpan Age => DateTime.UtcNow - StartedUtc;

    public string AgeDescription
    {
        get
        {
            TimeSpan age = Age;

            if (age.TotalMinutes < 2)
            {
                return "moments ago";
            }

            if (age.TotalHours < 1)
            {
                return $"{(int)age.TotalMinutes} minutes ago";
            }

            if (age.TotalDays < 1)
            {
                return $"{(int)age.TotalHours} hours ago";
            }

            return age.TotalDays < 2 ? "yesterday" : $"{(int)age.TotalDays} days ago";
        }
    }

    /// <summary>
    /// Whether the record is old enough that the user should be warned rather than simply
    /// offered the scan. Under two hours the machine is very likely unchanged.
    /// </summary>
    public bool IsStale => Age > TimeSpan.FromHours(2);

    /// <summary>Rebuilds a scan target from the stored details.</summary>
    public InstalledApp ToApp() => new()
    {
        DisplayName = Name,
        UninstallString = UninstallString,
        InstallLocation = InstallLocation,
        Publisher = Publisher,
    };
}

internal static class PendingUninstallStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly object Gate = new();

    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sweep",
        "pending-uninstall.json");

    /// <summary>Records an interrupted removal. One at a time; the newest replaces older.</summary>
    public static void Save(PendingUninstall pending)
    {
        lock (Gate)
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(Path_);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(Path_, JsonSerializer.Serialize(pending, Options));
                LogStore.Warn(
                    $"Removal of \"{pending.Name}\" was interrupted ({pending.Reason}); " +
                    "saved so it can be resumed.");
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Could not save pending uninstall: {ex.Message}");
            }
        }
    }

    /// <summary>Reads the pending record, or null when there is nothing to resume.</summary>
    public static PendingUninstall? Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(Path_))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<PendingUninstall>(
                    File.ReadAllText(Path_), Options);
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Could not read pending uninstall: {ex.Message}");
                return null;
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(Path_))
                {
                    File.Delete(Path_);
                }
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Could not clear pending uninstall: {ex.Message}");
            }
        }
    }
}
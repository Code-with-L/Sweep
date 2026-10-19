using Microsoft.Win32;

namespace Sweep;

/// <summary>
/// Enumerates installed programs from the Windows uninstall registry keys.
/// Uses explicit RegistryView handling rather than relying on process bitness, which is
/// what makes this correct under WOW64 and on Windows-on-ARM.
/// </summary>
public static class RegistryScanner
{
    public const string UninstallBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View, string Label)[] Sources =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, "Machine 64"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "Machine 32"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, "User 64"),
        (RegistryHive.CurrentUser, RegistryView.Registry32, "User 32"),
    ];

    private static readonly HashSet<string> UpdatePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update for ", "Hotfix for ", "Security Update for ", "Update (", "Security Update (",
    };

    private static readonly HashSet<string> UpdateReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update", "Hotfix", "Security Update",
    };

    public static IReadOnlyList<(RegistryHive Hive, RegistryView View, string Label)> SourcesInfo => Sources;

    public static List<InstalledApp> Scan(CancellationToken cancellationToken = default)
    {
        var results = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view, _) in Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Under over-the-shoulder elevation HKCU belongs to the administrator at the UAC
            // prompt, not to the user who launched Sweep. Listing their programs as the
            // invoker's - and offering them for removal - would be wrong, so skip the hive.
            if (hive == RegistryHive.CurrentUser && RunContext.IsCrossUser)
            {
                continue;
            }

            RegistryKey root;
            try
            {
                root = RegistryKey.OpenBaseKey(hive, view);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            using (root)
            {
                RegistryKey? uninstall;
                try
                {
                    uninstall = root.OpenSubKey(UninstallBase, writable: false);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    continue;
                }

                if (uninstall is null)
                {
                    continue;
                }

                using (uninstall)
                {
                    string[] subKeys;
                    try
                    {
                        subKeys = uninstall.GetSubKeyNames();
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        continue;
                    }

                    foreach (string subKeyName in subKeys)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        RegistryKey? subKey;
                        try
                        {
                            subKey = uninstall.OpenSubKey(subKeyName, writable: false);
                        }
                        catch
                        {
                            continue;
                        }

                        if (subKey is null)
                        {
                            continue;
                        }

                        using (subKey)
                        {
                            if (TryReadApp(subKey, subKeyName, hive, view, out var app) && app is not null)
                            {
                                string key = $"{app.DisplayName}|{app.DisplayVersion}|{app.UninstallString}";
                                if (seen.Add(key))
                                {
                                    results.Add(app);
                                }
                            }
                        }
                    }
                }
            }
        }

        return results
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool TryReadApp(
        RegistryKey key,
        string subKeyName,
        RegistryHive hive,
        RegistryView view,
        out InstalledApp? app)
    {
        app = null;

        string displayName = ReadString(key, "DisplayName") ?? string.Empty;
        if (displayName.Length == 0)
        {
            return false;
        }

        if (ReadInt(key, "SystemComponent") == 1)
        {
            return false;
        }

        // Components are children of a product; listing them separately is noise.
        if (!string.IsNullOrWhiteSpace(ReadString(key, "ParentKeyName")))
        {
            return false;
        }

        string releaseType = ReadString(key, "ReleaseType") ?? string.Empty;
        if (UpdateReleaseTypes.Contains(releaseType))
        {
            return false;
        }

        foreach (string prefix in UpdatePrefixes)
        {
            if (displayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string uninstall = ReadString(key, "UninstallString") ?? string.Empty;
        string location = ReadString(key, "InstallLocation") ?? string.Empty;

        // An entry with neither an uninstaller nor an install path is inert.
        if (uninstall.Length == 0 && location.Length == 0)
        {
            return false;
        }

        int estimatedSizeKb = ReadInt(key, "EstimatedSize") ?? 0;

        app = new InstalledApp
        {
            DisplayName = displayName,
            DisplayVersion = ReadString(key, "DisplayVersion") ?? string.Empty,
            Publisher = ReadString(key, "Publisher") ?? string.Empty,
            InstallLocation = location,
            UninstallString = uninstall,
            QuietUninstallString = ReadString(key, "QuietUninstallString") ?? string.Empty,
            DisplayIcon = ReadString(key, "DisplayIcon") ?? string.Empty,
            InstallDateRaw = ReadRawString(key, "InstallDate") ?? string.Empty,
            Comments = ReadString(key, "Comments") ?? string.Empty,
            UrlInfoAbout = ReadString(key, "URLInfoAbout") ?? string.Empty,
            HelpLink = ReadString(key, "HelpLink") ?? string.Empty,
            Contact = ReadString(key, "Contact") ?? string.Empty,
            EstimatedSizeBytes = estimatedSizeKb > 0 ? (long)estimatedSizeKb * 1024 : 0,
            SystemComponent = ReadInt(key, "SystemComponent") == 1,
            Location = new RegLocation(hive, view, UninstallBase + "\\" + subKeyName),
        };

        return true;
    }

    private static string? ReadRawString(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name, null) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static int? ReadInt(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name, null) switch
            {
                int i => i,
                long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
                uint u when u <= int.MaxValue => (int)u,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Removes an app's uninstall entry (used after a manual removal).</summary>
    public static bool DeleteEntry(RegLocation location)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(location.Hive, location.View);
            root.DeleteSubKeyTree(location.SubKey, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex)
        {
            LogStore.Error($"Could not delete registry entry {location}", ex);
            return false;
        }
    }

    public static bool EntryExists(RegLocation location)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(location.Hive, location.View);
            using var key = root.OpenSubKey(location.SubKey, writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }
}

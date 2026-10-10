using Microsoft.Win32;

namespace Sweep;

/// <summary>Identifies a registry key precisely, including which WOW64 view it came from.</summary>
public readonly record struct RegLocation(RegistryHive Hive, RegistryView View, string SubKey)
{
    public override string ToString()
    {
        string root = Hive switch
        {
            RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
            RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
            RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
            RegistryHive.Users => "HKEY_USERS",
            _ => Hive.ToString(),
        };
        string view = View switch
        {
            RegistryView.Registry32 => " [32-bit view]",
            RegistryView.Registry64 => " [64-bit view]",
            _ => "",
        };
        return $"{root}\\{SubKey}{view}";
    }
}

/// <summary>One row in the installed-programs list.</summary>
public sealed class InstalledApp
{
    public string DisplayName { get; init; } = "";
    public string DisplayVersion { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public string DisplayIcon { get; init; } = "";
    public string InstallDateRaw { get; init; } = "";
    public long EstimatedSizeBytes { get; init; }
    public bool SystemComponent { get; init; }
    public RegLocation Location { get; init; }

    public bool HasUninstaller => !string.IsNullOrWhiteSpace(UninstallString);
    public bool HasQuietUninstaller => !string.IsNullOrWhiteSpace(QuietUninstallString);
    public bool IsPerUser => Location.Hive == RegistryHive.CurrentUser;

    public string ScopeLabel => IsPerUser
        ? "This user only"
        : Location.View == RegistryView.Registry32 ? "All users (32-bit)" : "All users";

    public DateTime? InstallDate => ParseInstallDate(InstallDateRaw);

    public string SizeDisplay => EstimatedSizeBytes switch
    {
        <= 0 => string.Empty,
        < 1024L * 1024 => $"{EstimatedSizeBytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{EstimatedSizeBytes / (1024d * 1024):0.#} MB",
        _ => $"{EstimatedSizeBytes / (1024d * 1024 * 1024):0.##} GB",
    };

    /// <summary>
    /// The executable this app would show in Explorer, used for icon extraction and as a
    /// fallback when no uninstaller string exists.
    /// </summary>
    public string? ExecutablePath
    {
        get
        {
            var icon = IconTargetFromDisplayIcon();
            if (icon is not null)
            {
                return icon;
            }

            var fromUninstall = ExecutableFromCommand(UninstallString);
            if (fromUninstall is not null)
            {
                return fromUninstall;
            }

            return GuessFromInstallLocation();
        }
    }

    private string? IconTargetFromDisplayIcon()
    {
        if (string.IsNullOrWhiteSpace(DisplayIcon))
        {
            return null;
        }

        // Format is "path,index" or just "path". Strip the trailing ",N".
        string value = DisplayIcon.Trim();
        int comma = value.LastIndexOf(',');
        if (comma > 1)
        {
            string tail = value[(comma + 1)..].Trim();
            if (int.TryParse(tail, out _))
            {
                value = value[..comma].Trim();
            }
        }

        return File.Exists(value) ? value : null;
    }

    private static string? ExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string exe = CommandLine.Split(command).FileName;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return null;
        }

        exe = exe.Trim().Trim('"');
        if (File.Exists(exe))
        {
            return exe;
        }

        // Environment variables are common in uninstall strings.
        if (exe.Contains('%'))
        {
            string expanded = Environment.ExpandEnvironmentVariables(exe);
            if (File.Exists(expanded))
            {
                return expanded;
            }
        }

        return null;
    }

    private string? GuessFromInstallLocation()
    {
        if (string.IsNullOrWhiteSpace(InstallLocation))
        {
            return null;
        }

        try
        {
            string dir = InstallLocation.Trim().Trim('"');
            if (!Directory.Exists(dir))
            {
                return null;
            }

            // Prefer an exe whose name matches the product, else the largest exe present.
            var candidates = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Take(200).ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            string compact = NameTokens.Compact(DisplayName);
            var best = candidates
                .OrderByDescending(c => NameTokens.Compact(Path.GetFileNameWithoutExtension(c)).Contains(compact, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(c => new FileInfo(c).Length)
                .First();
            return best;
        }
        catch
        {
            return null;
        }
    }

    internal static DateTime? ParseInstallDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (long.TryParse(raw.Trim(), out long number))
        {
            // Windows Installer stores either a FILETIME or a Unix-epoch second count.
            if (number > 100_000_000_000_000L)
            {
                try
                {
                    return DateTime.FromFileTimeUtc(number).ToLocalTime();
                }
                catch
                {
                    return null;
                }
            }

            if (number is > 946_684_800L and < 4_102_444_800L)
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeSeconds(number).LocalDateTime;
                }
                catch
                {
                    return null;
                }
            }
        }

        return DateTime.TryParse(raw, out var parsed) ? parsed : null;
    }

    public override string ToString() => DisplayName;
}
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
    public string Comments { get; init; } = "";
    public string UrlInfoAbout { get; init; } = "";
    public string HelpLink { get; init; } = "";
    public string Contact { get; init; } = "";
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

    private Lazy<string?> _peArchitecture = null!;

    private string? _drive;

    private string PeArchitecture()
    {
        _peArchitecture ??= new Lazy<string?>(
            () => PeHeader.ReadArchitecture(ExecutablePath ?? string.Empty),
            LazyThreadSafetyMode.ExecutionAndPublication);

        return _peArchitecture.Value ?? string.Empty;
    }

    /// <summary>Drive letter the program lives on, used by the drive filter.</summary>
    public string Drive => _drive ??= ResolveDrive();

    /// <summary>
    /// "32-bit" or "64-bit", determined from the real PE header of the program rather than
    /// from which registry view the entry came from. The view only tells us how the entry
    /// is stored, not how the program is built.
    /// </summary>
    public string Architecture
    {
        get
        {
            string detected = PeArchitecture();
            return detected.Length > 0
                ? detected
                : Location.View == RegistryView.Registry32 ? "32-bit" : "64-bit";
        }
    }

    private string ResolveDrive()
    {
        string? path = ExecutablePath ?? (string.IsNullOrWhiteSpace(InstallLocation) ? null : InstallLocation);
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            string? root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? string.Empty : root;
        }
        catch
        {
            return string.Empty;
        }
    }

    public string WebsiteDisplay => FirstNonEmpty(UrlInfoAbout, Contact);

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

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
using System.Globalization;
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

/// <summary>
/// One row in the installed-programs list.
///
/// Everything here is split into two groups. Values that come straight out of the registry
/// are available immediately. Values that need the disk - which executable the program has,
/// its architecture, its company, its size, when it was installed - are resolved once by
/// <see cref="EnsureMetadataResolved"/> on a background thread and cached here.
///
/// The distinction matters: the data grid raises CellValueNeeded on the UI thread, so any
/// directory enumeration or file stat reached from a display property freezes the window.
/// </summary>
public sealed class InstalledApp
{
    // ---- Straight from the registry ----

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

    // ---- Resolved on a background thread ----

    private string? _resolvedExe;
    private bool _exeResolved;
    private string? _architecture;
    private string? _company;
    private string? _version;
    private string? _description;
    private string? _drive;
    private long? _computedSize;
    private DateTime? _computedInstallDate;

    public bool HasUninstaller => !string.IsNullOrWhiteSpace(UninstallString);
    public bool HasQuietUninstaller => !string.IsNullOrWhiteSpace(QuietUninstallString);
    public bool IsPerUser => Location.Hive == RegistryHive.CurrentUser;

    public string ScopeLabel => IsPerUser
        ? "This user only"
        : Location.View == RegistryView.Registry32 ? "All users (32-bit)" : "All users";

    public DateTime? InstallDate => ParseInstallDate(InstallDateRaw);

    /// <summary>
    /// The program's main executable, or null when it cannot be determined.
    ///
    /// Resolution can involve enumerating an install folder, so it must not be called from a
    /// UI callback. Use <see cref="ResolvedExecutablePath"/> on the grid path.
    /// </summary>
    public string? ExecutablePath
    {
        get
        {
            EnsureExecutableResolved();
            return _resolvedExe;
        }
    }

    /// <summary>Cached executable path. Null until the background pass has run.</summary>
    public string? ResolvedExecutablePath => _resolvedExe;

    private void EnsureExecutableResolved()
    {
        if (_exeResolved)
        {
            return;
        }

        _exeResolved = true;
        _resolvedExe = ResolveExecutablePath();
    }

    private string? ResolveExecutablePath()
    {
        var fromDisplayIcon = IconTargetFromDisplayIcon();
        if (fromDisplayIcon is not null)
        {
            return fromDisplayIcon;
        }

        var fromUninstall = ExecutableFromCommand(UninstallString);
        if (fromUninstall is not null)
        {
            return fromUninstall;
        }

        return GuessFromInstallLocation();
    }

    /// <summary>Drive the program lives on, used by the drive filter. Cheap once resolved.</summary>
    public string Drive
    {
        get
        {
            if (_drive is not null)
            {
                return _drive;
            }

            string? path = _resolvedExe ?? (string.IsNullOrWhiteSpace(InstallLocation) ? null : InstallLocation);
            _drive = path is null ? string.Empty : SafePathRoot(path);
            return _drive;
        }
    }

    private static string SafePathRoot(string path)
    {
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

    /// <summary>"32-bit" or "64-bit", from the PE header, not from the registry view.</summary>
    public string Architecture
    {
        get
        {
            if (_architecture is not null)
            {
                return _architecture;
            }

            // Before the background pass runs, the registry view is a reasonable guess and
            // costs nothing.
            return Location.View == RegistryView.Registry32 ? "32-bit" : "64-bit";
        }
    }

    public string Company => _company ?? string.Empty;

    public string VersionDisplay => _version ?? string.Empty;

    public string CommentDisplay => _description ?? string.Empty;

    public DateTime? InstallDateDisplay => InstallDate ?? _computedInstallDate;

    /// <summary>Registry-estimated size only. See <see cref="SizeText"/> for the resolved value.</summary>
    public string SizeDisplay => SizeText;

    public long SizeBytes => _computedSize ?? (EstimatedSizeBytes > 0 ? EstimatedSizeBytes : 0);

    public string SizeText
    {
        get
        {
            long bytes = SizeBytes;
            return bytes switch
            {
                <= 0 => string.Empty,
                < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
                < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
                _ => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
            };
        }
    }

    public string WebsiteDisplay => FirstNonEmpty(UrlInfoAbout, Contact);

    /// <summary>
    /// Resolves everything the grid displays. Call from a background thread, once.
    /// The budget bounds the one expensive step: walking the install folder for a size.
    /// </summary>
    public void EnsureMetadataResolved(TimeSpan sizeBudget, CancellationToken cancellationToken = default)
    {
        EnsureExecutableResolved();

        if (_architecture is null && _resolvedExe is not null)
        {
            _architecture = PeHeader.ReadArchitecture(_resolvedExe);
        }

        if (_resolvedExe is not null)
        {
            var info = Metadata.ForPath(_resolvedExe);
            if (info is not null)
            {
                _company ??= Metadata.CompanyFor(this, info);
                _version ??= Metadata.VersionFor(this, info);
                _description ??= Metadata.DescriptionFor(this, info);
            }
        }

        if (_computedInstallDate is null && InstallDate is null)
        {
            _computedInstallDate = Metadata.FolderCreationDate(this);
        }

        if (_computedSize is null && EstimatedSizeBytes <= 0)
        {
            _computedSize = Metadata.SizeFor(this, sizeBudget, cancellationToken);
        }
    }

    private string? IconTargetFromDisplayIcon()
    {
        if (string.IsNullOrWhiteSpace(DisplayIcon))
        {
            return null;
        }

        string value = DisplayIcon.Trim();
        int comma = value.LastIndexOf(',');
        if (comma > 1 && int.TryParse(value[(comma + 1)..].Trim(), out _))
        {
            value = value[..comma].Trim();
        }

        value = Environment.ExpandEnvironmentVariables(value.Trim('"'));
        return value.Length > 0 && File.Exists(value) ? value : null;
    }

    private static string? ExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string exe = CommandLine.ResolveExecutable(CommandLine.Split(command).FileName);
        return File.Exists(exe) ? exe : null;
    }

    private string? GuessFromInstallLocation()
    {
        if (string.IsNullOrWhiteSpace(InstallLocation))
        {
            return null;
        }

        try
        {
            string dir = Environment.ExpandEnvironmentVariables(InstallLocation.Trim().Trim('"'));
            if (!Directory.Exists(dir))
            {
                return null;
            }

            string compact = NameTokens.Compact(DisplayName);

            // An exe whose own name matches the product beats any other candidate, so the
            // uninstaller stub and the redistributable bootstrapper do not win by default.
            var candidates = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .Take(300)
                .Select(path => (Path: path, Score: Score(path, compact)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => SafeLength(x.Path))
                .Select(x => x.Path)
                .ToList();

            return candidates.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            // The file can vanish between enumeration and stat.
            return 0;
        }
    }

    private static int Score(string path, string compact)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string lower = name.ToLowerInvariant();

        // Uninstallers, bootstrappers and redistributables are never the product icon.
        if (lower.StartsWith("unins", StringComparison.Ordinal) ||
            lower.Contains("uninstall", StringComparison.Ordinal) ||
            lower.Contains("setup", StringComparison.Ordinal) ||
            lower.Contains("install", StringComparison.Ordinal) ||
            lower.Contains("redist", StringComparison.Ordinal) ||
            lower.Contains("vcredist", StringComparison.Ordinal) ||
            lower.Contains("crashreport", StringComparison.Ordinal) ||
            lower.Contains("update", StringComparison.Ordinal) ||
            lower.Contains("helper", StringComparison.Ordinal))
        {
            return -1;
        }

        string nameCompact = NameTokens.Compact(name);
        if (compact.Length > 0 && string.Equals(nameCompact, compact, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return nameCompact.Length >= 4 ? 1 : 0;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    internal static DateTime? ParseInstallDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (long.TryParse(raw.Trim(), out long number))
        {
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

// Almost every registry InstallDate is an 8-digit yyyyMMdd string. General parsing
        // refuses that shape because it cannot be told apart from ddMMyyyy or MMddyyyy,
        // so it silently returns null. Match the format explicitly before falling back.
        string text = raw.Trim();
        if (text.Length == 8 && text.All(char.IsDigit))
        {
            if (DateTime.TryParseExact(
                    text,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime exact))
            {
                return exact;
            }
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    public override string ToString() => DisplayName;
}
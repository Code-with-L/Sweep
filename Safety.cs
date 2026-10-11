using Microsoft.Win32;

namespace Sweep;

public enum SafetyLevel
{
    /// <summary>Deleting this cannot affect anything outside the app itself.</summary>
    Safe,

    /// <summary>Could plausibly be shared. Worth a second look, offered with a warning.</summary>
    Caution,

    /// <summary>Could break the system or unrelated software. Never removed silently.</summary>
    Dangerous,
}

public sealed record SafetyAssessment(SafetyLevel Level, string Reason);

/// <summary>
/// Central guardrails.
///
/// The residue scanner works by name matching, so it will eventually propose something
/// that belongs to Windows, to a shared runtime, or to another application entirely. These
/// checks exist so that a matching mistake can never become a broken machine.
///
/// Everything here is a hard deny-list. It is intentionally over-inclusive: refusing to
/// offer a deletion is a far cheaper mistake than deleting it.
/// </summary>
public static class Safety
{
    private static List<string>? _systemRoots;
    private static List<string>? _sharedRoots;

    /// <summary>Directory names that must never be deleted regardless of a name match.</summary>
    private static readonly string[] ProtectedFolderNames =
    [
        "windows", "system32", "syswow64", "winsxs", "windowsapps", "assembly",
        "servicing", "boot", "recovery", "perflogs", "system volume information",
        "program files", "program files (x86)", "programdata",
        "package cache", "windowsinstaller", "installer", "microsoft",
        "appdata", "users", "user profile", "temp", "system volume",
        "$recycle.bin", "system32", "config.systemprofile", "recovery",
    ];

    private static readonly string[] ProtectedFileNames =
    [
        "ntldr", "bootmgr", "bootfont", "ntdetect.com", "pagefile.sys",
        "swapfile.sys", "hiberfil.sys", "sam", "security", "system", "software",
    ];

    /// <summary>
    /// Directories that are off limits at every depth. These belong to Windows or to the
    /// sandboxing of other applications.
    /// </summary>
    private static List<string> SystemRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var paths = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.Favorites),
            Environment.GetFolderPath(Environment.SpecialFolder.History),
            Path.Combine(local, "Packages"),
            Path.Combine(local, "Temp"),
            Path.Combine(local, "Microsoft", "Windows"),
            Path.Combine(local, "Microsoft", "OneDrive"),
            Path.Combine(local, "Microsoft", "EdgeUser Data"),
            Path.Combine(local, "CrashDumps"),
            Path.Combine(local, "D3DSCache"),
            Path.Combine(common, "Microsoft"),
            Path.Combine(common, "Package Cache"),
            Path.Combine(common, "Windows Defender"),
            @"C:\Windows",
            @"C:\Windows\System32",
            @"C:\$Recycle.Bin",
            @"C:\System Volume Information",
        };

        return Clean(paths);
    }

    /// <summary>
    /// Shared roots that must not be deleted themselves, but whose per-application
    /// subfolders are exactly what this tool exists to remove.
    /// </summary>
    private static List<string> SharedRoots()
    {
        var paths = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @"C:\Program Files",
            @"C:\Program Files (x86)",
            @"C:\ProgramData",
        };

        if (Native.ProgramFilesX86 is { Length: > 0 } pfx86)
        {
            paths.Add(pfx86);
        }

        if (Native.UserProgramFiles is { Length: > 0 } upf)
        {
            paths.Add(upf);
        }

        return Clean(paths);
    }

    private static List<string> Clean(IEnumerable<string> paths) =>
        paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Length)
            .ToList();

    public static IReadOnlyList<string> SystemRootsCache => _systemRoots ??= SystemRoots();

    public static IReadOnlyList<string> SharedRootsCache => _sharedRoots ??= SharedRoots();

    /// <summary>
    /// True when the path must never be removed automatically.
    ///
    /// Being inside a shared root is not enough on its own. "C:\Users\&lt;you&gt;\AppData\
    /// Local\SomeApp" is inside the profile, and removing it is precisely the job. Only the
    /// shared root itself, plus everything beneath a genuine system root, is refused.
    /// </summary>
    public static bool IsProtectedLocation(string path)
    {
        _systemRoots ??= SystemRoots();
        _sharedRoots ??= SharedRoots();

        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd('\\');
        }
        catch
        {
            return true;
        }

        // Everything under a system root, and the system root itself.
        foreach (string root in _systemRoots)
        {
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Only the shared root itself. Its children are per-application and fair game.
        foreach (string root in _sharedRoots)
        {
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string name in ProtectedFolderNames)
        {
            if (full.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string leaf;
        try
        {
            leaf = Path.GetFileName(full);
        }
        catch
        {
            return true;
        }

        if (ProtectedFileNames.Contains(leaf, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // A drive root such as C:\ is never a leftover.
        return full.Length <= 3;
    }

    /// <summary>Registry locations shared by many applications.</summary>
    public static bool IsProtectedRegistryKey(RegLocation location)
    {
        // Non-registry items carry a default RegLocation with a null SubKey.
        if (location.SubKey is not { Length: > 0 } subKey)
        {
            return false;
        }

        const string root = @"SOFTWARE\";

        if (!subKey.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = subKey.Split('\\');

        // HKLM\SOFTWARE\Microsoft is shared by the entire operating system.
        if (location.Hive == RegistryHive.LocalMachine &&
            segments.Length > 1 &&
            segments[1].Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string segment in segments)
        {
            if (segment.Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("CLSID", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("TypeLib", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Installer", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Grades an item and explains why, so the UI can show a real warning.</summary>
    public static SafetyAssessment Assess(ResidueItem item)
    {
        if (item.Kind is ResidueKind.Folder or ResidueKind.File or ResidueKind.Shortcut)
        {
            if (IsProtectedLocation(item.Path))
            {
                return new SafetyAssessment(SafetyLevel.Dangerous,
                    "This path is inside a shared or system location.");
            }

            return new SafetyAssessment(SafetyLevel.Safe, "Belongs to the removed program.");
        }

        if (IsProtectedRegistryKey(item.Reg))
        {
            return new SafetyAssessment(SafetyLevel.Dangerous,
                "This registry key is shared with the operating system or other applications.");
        }

        return item.Kind switch
        {
            ResidueKind.Service => new SafetyAssessment(SafetyLevel.Caution,
                "Removing a service can stop a dependent application from working."),

            ResidueKind.ScheduledTask => new SafetyAssessment(SafetyLevel.Caution,
                "Removing a scheduled task cancels recurring work."),

            ResidueKind.Font => new SafetyAssessment(SafetyLevel.Caution,
                "Removing a font makes text render with a substitute until sign-out."),

            ResidueKind.EnvironmentVariable => new SafetyAssessment(SafetyLevel.Caution,
                "Removing an environment variable can break shells and build tools."),

            ResidueKind.StartupEntry => new SafetyAssessment(SafetyLevel.Caution,
                "Removing a startup entry stops the program launching at sign-in."),

            ResidueKind.RegistryKey => new SafetyAssessment(SafetyLevel.Safe,
                "Registry key belonging to the removed program."),

            ResidueKind.RegistryValue => new SafetyAssessment(SafetyLevel.Caution,
                "This value may be a file-type association used by other applications."),

            _ => new SafetyAssessment(SafetyLevel.Safe, string.Empty),
        };
    }

    /// <summary>Items that must never be offered, whatever the matcher says.</summary>
    public static bool IsHardBlocked(ResidueItem item) =>
        item.Kind is ResidueKind.Folder or ResidueKind.File or ResidueKind.Shortcut
            ? IsProtectedLocation(item.Path)
            : IsProtectedRegistryKey(item.Reg);
}
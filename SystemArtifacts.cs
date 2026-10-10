using System.Diagnostics;
using Microsoft.Win32;

namespace Sweep;

/// <summary>
/// Targeted scans for the artefacts an uninstaller usually misses, because these are
/// identified by the data inside a registry value or a file name rather than by a folder
/// named after the product. A name-only sweep structurally cannot find any of them.
/// </summary>
public static class SystemArtifacts
{
    private static readonly char[] PathSeparators =
        ['\\', '/', ':', '"', '\'', ',', ' ', '\t', ';', '|'];

    /// <summary>
    /// Matches a path or command line segment by segment. Matching the whole string
    /// compacted would let a token match across unrelated path boundaries.
    /// </summary>
    public static string? MatchSegmented(string? candidate, ResidueTokenSet tokens, bool allowWeak)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        foreach (string segment in candidate.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string? match = tokens.Match(segment) ?? (allowWeak ? tokens.MatchWeak(segment) : null);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    // ---- Startup entries -------------------------------------------------

    private static readonly (RegistryHive Hive, RegistryView View, string Path)[] StartupKeys =
    [
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
    ];

    public static IEnumerable<ResidueItem> StartupEntries(
        ResidueTokenSet tokens, bool allowWeak, CancellationToken cancellationToken)
    {
        foreach (var (hive, view, path) in StartupKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var (name, data) in ReadValues(hive, view, path))
            {
                // Either the entry is named after the product, or the command points at it.
                string? match = tokens.Match(name) ?? MatchSegmented(data, tokens, allowWeak);
                if (match is null)
                {
                    continue;
                }

                yield return new ResidueItem
                {
                    Kind = ResidueKind.StartupEntry,
                    Path = $@"{HiveName(hive)}\{path}\{name}",
                    DisplayName = name,
                    MatchedToken = match,
                    Reg = new RegLocation(hive, view, path),
                    ValueName = name,
                    Target = new RegistryValueTarget(new RegLocation(hive, view, path), name),
                    Confidence = Confidence.Moderate,
                };
            }
        }

        foreach (string folder in StartupFolders())
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (string file in SafeEnumerateShortcuts(folder, cancellationToken))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                string? match = tokens.Match(name);
                if (match is null)
                {
                    continue;
                }

                yield return new ResidueItem
                {
                    Kind = ResidueKind.Shortcut,
                    Path = file,
                    DisplayName = name,
                    MatchedToken = match,
                    Target = new FileTarget(file),
                    Confidence = Confidence.Moderate,
                };
            }
        }
    }

    private static IEnumerable<string> StartupFolders()
    {
        foreach (Environment.SpecialFolder folder in
                 new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateShortcuts(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dir = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.lnk", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                yield return file;
            }

            try
            {
                foreach (string child in Directory.GetDirectories(dir))
                {
                    var attrs = File.GetAttributes(child);
                    if ((attrs & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(child);
                    }
                }
            }
            catch
            {
                // Unreadable subfolder.
            }
        }
    }

    // ---- Services --------------------------------------------------------

    public static IEnumerable<ResidueItem> Services(
        ResidueTokenSet tokens, bool allowWeak, CancellationToken cancellationToken)
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var (serviceName, imagePath) in ReadServices(view, cancellationToken))
            {
                string? match = tokens.Match(serviceName) ?? MatchSegmented(imagePath, tokens, allowWeak);
                if (match is null)
                {
                    continue;
                }

                // A service whose executable has been deleted is an orphan, and the
                // strongest possible signal that the owning product is gone.
                bool orphaned = ServiceTargetMissing(imagePath);

                yield return new ResidueItem
                {
                    Kind = ResidueKind.Service,
                    Path = serviceName,
                    DisplayName = serviceName,
                    MatchedToken = match,
                    Target = new ServiceTarget(serviceName),
                    Confidence = orphaned ? Confidence.Safe : Confidence.Moderate,
                    Note = orphaned ? "executable missing (orphaned)" : "executable still present",
                };
            }
        }
    }

    private static bool ServiceTargetMissing(string imagePath)
    {
        try
        {
            string path = imagePath.Trim().Trim('"');
            int space = path.IndexOf(' ');
            if (space > 0 && !File.Exists(path))
            {
                path = path[..space];
            }

            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<(string Name, string ImagePath)> ReadServices(
        RegistryView view, CancellationToken cancellationToken)
    {
        const string servicesPath = @"SYSTEM\CurrentControlSet\Services";

        RegistryKey? baseKey = null;
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            baseKey = hive.OpenSubKey(servicesPath, writable: false);
        }
        catch
        {
            yield break;
        }

        if (baseKey is null)
        {
            yield break;
        }

        using (baseKey)
        {
            string[] names;
            try
            {
                names = baseKey.GetSubKeyNames();
            }
            catch
            {
                yield break;
            }

            foreach (string name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? imagePath = null;
                try
                {
                    using var key = baseKey.OpenSubKey(name, writable: false);
                    if (key?.GetValue("ImagePath") is string value)
                    {
                        imagePath = value;
                    }
                }
                catch
                {
                    // Unreadable service; skip.
                }

                if (imagePath is not null)
                {
                    yield return (name, imagePath);
                }
            }
        }
    }

    // ---- Scheduled tasks -------------------------------------------------

    public static IEnumerable<ResidueItem> ScheduledTasks(
        ResidueTokenSet tokens, bool allowWeak, CancellationToken cancellationToken)
    {
        foreach (var (taskName, action) in QueryScheduledTasks(cancellationToken))
        {
            // Tasks under \Microsoft\Windows are owned by the operating system.
            if (taskName.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? match = tokens.Match(taskName) ?? MatchSegmented(action, tokens, allowWeak);
            if (match is null)
            {
                continue;
            }

            yield return new ResidueItem
            {
                Kind = ResidueKind.ScheduledTask,
                Path = taskName,
                DisplayName = taskName.TrimStart('\\'),
                MatchedToken = match,
                Target = new ScheduledTaskTarget(taskName),
                Confidence = Confidence.Moderate,
                Note = string.IsNullOrEmpty(action) ? null : action,
            };
        }
    }

    /// <summary>
    /// Uses the LIST format rather than CSV so that command lines containing commas and
    /// quotes do not need to be unescaped.
    /// </summary>
    private static IEnumerable<(string Name, string Action)> QueryScheduledTasks(CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/Query");
        psi.ArgumentList.Add("/FO");
        psi.ArgumentList.Add("LIST");
        psi.ArgumentList.Add("/V");

        string output;
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                yield break;
            }

            output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(45_000))
            {
                process.Kill(entireProcessTree: true);
                yield break;
            }
        }
        catch (Exception ex)
        {
            LogStore.Warn($"Could not enumerate scheduled tasks: {ex.Message}");
            yield break;
        }

        string name = string.Empty;
        string action = string.Empty;

        foreach (string raw in output.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                // Blank line terminates a record.
                if (name.Length > 0)
                {
                    yield return (name, action);
                }

                name = string.Empty;
                action = string.Empty;
                continue;
            }

            if (line.StartsWith("TaskName:", StringComparison.OrdinalIgnoreCase))
            {
                name = line["TaskName:".Length..].Trim();
            }
            else if (line.StartsWith("Task To Run:", StringComparison.OrdinalIgnoreCase))
            {
                action = line["Task To Run:".Length..].Trim();
            }
        }

        if (name.Length > 0)
        {
            yield return (name, action);
        }
    }

    // ---- Environment variables -------------------------------------------

    private static readonly (RegistryHive Hive, RegistryView View, string Path)[] EnvironmentKeys =
    [
        (RegistryHive.CurrentUser, RegistryView.Registry64, "Environment"),
        (RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
    ];

    public static IEnumerable<ResidueItem> EnvironmentVariables(
        ResidueTokenSet tokens, CancellationToken cancellationToken)
    {
        foreach (var (hive, view, path) in EnvironmentKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var (name, _) in ReadValues(hive, view, path))
            {
                // Only the variable NAME is matched. Editing PATH contents is far too
                // dangerous to automate.
                string? match = tokens.Match(name);
                if (match is null)
                {
                    continue;
                }

                yield return new ResidueItem
                {
                    Kind = ResidueKind.EnvironmentVariable,
                    Path = $@"{HiveName(hive)}\{path}\{name}",
                    DisplayName = name,
                    MatchedToken = match,
                    Reg = new RegLocation(hive, view, path),
                    ValueName = name,
                    Target = new RegistryValueTarget(new RegLocation(hive, view, path), name),
                    Confidence = Confidence.Moderate,
                };
            }
        }
    }

    // ---- Fonts ------------------------------------------------------------

    private static readonly (RegistryHive Hive, RegistryView View, string Path)[] FontKeys =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"),
        (RegistryHive.CurrentUser, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"),
    ];

    public static IEnumerable<ResidueItem> Fonts(
        ResidueTokenSet tokens, CancellationToken cancellationToken)
    {
        foreach (var (hive, view, path) in FontKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var (name, data) in ReadValues(hive, view, path))
            {
                string? match = tokens.Match(name) ?? tokens.Match(Path.GetFileNameWithoutExtension(data ?? string.Empty));
                if (match is null)
                {
                    continue;
                }

                yield return new ResidueItem
                {
                    Kind = ResidueKind.Font,
                    Path = $@"{HiveName(hive)}\{path}\{name}",
                    DisplayName = name,
                    MatchedToken = match,
                    Reg = new RegLocation(hive, view, path),
                    ValueName = name,
                    Target = new RegistryValueTarget(new RegLocation(hive, view, path), name),
                    Confidence = Confidence.Moderate,
                };
            }
        }
    }

    // ---- Shortcuts ---------------------------------------------------------

    public static IEnumerable<ResidueItem> Shortcuts(
        ResidueTokenSet tokens, CancellationToken cancellationToken)
    {
        foreach (string root in ShortcutRoots())
        {
            foreach (string file in SafeEnumerateShortcuts(root, cancellationToken))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                string? match = tokens.Match(name);
                if (match is null)
                {
                    continue;
                }

                yield return new ResidueItem
                {
                    Kind = ResidueKind.Shortcut,
                    Path = file,
                    DisplayName = name,
                    MatchedToken = match,
                    Target = new FileTarget(file),
                    Confidence = Confidence.Moderate,
                };
            }
        }
    }

    private static IEnumerable<string> ShortcutRoots()
    {
        foreach (Environment.SpecialFolder folder in new[]
                 {
                     Environment.SpecialFolder.StartMenu,
                     Environment.SpecialFolder.CommonStartMenu,
                     Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.DesktopDirectory,
                 })
        {
            string path;
            try
            {
                path = Environment.GetFolderPath(folder);
            }
            catch
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    // ---- Helpers -----------------------------------------------------------

    private static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        _ => hive.ToString(),
    };

    private static IEnumerable<(string Name, string? Data)> ReadValues(
        RegistryHive hive, RegistryView view, string path)
    {
        RegistryKey? baseKey = null;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            baseKey = root.OpenSubKey(path, writable: false);
        }
        catch
        {
            yield break;
        }

        if (baseKey is null)
        {
            yield break;
        }

        using (baseKey)
        {
            string[] names;
            try
            {
                names = baseKey.GetValueNames();
            }
            catch
            {
                yield break;
            }

            foreach (string name in names)
            {
                string? data = null;
                try
                {
                    data = baseKey.GetValue(name)?.ToString();
                }
                catch
                {
                    // Unreadable value; the name is still worth reporting.
                }

                yield return (name, data);
            }
        }
    }
}
using Microsoft.Win32;

namespace Sweep;

/// <summary>What an installation changed, compared with the snapshot taken before it ran.</summary>
public sealed record TraceDiff(
    IReadOnlyList<InstalledApp> NewPrograms,
    IReadOnlyList<ResidueItem> Removable);

/// <summary>
/// A before/after picture of the machine around one installer.
///
/// This is the snapshot-diff approach, not a real-time monitor: the app captures the
/// uninstall entries, the first level of the program and user data folders, and the first two
/// levels of the software registry, runs the installer, then captures the same things again.
/// It cannot see an installer that writes and deletes within the same run, but it needs no
/// driver and cannot leave a hook behind.
/// </summary>
public sealed class TraceSnapshot
{
    private const int MaxFolders = 20_000;
    private const int MaxRegistryKeys = 60_000;
    private const int MaxResults = 500;
    private static readonly TimeSpan RegistryBudget = TimeSpan.FromSeconds(25);

    public Dictionary<string, InstalledApp> Programs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, RegLocation> RegistryKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Task<TraceSnapshot> CaptureAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => Capture(progress, cancellationToken), cancellationToken);

    private static TraceSnapshot Capture(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var snapshot = new TraceSnapshot();

        progress?.Report("Snapshotting installed programs...");
        foreach (InstalledApp app in RegistryScanner.Scan(cancellationToken))
        {
            snapshot.Programs[app.Location.ToString()] = app;
        }

        progress?.Report("Snapshotting program folders...");
        CaptureFolders(snapshot, cancellationToken);

        progress?.Report("Snapshotting the software registry...");
        CaptureRegistry(snapshot, cancellationToken);

        return snapshot;
    }

    private static IEnumerable<string> FolderRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (Native.UserProgramFiles is { Length: > 0 } userProgramFiles)
        {
            yield return userProgramFiles;
        }
    }

    private static void CaptureFolders(TraceSnapshot snapshot, CancellationToken cancellationToken)
    {
        foreach (string root in FolderRoots())
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (snapshot.Folders.Count >= MaxFolders)
                    {
                        return;
                    }

                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    snapshot.Folders.Add(directory);
                }
            }
            catch
            {
                // Unreadable root - keep the others.
            }
        }
    }

    private static void CaptureRegistry(TraceSnapshot snapshot, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + RegistryBudget;

        WalkRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE", snapshot, cancellationToken, deadline);
        WalkRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE", snapshot, cancellationToken, deadline);
        WalkRegistry(RegistryHive.CurrentUser, RegistryView.Registry64, "SOFTWARE", snapshot, cancellationToken, deadline);
    }

    private static void WalkRegistry(
        RegistryHive hive,
        RegistryView view,
        string rootPath,
        TraceSnapshot snapshot,
        CancellationToken cancellationToken,
        DateTime deadline)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(rootPath, writable: false);
            if (root is null)
            {
                return;
            }

            Walk(root, hive, view, 0, snapshot, cancellationToken, deadline);
        }
        catch
        {
            // A denied hive is not worth failing the trace over.
        }
    }

    private static void Walk(
        RegistryKey key,
        RegistryHive hive,
        RegistryView view,
        int depth,
        TraceSnapshot snapshot,
        CancellationToken cancellationToken,
        DateTime deadline)
    {
        if (depth >= 2 ||
            snapshot.RegistryKeys.Count >= MaxRegistryKeys ||
            DateTime.UtcNow > deadline)
        {
            return;
        }

        string[] names;
        try
        {
            names = key.GetSubKeyNames();
        }
        catch
        {
            return;
        }

        foreach (string name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using RegistryKey? child = key.OpenSubKey(name, writable: false);
                if (child is null)
                {
                    continue;
                }

                string subKey = SubKeyOf(child.Name, hive);
                var location = new RegLocation(hive, view, subKey);
                snapshot.RegistryKeys[location.ToString()] = location;

                Walk(child, hive, view, depth + 1, snapshot, cancellationToken, deadline);
            }
            catch
            {
                // Key vanished or is unreadable.
            }
        }
    }

    private static string SubKeyOf(string fullName, RegistryHive hive)
    {
        string prefix = hive switch
        {
            RegistryHive.LocalMachine => @"HKEY_LOCAL_MACHINE\",
            RegistryHive.CurrentUser => @"HKEY_CURRENT_USER\",
            _ => string.Empty,
        };

        return prefix.Length > 0 && fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fullName[prefix.Length..]
            : fullName;
    }

    /// <summary>Everything present now that was not in <paramref name="before"/>.</summary>
    public TraceDiff Compare(TraceSnapshot before)
    {
        var newPrograms = Programs
            .Where(pair => !before.Programs.ContainsKey(pair.Key))
            .Select(pair => pair.Value)
            .OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var items = new List<ResidueItem>();

        foreach (string folder in Prune(Folders.Where(path => !before.Folders.Contains(path))))
        {
            if (Safety.IsProtectedLocation(folder) || items.Count >= MaxResults)
            {
                continue;
            }

            long size = 0;
            try
            {
                size = SizeCalculator.GetDirectorySize(folder, TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Size is cosmetic.
            }

            items.Add(new ResidueItem
            {
                Kind = ResidueKind.Folder,
                Path = folder,
                DisplayName = Path.GetFileName(folder),
                SizeBytes = size,
                MatchedToken = "new folder",
                Target = new FileTarget(folder),
                Confidence = Confidence.Safe,
                Note = "Created while the traced installer ran.",
            });
        }

        var keptKeys = new List<RegLocation>();

        foreach (RegLocation location in RegistryKeys
            .Where(pair => !before.RegistryKeys.ContainsKey(pair.Key))
            .Select(pair => pair.Value)
            .OrderBy(location => location.SubKey.Length))
        {
            // A new parent key already covers everything beneath it.
            if (keptKeys.Any(parent =>
                    location.SubKey.StartsWith(parent.SubKey + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (items.Count >= MaxResults || Safety.IsProtectedRegistryKey(location))
            {
                continue;
            }

            keptKeys.Add(location);

            items.Add(new ResidueItem
            {
                Kind = ResidueKind.RegistryKey,
                Path = location.SubKey,
                DisplayName = location.SubKey.Split('\\').Last(),
                MatchedToken = "new registry key",
                Reg = location,
                Target = new RegistryKeyTarget(location),
                Confidence = Confidence.Moderate,
                Note = "Created while the traced installer ran.",
            });
        }

        return new TraceDiff(newPrograms, items);
    }

    /// <summary>Drops children of a path that is already in the list.</summary>
    private static List<string> Prune(IEnumerable<string> paths)
    {
        var kept = new List<string>();

        foreach (string path in paths.OrderBy(p => p.Length))
        {
            string separator = path.Contains('\\') ? "\\" : string.Empty;
            if (kept.Any(parent =>
                    path.StartsWith(parent + separator, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            kept.Add(path);
        }

        return kept;
    }
}

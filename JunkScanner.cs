namespace Sweep;

/// <summary>One category of disposable files, with its measured size.</summary>
public sealed class JunkGroup
{
    public required string Name { get; init; }

    /// <summary>Folder whose contents (or matching files) are disposable.</summary>
    public required string Path { get; init; }

    public required string Detail { get; init; }

    /// <summary>File pattern for caches that live beside files worth keeping.</summary>
    public string Pattern { get; init; } = "*";

    public bool Recursive { get; init; } = true;

    public bool NeedsElevation { get; init; }

    public long Bytes { get; internal set; }

    public int Files { get; internal set; }

    public bool Exists { get; internal set; }

    public string SizeDisplay => SizeCalculator.Format(Bytes);

    public string LocationDisplay => Exists ? Path : "not present";
}

public sealed record JunkCleanResult(int Deleted, long Bytes, int Failed, IReadOnlyList<string> Errors);

/// <summary>
/// Finds disposable files in a fixed, hand-written list of locations.
///
/// Deliberately not name-based or heuristic: junk cleaning is the one feature where a clever
/// matcher would eventually delete something that mattered. Every location here is one
/// Windows or a browser recreates on demand, and the folder roots themselves are never
/// removed - only their contents.
/// </summary>
public static class JunkScanner
{
    private const int MaxFilesPerGroup = 100_000;
    private static readonly TimeSpan MeasureTime = TimeSpan.FromSeconds(4);

    public static IReadOnlyList<JunkGroup> Locations()
    {
        string local = UserFolders.LocalAppData;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string temp = UserFolders.Temp;

        var locations = new List<JunkGroup>();

        if (temp.Length > 0)
        {
            locations.Add(new JunkGroup
            {
                Name = "User temporary files",
                Path = temp,
                Detail = "Files applications left in this user's temp folder.",
            });
        }

        locations.Add(new JunkGroup
        {
            Name = "Windows temporary files",
            Path = Path.Combine(windows, "Temp"),
            Detail = "Machine-wide temp folder. Needs administrator rights.",
            NeedsElevation = true,
        });

        // Everything below is under the invoker's LocalAppData. With no profile to use (a
        // different admin at the UAC prompt and no profile path passed) there is nothing
        // per-user to measure, and Path.Combine("", ...) must not become a relative path.
        if (local.Length > 0)
        {
            locations.Add(new JunkGroup
            {
                Name = "Crash dumps",
                Path = Path.Combine(local, "CrashDumps"),
                Detail = "Dumps written when an application crashed.",
            });

            locations.Add(new JunkGroup
            {
                Name = "Legacy internet cache",
                Path = Path.Combine(local, "Microsoft", "Windows", "INetCache"),
                Detail = "Cache used by Windows components and older browsers.",
            });

            locations.Add(new JunkGroup
            {
                Name = "Direct3D shader cache",
                Path = Path.Combine(local, "D3DSCache"),
                Detail = "Compiled shader blobs, rebuilt the next time a game runs.",
            });

            locations.Add(new JunkGroup
            {
                Name = "Thumbnail cache",
                Path = Path.Combine(local, "Microsoft", "Windows", "Explorer"),
                Pattern = "thumbcache_*.db",
                Recursive = false,
                Detail = "Explorer thumbnail databases. Windows rebuilds them as needed.",
            });
        }

        return locations;
    }

    public static IReadOnlyList<JunkGroup> Scan(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var groups = Locations().ToList();

        foreach (JunkGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(group.Name);
            Measure(group, cancellationToken);
        }

        return groups;
    }

    private static void Measure(JunkGroup group, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(group.Path))
            {
                return;
            }

            group.Exists = true;
            var deadline = DateTime.UtcNow + MeasureTime;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = group.Recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MaxRecursionDepth = 8,
            };

            foreach (string file in Directory.EnumerateFiles(group.Path, group.Pattern, options))
            {
                if (cancellationToken.IsCancellationRequested ||
                    DateTime.UtcNow > deadline ||
                    group.Files >= MaxFilesPerGroup)
                {
                    break;
                }

                try
                {
                    group.Bytes += new FileInfo(file).Length;
                    group.Files++;
                }
                catch
                {
                    // Vanished or locked between enumeration and stat.
                }
            }
        }
        catch
        {
            // Unreadable location - reported as empty rather than failing the whole scan.
        }
    }

    /// <summary>
    /// Deletes the contents of the selected groups. The folder itself is kept: Windows and
    /// the applications expect their temp and cache directories to exist.
    /// </summary>
    public static JunkCleanResult Clean(
        IEnumerable<JunkGroup> groups,
        CancellationToken cancellationToken = default)
    {
        int deleted = 0;
        int failed = 0;
        long bytes = 0;
        var errors = new List<string>();

        foreach (JunkGroup group in groups)
        {
            // Check the disk rather than the scan's cached Exists flag: content can appear
            // between measuring and cleaning, and a caller may hand over a freshly built group.
            if (!Directory.Exists(group.Path))
            {
                continue;
            }

            foreach (string entry in Entries(group))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new JunkCleanResult(deleted, bytes, failed, errors);
                }

                try
                {
                    // A junction inside temp could point anywhere; deleting through it would
                    // reach outside the group. Leave reparse points alone.
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                bool isDirectory;
                try
                {
                    isDirectory = Directory.Exists(entry);
                }
                catch
                {
                    continue;
                }

                long size = MeasureEntry(entry, isDirectory);
                DeletionResult result = isDirectory
                    ? FileOps.DeleteDirectory(entry)
                    : FileOps.DeleteFile(entry);

                if (result.Success || result.ScheduledForReboot)
                {
                    deleted++;
                    bytes += size;
                }
                else
                {
                    failed++;
                    if (errors.Count < 20)
                    {
                        errors.Add($"{entry}: {result.Error}");
                    }
                }
            }
        }

        return new JunkCleanResult(deleted, bytes, failed, errors);
    }

    private static IEnumerable<string> Entries(JunkGroup group)
    {
        if (!group.Recursive || group.Pattern != "*")
        {
            return Directory.EnumerateFiles(group.Path, group.Pattern, SearchOption.TopDirectoryOnly);
        }

        return Directory.EnumerateFileSystemEntries(group.Path, "*", SearchOption.TopDirectoryOnly);
    }

    private static long MeasureEntry(string path, bool isDirectory)
    {
        try
        {
            return isDirectory
                ? SizeCalculator.GetDirectorySize(path, TimeSpan.FromSeconds(1))
                : new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}

namespace Sweep;

/// <summary>One browser profile's history files, or the Windows recent-items list.</summary>
public sealed class HistoryGroup
{
    public required string Name { get; init; }

    public required string Detail { get; init; }

    public List<string> Files { get; } = [];

    public List<string> Directories { get; } = [];

    public long Bytes { get; set; }

    public int Items => Files.Count + Directories.Count;

    public string SizeDisplay => SizeCalculator.Format(Bytes);
}

public sealed record HistoryCleanResult(int Removed, int Failed, IReadOnlyList<string> Errors);

/// <summary>
/// Finds the files that hold browsing history and the Windows recent-items list.
///
/// Deliberately file-based: reading or editing a SQLite history store needs a database
/// engine, and the safe fallback is to move the whole store aside. Cookies, passwords and
/// bookmarks are separate files and are never touched. Firefox keeps history and bookmarks in
/// the same database (places.sqlite), so it is left alone entirely rather than risk the
/// bookmarks.
/// </summary>
public static class HistoryCleaner
{
    /// <summary>
    /// Chromium's history lives across these files. Favicons and the cookie store are
    /// separate and intentionally absent.
    /// </summary>
    private static readonly string[] ChromiumHistoryFiles =
    [
        "History",
        "History-journal",
        "Visited Links",
        "Top Sites",
        "Shortcuts",
        "Media History",
        "Network Action Predictor",
    ];

    public static IReadOnlyList<HistoryGroup> Scan(CancellationToken cancellationToken = default)
    {
        var groups = new List<HistoryGroup>();

        foreach ((string browser, string profile, string profilePath) in BrowserExtensions.ChromiumProfiles())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var files = new List<string>();
            foreach (string name in ChromiumHistoryFiles)
            {
                string file = Path.Combine(profilePath, name);
                if (File.Exists(file))
                {
                    files.Add(file);
                }
            }

            if (files.Count == 0)
            {
                continue;
            }

            var group = new HistoryGroup
            {
                Name = $"{browser} - {profile}",
                Detail = "History, visited links, top sites, shortcuts and media history. " +
                         "Cookies, passwords and bookmarks are separate files and stay.",
            };

            group.Files.AddRange(files);
            group.Bytes = files.Sum(SizeOf);
            groups.Add(group);
        }

        AddRecentItems(groups, cancellationToken);
        return groups;
    }

    private static void AddRecentItems(List<HistoryGroup> groups, CancellationToken cancellationToken)
    {
        string recent = UserFolders.Recent;
        if (recent.Length == 0 || !Directory.Exists(recent))
        {
            return;
        }

        try
        {
            var files = Directory
                .EnumerateFiles(recent, "*.lnk", SearchOption.TopDirectoryOnly)
                .Take(5000)
                .ToList();

            var directories = new List<string>();
            foreach (string name in new[] { "AutomaticDestinations", "CustomDestinations" })
            {
                string path = Path.Combine(recent, name);
                if (Directory.Exists(path))
                {
                    directories.Add(path);
                }
            }

            if (files.Count == 0 && directories.Count == 0)
            {
                return;
            }

            var group = new HistoryGroup
            {
                Name = "Windows recent items",
                Detail = $"{files.Count} shortcut(s) and the jump lists Windows keeps for " +
                         "recently used files and folders.",
            };

            group.Files.AddRange(files);
            group.Directories.AddRange(directories);
            group.Bytes = files.Sum(SizeOf) + directories.Sum(SizeOfDirectory);
            groups.Add(group);
        }
        catch
        {
            // An unreadable Recent folder simply is not offered.
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Browsers that must be closed before their history files can be moved.</summary>
    public static IReadOnlyList<string> RunningBrowsers()
    {
        var running = new List<string>();

        foreach (string browser in new[] { "Chrome", "Edge", "Brave", "Vivaldi" })
        {
            string? process = BrowserExtensions.RunningProcess(browser);
            if (process is not null)
            {
                running.Add($"{browser} ({process})");
            }
        }

        return running;
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static long SizeOfDirectory(string path) =>
        SizeCalculator.GetDirectorySize(path, TimeSpan.FromSeconds(2));
}

using System.Diagnostics;

namespace Sweep;

/// <summary>
/// Fills in the metadata the uninstall registry frequently does not carry.
///
/// Most uninstall keys simply have no EstimatedSize, no Publisher and often no InstallDate,
/// which is why a naive reader shows a column of dashes. The executable itself almost always
/// knows: its version resource carries the company, product version and description. When
/// the registry is silent, the binary answers instead.
/// </summary>
public static class Metadata
{
    private static readonly Dictionary<string, FileVersionInfo?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static FileVersionInfo? ForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out FileVersionInfo? cached))
            {
                return cached;
            }

            FileVersionInfo? info = null;
            try
            {
                if (File.Exists(path))
                {
                    info = FileVersionInfo.GetVersionInfo(path);
                }
            }
            catch
            {
                // Locked or unreadable binary.
            }

            Cache[path] = info;
            return info;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }

    /// <summary>Publisher, falling back to the binary's company then product name.</summary>
    public static string CompanyFor(InstalledApp app, FileVersionInfo? info)
    {
        if (!string.IsNullOrWhiteSpace(app.Publisher))
        {
            return app.Publisher.Trim();
        }

        return info is null
            ? string.Empty
            : FirstNonEmpty(info.CompanyName, info.ProductName);
    }

    public static string VersionFor(InstalledApp app, FileVersionInfo? info)
    {
        if (!string.IsNullOrWhiteSpace(app.DisplayVersion))
        {
            return app.DisplayVersion.Trim();
        }

        // ProductVersion is what a user recognises; FileVersion often has four build parts.
        return info is null ? string.Empty : FirstNonEmpty(info.ProductVersion, info.FileVersion);
    }

    public static string DescriptionFor(InstalledApp app, FileVersionInfo? info)
    {
        if (!string.IsNullOrWhiteSpace(app.Comments))
        {
            return app.Comments.Trim();
        }

        return info is null ? string.Empty : FirstNonEmpty(info.FileDescription, info.Comments);
    }

    /// <summary>
    /// Install date from the install folder's creation time.
    ///
    /// Not the same as the install date, but for a directory that ships with the product it
    /// is close, and it beats a blank column. Probes the filesystem, so callers keep it off
    /// the UI thread.
    /// </summary>
    public static DateTime? FolderCreationDate(InstalledApp app)
    {
        foreach (string folder in CandidateFolders(app))
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    return Directory.GetCreationTime(folder);
                }
            }
            catch
            {
                // Inaccessible folder.
            }
        }

        return null;
    }

    /// <summary>
    /// Size on disk for the install folder, used when the registry has no estimate.
    ///
    /// This is the expensive one: it walks the folder. It is therefore capped and only runs
    /// for rows that actually need it.
    /// </summary>
    public static long SizeFor(InstalledApp app, TimeSpan cap, CancellationToken cancellationToken = default)
    {
        if (app.EstimatedSizeBytes > 0)
        {
            return app.EstimatedSizeBytes;
        }

        long total = 0;
        bool foundAny = false;

        foreach (string folder in CandidateFolders(app))
        {
            cancellationToken.ThrowIfCancellationRequested();

            long size = SizeCalculator.GetDirectorySize(folder, cap, cancellationToken);
            if (size > 0)
            {
                total += size;
                foundAny = true;

                // An install folder contains the product; walking its subfolders as separate
                // candidates would double count.
                break;
            }
        }

        if (foundAny)
        {
            return total;
        }

        // No usable folder. For an app installed outside a known folder, fall back to the
        // size of the main executable so the column is not blank.
        string? exe = app.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(exe))
        {
            try
            {
                var info = new FileInfo(exe);
                if (info.Exists)
                {
                    return info.Length;
                }
            }
            catch
            {
                // ignore
            }
        }

        return 0;
    }

    private static IEnumerable<string> CandidateFolders(InstalledApp app)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            string location = app.InstallLocation.Trim().Trim('"');
            if (seen.Add(location))
            {
                yield return location;
            }
        }

        string? exe = app.ExecutablePath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            yield break;
        }

        string parent;
        try
        {
            parent = Path.GetDirectoryName(exe) ?? string.Empty;
        }
        catch
        {
            yield break;
        }

        if (parent.Length > 0 && seen.Add(parent))
        {
            yield return parent;
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
}
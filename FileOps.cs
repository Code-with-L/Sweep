using System.Text.Json;

namespace Sweep;

internal static class PathUtil
{
    /// <summary>Applies the \\?\ long-path prefix where required.</summary>
    public static string Long(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.Length >= 240 ? @"\\?\" + path : path;
    }

    public static string? SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string result = new string(chars).Trim();
        result = string.Join("_", result.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (result.Length > 60)
        {
            result = result[..60];
        }

        return result.Length == 0 ? "app" : result;
    }
}

internal static class SizeCalculator
{
    /// <summary>
    /// Directory size with a time cap. A wrong number shown quickly is better than a right
    /// number that freezes the UI for two minutes.
    /// </summary>
    public static long GetDirectorySize(string path, TimeSpan cap, CancellationToken cancellationToken = default)
    {
        long total = 0;
        var deadline = DateTime.UtcNow + cap;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 12,
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(PathUtil.Long(path), "*", options))
            {
                if (DateTime.UtcNow > deadline || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // Locked or vanished - skip.
                }
            }
        }
        catch
        {
            return total;
        }

        return total;
    }

    public static string Format(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
    };
}

public sealed record DeletionResult(bool Success, string Path, bool ScheduledForReboot, string? Error);

/// <summary>Filesystem and registry deletion with long-path, read-only and reboot-pending handling.</summary>
public static class FileOps
{
    public static DeletionResult DeleteDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(PathUtil.Long(path));
            if (!info.Exists)
            {
                return new DeletionResult(true, path, false, null);
            }

            ClearReadOnly(info);
            info.Delete(recursive: true);
            return new DeletionResult(true, path, false, null);
        }
        catch (Exception ex) when (IsSharingViolation(ex))
        {
            bool scheduled = Native.ScheduleDeleteOnReboot(PathUtil.Long(path));
            return new DeletionResult(false, path, scheduled, scheduled ? null : ex.Message);
        }
        catch (Exception ex)
        {
            return new DeletionResult(false, path, false, ex.Message);
        }
    }

    public static DeletionResult DeleteFile(string path)
    {
        try
        {
            var info = new FileInfo(PathUtil.Long(path));
            if (!info.Exists)
            {
                return new DeletionResult(true, path, false, null);
            }

            info.IsReadOnly = false;
            info.Delete();
            return new DeletionResult(true, path, false, null);
        }
        catch (Exception ex) when (IsSharingViolation(ex))
        {
            bool scheduled = Native.ScheduleDeleteOnReboot(PathUtil.Long(path));
            return new DeletionResult(false, path, scheduled, scheduled ? null : ex.Message);
        }
        catch (Exception ex)
        {
            return new DeletionResult(false, path, false, ex.Message);
        }
    }

    private static bool IsSharingViolation(Exception ex) =>
        // Only a genuine lock justifies deferring to reboot. An access-denied error will
        // simply fail again after a restart, and hiding that behind a "scheduled" result
        // would misreport what happened.
        ex is IOException;

    private static void ClearReadOnly(DirectoryInfo info)
    {
        try
        {
            info.Attributes = FileAttributes.Normal;
        }
        catch
        {
            // Attribute resets fail on some protected directories; the delete below will report it.
        }

        try
        {
            foreach (var file in info.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    file.Attributes = FileAttributes.Normal;
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }
    }
}

/// <summary>
/// Moves deleted items aside instead of destroying them, with a manifest so the operation
/// can be undone. This is the default because uninstall residue removal is destructive and
/// irreversible if the heuristic was wrong.
/// </summary>
public sealed class Quarantine
{
    private sealed record Entry(string Original, string Stored, bool IsDirectory, long Size);

    private readonly List<Entry> _entries = [];

    public string Root { get; }

    public string ManifestPath => Path.Combine(Root, "manifest.json");

    public Quarantine(string appName)
    {
        string name = PathUtil.SafeFileName(appName) ?? "app";
        string baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Sweep",
            "Quarantine");

        if (!CanWrite(Path.GetDirectoryName(baseDir)!))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sweep",
                "Quarantine");
        }

        baseDir = Path.Combine(baseDir, name);

        // Two apps sharing a name prefix, quarantined in the same second, must not land in
        // the same directory - the second SaveManifest would overwrite the first session's
        // undo record.
        Root = baseDir + $"_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        Directory.CreateDirectory(Root);
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, $".sweep-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public DeletionResult QuarantineDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return new DeletionResult(true, path, false, null);
        }

        string destination = Path.Combine(Root, Path.GetFileName(path.TrimEnd('\\')));
        var result = Move(path, destination, isDirectory: true);
        if (result.Success)
        {
            _entries.Add(new Entry(path, destination, true, 0));
        }

        return result;
    }

    public DeletionResult QuarantineFile(string path)
    {
        if (!File.Exists(path))
        {
            return new DeletionResult(true, path, false, null);
        }

        string destination = Path.Combine(Root, Path.GetFileName(path));
        var result = Move(path, destination, isDirectory: false);
        if (result.Success)
        {
            long size = 0;
            try
            {
                size = new FileInfo(destination).Length;
            }
            catch
            {
                // ignore
            }

            _entries.Add(new Entry(path, destination, false, size));
        }

        return result;
    }

    private static DeletionResult Move(string source, string destination, bool isDirectory)
    {
        string unique = destination;
        int n = 1;
        while (isDirectory ? Directory.Exists(unique) : File.Exists(unique))
        {
            string suffix = $" ({n++})";
            unique = destination + suffix;
        }

        try
        {
            if (isDirectory)
            {
                Directory.Move(source, unique);
            }
            else
            {
                File.Move(source, unique);
            }

            return new DeletionResult(true, source, false, null);
        }
        catch (IOException)
        {
            // Almost always a cross-volume move; fall back to copy + delete.
            try
            {
                if (isDirectory)
                {
                    CopyDirectory(source, unique);
                }
                else
                {
                    File.Copy(source, unique, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                return new DeletionResult(false, source, false, ex.Message);
            }

            // The copy succeeded, so the item must now be recorded even though the
            // original still exists. Reporting success here without a manifest entry
            // would leave an unrestorable duplicate behind.
            DeletionResult removed = isDirectory
                ? FileOps.DeleteDirectory(source)
                : FileOps.DeleteFile(source);

            if (!removed.Success && !removed.ScheduledForReboot)
            {
                TryDeleteCopy(unique, isDirectory);
                return new DeletionResult(false, source, false,
                    $"{removed.Error} (a copy was left at {unique})");
            }

            return new DeletionResult(removed.Success, source, removed.ScheduledForReboot, null);
        }
        catch (Exception ex)
        {
            return new DeletionResult(false, source, false, ex.Message);
        }
    }

    private static void TryDeleteCopy(string copy, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                Directory.Delete(copy, recursive: true);
            }
            else
            {
                File.Delete(copy);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        Directory.CreateDirectory(destination);

        // Preserve the shape of the tree. Flattening by file name made two same-named files
        // in different subfolders overwrite each other, and the manifest then described a
        // restore that could not put the original content back.
        string root = Path.GetFullPath(source).TrimEnd('\\');

        foreach (string dir in Directory.EnumerateDirectories(source, "*", options))
        {
            Directory.CreateDirectory(
                Path.Combine(destination, Path.GetRelativePath(root, dir)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", options))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Never overwrite: a collision means the copy is incomplete, not that one file
            // should win silently.
            File.Copy(file, target, overwrite: false);

            var info = new FileInfo(target);
            if (info.IsReadOnly)
            {
                info.IsReadOnly = false;
            }
        }
    }

    public bool SaveManifest(out string error)
    {
        try
        {
            File.WriteAllText(
                ManifestPath,
                JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            // Reported to the user: without a manifest these items cannot be restored.
            error = ex.Message;
            LogStore.Error($"Could not write quarantine manifest: {ex.Message}");
            return false;
        }
    }

    public void SaveManifest() => SaveManifest(out _);

    public int Count => _entries.Count;

    /// <summary>The two places a quarantine session may live.</summary>
    private static IReadOnlyList<string> BaseDirectories() =>
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Sweep", "Quarantine"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sweep", "Quarantine"),
    ];

    /// <summary>
    /// True when the session really sits under one of Sweep's quarantine roots.
    ///
    /// A manifest is untrusted input: without this check, a hand-written manifest anywhere on
    /// disk turns "Restore" into an arbitrary file move with the Administrator token.
    /// </summary>
    public static bool IsTrustedSession(string quarantineRoot)
    {
        try
        {
            string full = Path.GetFullPath(quarantineRoot).TrimEnd('\\');
            foreach (string baseDir in BaseDirectories())
            {
                string root = Path.GetFullPath(baseDir).TrimEnd('\\');
                if (full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) &&
                    full.Length > root.Length + 1)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Malformed path.
        }

        return false;
    }

    /// <summary>Reverses a previous quarantine operation.</summary>
    public static int RestoreFrom(string quarantineRoot)
    {
        if (!IsTrustedSession(quarantineRoot))
        {
            throw new InvalidOperationException(
                "That folder is not a Sweep quarantine session, so it will not be restored.");
        }

        string root = Path.GetFullPath(quarantineRoot).TrimEnd('\\');
        string manifest = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifest))
        {
            throw new FileNotFoundException("No quarantine manifest found.", manifest);
        }

        var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(manifest)) ?? [];
        int restored = 0;

        foreach (var entry in entries)
        {
            try
            {
                // Both halves of the move must be legitimate: the stored copy has to live
                // inside this session, and the destination must not be a protected location.
                string stored = Path.GetFullPath(entry.Stored);
                if (!stored.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    LogStore.Warn($"Ignored quarantine entry pointing outside the session: {entry.Stored}");
                    continue;
                }

                if (Safety.IsProtectedLocation(entry.Original))
                {
                    LogStore.Warn($"Ignored quarantine entry targeting a protected location: {entry.Original}");
                    continue;
                }

                if (!Directory.Exists(entry.Original) && !File.Exists(entry.Original))
                {
                    if (entry.IsDirectory)
                    {
                        Directory.Move(stored, entry.Original);
                    }
                    else
                    {
                        File.Move(stored, entry.Original);
                    }

                    restored++;
                }
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Could not restore {entry.Original}: {ex.Message}");
            }
        }

        return restored;
    }

    public static int CountEntries(string quarantineRoot)
    {
        try
        {
            string manifest = Path.Combine(quarantineRoot, "manifest.json");
            if (!File.Exists(manifest))
            {
                return 0;
            }

            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(manifest))?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The first quarantine root that currently exists, for "open folder".</summary>
    public static string? ExistingBaseDirectory =>
        BaseDirectories().FirstOrDefault(Directory.Exists);

    public static IReadOnlyList<string> ListQuarantineRoots()
    {
        var roots = new List<string>();

        foreach (string baseDir in BaseDirectories())
        {
            if (!Directory.Exists(baseDir))
            {
                continue;
            }

            try
            {
                roots.AddRange(Directory.EnumerateDirectories(baseDir));
            }
            catch
            {
                // Unreadable root - skip it rather than losing the other one.
            }
        }

        return roots.OrderByDescending(d => d).ToList();
    }
}
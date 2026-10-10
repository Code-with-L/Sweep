using Microsoft.Win32;

namespace Sweep;

public enum ResidueKind
{
    Folder,
    File,
    RegistryKey,
    RegistryValue,

    StartupEntry,
    Service,
    ScheduledTask,
    Shortcut,
    EnvironmentVariable,
    Font,
}

public sealed class ResidueItem
{
    public required ResidueKind Kind { get; init; }

    /// <summary>Full filesystem path, or the registry key path for registry items.</summary>
    public required string Path { get; init; }

    public required string DisplayName { get; init; }

    public long SizeBytes { get; set; }

    /// <summary>The token that caused this match, shown in the UI so the user can judge it.</summary>
    public string MatchedToken { get; set; } = "";

    public RegLocation Reg { get; init; }

    public string? ValueName { get; init; }

    /// <summary>How this item must be removed. Null means "not removable".</summary>
    public RemovalTarget? Target { get; init; }

    public Confidence Confidence { get; init; } = Confidence.Safe;

    /// <summary>Extra context, e.g. an orphaned service or the task's action line.</summary>
    public string? Note { get; init; }

    public string SizeDisplay => SizeCalculator.Format(SizeBytes);

    public bool SelectedByDefault => Confidence != Confidence.Risky;

    public override string ToString() => Path;
}

public sealed record ScanProgress(string Stage, string Detail, int Matches, int Percent);

public sealed record ScanOptions
{
    public int FileDepth { get; init; } = 3;
    public int FileSeconds { get; init; } = 120;
    public int MaxFileMatches { get; init; } = 2000;
    public int MaxRegistryMatches { get; init; } = 300;
    public int RegistryDepth { get; init; } = 3;
    public bool ScanRegistry { get; init; } = true;
    public bool UsePublisherTokens { get; init; }

    public static ScanOptions FromSettings(AppSettings s) => new()
    {
        FileDepth = s.FileScanDepth,
        FileSeconds = s.FileScanSeconds,
        MaxFileMatches = s.MaxFileMatches,
        MaxRegistryMatches = s.MaxRegistryMatches,
        RegistryDepth = s.RegistryScanDepth,
        ScanRegistry = s.ScanRegistry,
        UsePublisherTokens = s.UsePublisherTokens,
    };
}

/// <summary>
/// Finds files, folders and registry entries left behind after an uninstall.
///
/// Design constraints, in priority order:
///  1. Never suggest deleting something that belongs to Windows or to the MSI repair cache.
///  2. Prune aggressively - a matched folder is reported once, never expanded.
///  3. Stay within hard time and match caps so the UI never appears hung.
/// </summary>
public sealed class ResidueScanner
{
    private sealed record ScanRoot(string Path, int Depth, bool AllowWeak, string Label);

    /// <summary>Names that are never residue, whatever they match.</summary>
    private static readonly HashSet<string> ExcludedLeafNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "installer", "package cache", "windowsinstaller", "temp", "tempstate",
        "windowsapps", "winsxs", "assembly", "gac", "gac_msil",
    };

    /// <summary>Registry segments that are pure noise and enormous when walked.</summary>
    private static readonly HashSet<string> ExcludedRegSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "clsid", "typelib", "interface", "appid", "installer",
        "wow6432node", "component categories", "assembly", "gac", "gac_msil",
    };

    private readonly ResidueTokenSet _tokens;
    private readonly ScanOptions _options;
    private readonly List<ResidueItem> _results = [];
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _protected = [];
    private readonly object _sync = new();
    private int _fileMatchCount;
    private int _registryMatchCount;
    private readonly List<string> _warnings = [];
    private DateTime _registryDeadline;

    /// <summary>Non-fatal problems that the UI must surface instead of only logging.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Hard ceiling on registry keys visited, so a broad token cannot run for hours.</summary>
    private const int MaxRegistryKeysVisited = 250_000;

    private int _registryKeysVisited;
    private long _sizeBudgetDeadline;
    private DateTime _deadline;
    private bool _capped;
    private IProgress<ScanProgress>? _progress;
    private CancellationToken _ct;

    public ResidueScanner(InstalledApp app, ScanOptions options)
    {
        _tokens = NameTokens.BuildTokenSet(app);
        _options = options;
        _app = app;
    }

    private readonly InstalledApp _app;

    private static IReadOnlyList<ScanRoot> FileRoots()
    {
        var roots = new List<ScanRoot>();

        void Add(Func<string?> getter, int depth, bool weak, string label)
        {
            try
            {
                string? path = getter();
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    roots.Add(new ScanRoot(path, depth, weak, label));
                }
            }
            catch
            {
                // Unavailable on this configuration.
            }
        }

        Add(() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 3, true, "Local AppData");
        Add(() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 3, true, "Roaming AppData");
        Add(() => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), 3, true, "ProgramData");
        Add(() => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), 2, true, "Program Files");
        Add(() => Native.ProgramFilesX86, 2, true, "Program Files (x86)");
        Add(() => Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), 1, true, "Common Files");
        Add(() => Native.UserProgramFiles, 2, true, "User Program Files");

        // The user profile root is deliberately NOT scanned. Developers keep repositories and
        // scripts named after tools under their home directory, and matching those would
        // offer to delete the user's own source code.

        return roots;
    }

    /// <summary>
    /// Directories owned by Windows or by other applications' sandboxing. Matching inside
    /// these produces matches we must never offer to delete.
    /// </summary>
    private static List<string> ProtectedPrefixes()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        return
        [
            Path.Combine(local, "Packages"),
            Path.Combine(local, "Microsoft", "Windows"),
            Path.Combine(local, "Microsoft", "OneDrive"),
            Path.Combine(local, "Microsoft", "EdgeUser Data"),
            Path.Combine(local, "Temp"),
            Path.Combine(local, "CrashDumps"),
            Path.Combine(local, "D3DSCache"),
            Path.Combine(common, "Microsoft"),
            Path.Combine(common, "Package Cache"),
            Path.Combine(common, "Windows Defender"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
        ];
    }

    private bool IsProtected(string path)
    {
        if (_protected.Count == 0)
        {
            _protected.AddRange(ProtectedPrefixes());
        }

        foreach (string prefix in _protected)
        {
            if (prefix.Length > 0 &&
                path.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public Task<List<ResidueItem>> ScanAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        _progress = progress;
        _ct = cancellationToken;
        return Task.Run(() => Scan(progress, cancellationToken), cancellationToken);
    }

private List<ResidueItem> Scan(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        _results.Clear();
        _seen.Clear();
        _capped = false;
        _warnings.Clear();
        _fileMatchCount = 0;
        _registryMatchCount = 0;
        _ct = cancellationToken;

        if (_tokens.IsEmpty)
        {
            LogStore.Warn($"No usable search tokens could be derived from '{_app.DisplayName}'; residue scan skipped.");
            return _results;
        }

        _deadline = DateTime.UtcNow + TimeSpan.FromSeconds(_options.FileSeconds);
        _registryDeadline = _deadline + TimeSpan.FromSeconds(Math.Max(15, _options.FileSeconds / 2));

        ScanFiles(progress, cancellationToken);

        // The registry phase must never be skipped because the file phase hit a cap.
        // A truncated file scan is a partial result; skipping the registry entirely
        // would silently hide every registry leftover.
        if (_options.ScanRegistry)
        {
            try
            {
                ScanRegistry(progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogStore.Error("Registry residue scan failed", ex);
                _warnings.Add("The registry scan failed; registry leftovers are missing from this list.");
            }
        }

        ScanArtifacts(progress, cancellationToken);

        // The original install folder, if it somehow survived, is always worth offering.
        AddSurvivingInstallFolder();

        return _results;
    }

    private void ScanArtifacts(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_options.ScanRegistry)
        {
            return;
        }

        bool allowWeak = _options.UsePublisherTokens;
        progress?.Report(new ScanProgress("System", "Checking startup entries", _results.Count, 60));

        Collect(() => SystemArtifacts.StartupEntries(_tokens, allowWeak, cancellationToken), "startup entries");
        Collect(() => SystemArtifacts.Services(_tokens, allowWeak, cancellationToken), "services");
        Collect(() => SystemArtifacts.ScheduledTasks(_tokens, allowWeak, cancellationToken), "scheduled tasks");
        Collect(() => SystemArtifacts.Shortcuts(_tokens, cancellationToken), "shortcuts");
        Collect(() => SystemArtifacts.EnvironmentVariables(_tokens, cancellationToken), "environment variables");
        Collect(() => SystemArtifacts.Fonts(_tokens, cancellationToken), "fonts");
    }

    /// <summary>
    /// Adds items from a targeted scanner. Failures are surfaced as warnings rather than
    /// aborting the whole scan, and counted against the registry match cap.
    /// </summary>
    private void Collect(Func<IEnumerable<ResidueItem>> produce, string label)
    {
        try
        {
            foreach (var item in produce())
            {
                if (_capped)
                {
                    return;
                }

                if (_seen.Add(item.Path))
                {
                    if (_registryMatchCount >= _options.MaxRegistryMatches)
                    {
                        _capped = true;
                        LogStore.Warn($"Match limit of {_options.MaxRegistryMatches} reached; results are partial.");
                        return;
                    }

                    _registryMatchCount++;
                    _results.Add(item);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogStore.Warn($"Could not scan {label}: {ex.Message}");
            _warnings.Add($"Could not scan {label}.");
        }
    }

    // ---- Filesystem -----------------------------------------------------

    private void ScanFiles(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var roots = FileRoots();
        if (roots.Count == 0)
        {
            return;
        }

        _deadline = DateTime.UtcNow + TimeSpan.FromSeconds(_options.FileSeconds);
        // Relative deadline: an absolute Stopwatch.GetTimestamp() is time-since-boot and
        // would compare as already-expired, silently skipping every size calculation.
        _sizeBudgetDeadline = System.Diagnostics.Stopwatch.GetTimestamp() + StopwatchTicks(20);

        for (int i = 0; i < roots.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var root = roots[i];
            int percent = (int)(100.0 * i / roots.Count);

            progress?.Report(new ScanProgress(
                "Files",
                $"Scanning {root.Label}",
                _results.Count,
                percent));

WalkDirectory(
                root.Path,
                0,
                root.Depth,
                root.AllowWeak && _options.UsePublisherTokens,
                progress,
                root.Label);

            if (_capped || DateTime.UtcNow > _deadline)
            {
                LogStore.Warn(
                    $"File scan stopped early ({(_capped ? "match limit reached" : "time limit reached")}). Results are partial.");
                break;
            }
        }
    }

    private static long StopwatchTicks(double seconds) =>
        (long)(seconds * System.Diagnostics.Stopwatch.Frequency);

private void WalkDirectory(
        string directory,
        int depth,
        int maxDepth,
        bool allowWeak,
        IProgress<ScanProgress>? progress,
        string rootLabel)
    {
        cancellationGuard();

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch
        {
            // Access denied, in use, or a broken symlink.
            return;
        }

        foreach (string entry in entries)
        {
            cancellationGuard();

            if (DateTime.UtcNow > _deadline || _capped)
            {
                return;
            }

            string name;
            try
            {
                name = Path.GetFileName(entry);
            }
            catch
            {
                continue;
            }

            if (name.Length == 0 || ExcludedLeafNames.Contains(name))
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

if (isDirectory)
            {
                if (IsProtected(entry))
                {
                    continue;
                }

                try
                {
                    var attrs = File.GetAttributes(entry);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        // Symlinks and junctions would loop the walk.
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                string? match = _tokens.Match(name) ?? (allowWeak ? _tokens.MatchWeak(name) : null);
                if (match is not null)
                {
                    AddFolder(entry, name, match, progress, rootLabel);
                    // Prune: the whole subtree belongs to this match.
                    continue;
                }

                if (depth + 1 <= maxDepth)
                {
                    WalkDirectory(entry, depth + 1, maxDepth, allowWeak, progress, rootLabel);
                }

                continue;
            }

            // Loose files are only considered directly inside a scan root. Matching individual
            // files deeper than that finds source code and scripts that merely mention the
            // product name, and offering to delete those would be actively harmful.
            if (depth != 0)
            {
                continue;
            }

            string? fileMatch = _tokens.Match(name);
            if (fileMatch is not null)
            {
                AddFile(entry, name, fileMatch);
            }
        }
    }

    private void cancellationGuard() => _ct.ThrowIfCancellationRequested();

    private void AddFolder(
        string path,
        string name,
        string match,
        IProgress<ScanProgress>? progress,
        string rootLabel)
    {
        if (!TryReserve(path, _options.MaxFileMatches, ref _fileMatchCount))
        {
            return;
        }

        long size = 0;
        if (System.Diagnostics.Stopwatch.GetTimestamp() < _sizeBudgetDeadline)
        {
            try
            {
                size = SizeCalculator.GetDirectorySize(path, TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Size is cosmetic; never fail the scan over it.
            }
        }

        _results.Add(new ResidueItem
        {
            Kind = ResidueKind.Folder,
            Path = path,
            DisplayName = name,
            SizeBytes = size,
MatchedToken = match,
            Target = new FileTarget(path),
            Confidence = Confidence.Safe,
        });

        _progress?.Report(new ScanProgress("Files", $"{rootLabel}: {name}", _results.Count, 55));
    }

    private void AddFile(string path, string name, string match)
    {
        if (!TryReserve(path, _options.MaxFileMatches, ref _fileMatchCount))
        {
            return;
        }

        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch
        {
            // ignore
        }

        _results.Add(new ResidueItem
        {
            Kind = ResidueKind.File,
            Path = path,
            DisplayName = name,
            SizeBytes = size,
MatchedToken = match,
            Target = new FileTarget(path),
            Confidence = Confidence.Moderate,
        });
    }

    private void AddSurvivingInstallFolder()
    {
        string location = _app.InstallLocation;
        if (string.IsNullOrWhiteSpace(location))
        {
            return;
        }

        try
        {
            location = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
            if (!Directory.Exists(location))
            {
                return;
            }

            // The install folder is force-added without needing a token match, so it must
            // be held to the same safety bar as every other result. InstallLocation is
            // routinely a shared location such as "C:\ProgramData\Package Cache\{GUID}"
            // or "C:\Windows\System32", and offering those for deletion would be
            // catastrophic.
            if (IsProtected(location))
            {
                LogStore.Warn($"Install location '{location}' is a protected shared location; not offering it for removal.");
                _warnings.Add($"Install location '{location}' is a shared/protected location and was excluded.");
                return;
            }

            string leaf = Path.GetFileName(location.TrimEnd('\\'));
            if (ExcludedLeafNames.Contains(leaf))
            {
                return;
            }

            if (!TryReserve(location, _options.MaxFileMatches, ref _fileMatchCount))
            {
                return;
            }

            _results.Add(new ResidueItem
            {
                Kind = ResidueKind.Folder,
                Path = location,
                DisplayName = leaf + " (original install folder)",
                SizeBytes = SizeCalculator.GetDirectorySize(location, TimeSpan.FromSeconds(3)),
                MatchedToken = "install location",
                Target = new FileTarget(location),
                Confidence = Confidence.Safe,
            });
        }
        catch (Exception ex)
        {
            LogStore.Warn($"Could not check the install folder: {ex.Message}");
        }
    }

private bool TryReserve(string path, int cap, ref int counter)
    {
        if (!_seen.Add(path))
        {
            return false;
        }

        if (counter >= cap)
        {
            if (!_capped)
            {
                _capped = true;
                LogStore.Warn($"Match limit of {cap} reached; results are partial.");
            }

            return false;
        }

        counter++;
        return true;
    }

    // ---- Registry -------------------------------------------------------

    private sealed record RegRoot(RegistryHive Hive, RegistryView View, string Path, int Depth, bool AllowWeak, string Label);

    private static IReadOnlyList<RegRoot> RegistryRoots(int uninstallDepth) =>
    [
        new(RegistryHive.CurrentUser, RegistryView.Registry64, "Software", 3, true, "HKCU\\Software"),
        new(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Classes", 2, true, "HKCU file associations"),
        new(RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE", uninstallDepth, false, "HKLM\\SOFTWARE (64)"),
        new(RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE", uninstallDepth, false, "HKLM\\SOFTWARE (32)"),
    ];

private void ScanRegistry(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var roots = RegistryRoots(Math.Max(2, _options.RegistryDepth));

        // The roots are disjoint subtrees of different hives/views, so they can be walked
        // concurrently. This is the dominant cost of a residue scan.
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        };

        try
        {
            Parallel.ForEach(roots, parallelOptions, root =>
            {
                progress?.Report(new ScanProgress("Registry", $"Scanning {root.Label}", _results.Count, 60));

                try
                {
                    using var hiveKey = RegistryKey.OpenBaseKey(root.Hive, root.View);
                    using var baseKey = hiveKey.OpenSubKey(root.Path, writable: false);
                    if (baseKey is null)
                    {
                        return;
                    }

                    WalkRegistry(baseKey, root, 0, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    LogStore.Warn($"Registry scan skipped {root.Label}: not permitted.");
                    lock (_sync)
                    {
                        _warnings.Add($"{root.Label} could not be read; it was not scanned.");
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AggregateException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        progress?.Report(new ScanProgress("Registry", "Registry scan complete", _results.Count, 80));
    }

    private void WalkRegistry(RegistryKey parent, RegRoot root, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (depth > root.Depth || _capped)
        {
            return;
        }

        string[] names;
        try
        {
            names = parent.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

foreach (string name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_capped || _registryBudgetExhausted())
            {
                return;
            }

            if (ExcludedRegSegments.Contains(name))
            {
                continue;
            }

            string childPath = parent.Name + "\\" + name;

            string? match = _tokens.Match(name) ?? (root.AllowWeak && _options.UsePublisherTokens ? _tokens.MatchWeak(name) : null);

            if (match is not null)
            {
                // Report by name without opening the key: matched subtrees are pruned, so
                // opening it could not reveal anything further.
                AddRegistryKey(root, childPath, name, match);
                continue;
            }

            // A parent's name says nothing about its children ("Microsoft" can contain
            // "Discord"), so there is no sound name-based prefilter here. The wins are
            // instead the parallel walk across independent roots, the excluded segments
            // and the time/key budgets below.
            Interlocked.Increment(ref _registryKeysVisited);

            RegistryKey? child = null;
            try
            {
                child = parent.OpenSubKey(name, writable: false);
            }
            catch
            {
                // Unreadable key - nothing more to report.
            }

            if (child is not null)
            {
                // Children of HKCU\Software\Classes are the file-type registrations; their
                // identity usually lives in the default value, not the key name.
                // The test must be on the PARENT path: the child's path already has the
                // association name appended and never ends with "Software\Classes".
                if (root.Hive == RegistryHive.CurrentUser &&
                    parent.Name.EndsWith(@"\Classes", StringComparison.OrdinalIgnoreCase))
                {
                    CheckAssociationValues(child, childPath, root);
                }

                WalkRegistry(child, root, depth + 1, cancellationToken);
                child.Dispose();
            }
        }
    }

    private bool _registryBudgetWarned;

    /// <summary>
    /// Bounds the registry walk. Without this a broad token walks tens of thousands of keys
    /// with no clock attached, and the scan appears to hang.
    /// </summary>
    private bool _registryBudgetExhausted()
    {
        if (Volatile.Read(ref _registryKeysVisited) < MaxRegistryKeysVisited && DateTime.UtcNow < _registryDeadline)
        {
            return false;
        }

        if (!_registryBudgetWarned)
        {
            _registryBudgetWarned = true;
            bool timedOut = DateTime.UtcNow >= _registryDeadline;
            LogStore.Warn(timedOut
                ? "Registry scan stopped on its time budget; registry results are partial."
                : $"Registry scan stopped after {MaxRegistryKeysVisited} keys; registry results are partial.");
            _warnings.Add(timedOut
                ? "The registry scan hit its time limit, so some registry leftovers may be missing."
                : "The registry scan hit its key budget, so some registry leftovers may be missing.");
        }

        return true;
    }

private void AddRegistryKey(RegRoot root, string keyPath, string name, string match)
    {
        lock (_sync)
        {
            if (_registryMatchCount >= _options.MaxRegistryMatches)
            {
                _capped = true;
                LogStore.Warn($"Match limit of {_options.MaxRegistryMatches} reached; results are partial.");
                return;
            }

            _registryMatchCount++;

            _results.Add(new ResidueItem
            {
                Kind = ResidueKind.RegistryKey,
                Path = keyPath,
                DisplayName = name,
                MatchedToken = match,
                Reg = new RegLocation(root.Hive, root.View, keyPath),
                Target = new RegistryKeyTarget(new RegLocation(root.Hive, root.View, keyPath)),
                Confidence = Confidence.Safe,
            });
        }
    }

    /// <summary>
    /// File-type associations show up as the default value of a key rather than the key name,
    /// e.g. HKCU\Software\Classes\MyApp.pdf with a friendly description as the default value.
    /// </summary>
    private void CheckAssociationValues(RegistryKey key, string keyPath, RegRoot root)
    {
        string[] valueNames;
        try
        {
            valueNames = key.GetValueNames();
        }
        catch
        {
            return;
        }

foreach (string valueName in valueNames)
        {
            string? match = null;
            try
            {
                if (key.GetValue(valueName) is string text && text.Length is > 0 and < 300)
                {
                    // Match the value's own text only. Matching the full registry path would
                    // let any token that happens to appear in "HKEY_CURRENT_USER\Software\
                    // Classes" match every association key in the hive.
                    match = _tokens.Match(text)
                        ?? (root.AllowWeak && _options.UsePublisherTokens ? _tokens.MatchWeak(text) : null);
                }
            }
            catch
            {
                // continue
            }

            if (match is null)
            {
                continue;
            }

            string fullPath = keyPath + "\\" + valueName;

            lock (_sync)
            {
                if (_registryMatchCount >= _options.MaxRegistryMatches)
                {
                    _capped = true;
                    return;
                }

                _registryMatchCount++;

                _results.Add(new ResidueItem
                {
                    Kind = ResidueKind.RegistryValue,
                    Path = fullPath,
                    DisplayName = string.IsNullOrEmpty(valueName) ? "(Default)" : valueName,
                    MatchedToken = match,
                    Reg = new RegLocation(root.Hive, root.View, keyPath),
                    ValueName = valueName,
                    Target = new RegistryValueTarget(new RegLocation(root.Hive, root.View, keyPath), valueName),
                    Confidence = Confidence.Moderate,
                });
            }
        }
    }
}

internal static class RegistryResidueOps
{
    public static DeletionResult DeleteKey(RegLocation location)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(location.Hive, location.View);
            root.DeleteSubKeyTree(location.SubKey, throwOnMissingSubKey: false);
            return new DeletionResult(true, location.SubKey, false, null);
        }
        catch (Exception ex)
        {
            return new DeletionResult(false, location.SubKey, false, ex.Message);
        }
    }

    public static DeletionResult DeleteValue(RegLocation location, string valueName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(location.Hive, location.View);
            using var key = root.OpenSubKey(location.SubKey, writable: true);
            if (key is null)
            {
                return new DeletionResult(true, location.SubKey, false, null);
            }

            key.DeleteValue(valueName, throwOnMissingValue: false);
            return new DeletionResult(true, location.SubKey + "\\" + valueName, false, null);
        }
        catch (Exception ex)
        {
            return new DeletionResult(false, location.SubKey + "\\" + valueName, false, ex.Message);
        }
    }
}
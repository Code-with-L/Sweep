using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sweep;

/// <summary>
/// Headless self-audit. Checks that each subsystem actually works on this machine rather
/// than assuming it does, and reports timings so slow scans can be attributed.
///
/// This exists because the alternative is shipping claims that were never measured.
/// </summary>
internal static class Audit
{
    public static async Task<int> Run(string[] args)
    {
        Native.TryAttachParentConsole();

        var problems = new List<string>();
        var notes = new List<string>();

        string? target = args
            .FirstOrDefault(a => a.StartsWith("--app=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1];

        try
        {
            PrintEnvironment();
            Console.WriteLine();
            Problems(problems, notes);
            Console.WriteLine();
            var (apps, scanMs) = ProgramList(problems);
            Console.WriteLine();
            Tokens(apps, problems);
            Console.WriteLine();
            Plans(apps, problems);
            Console.WriteLine();
            await Icons(apps, notes);
            Console.WriteLine();
            await Residue(apps, target, problems, notes);
            Console.WriteLine();
            Memory();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AUDIT FAILED: {ex}");
            return 1;
        }

        Console.WriteLine(new string('=', 64));
        if (notes.Count > 0)
        {
            Console.WriteLine("NOTES");
            foreach (string note in notes)
            {
                Console.WriteLine($"  - {note}");
            }

            Console.WriteLine();
        }

        if (problems.Count == 0)
        {
            Console.WriteLine("AUDIT PASSED - no problems detected");
            return 0;
        }

        Console.WriteLine($"AUDIT FOUND {problems.Count} PROBLEM(S)");
        foreach (string problem in problems)
        {
            Console.WriteLine($"  ! {problem}");
        }

        return 2;
    }

    private static void PrintEnvironment()
    {
        Console.WriteLine("== Environment ==");
        Console.WriteLine($"process arch    : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"OS arch         : {RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"framework       : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS              : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"64-bit OS       : {System.Environment.Is64BitOperatingSystem}");
        Console.WriteLine($"elevated        : {Program.IsElevated}");
        Console.WriteLine($"processors      : {System.Environment.ProcessorCount}");
        Console.WriteLine($"Windows 11 APIs : {Win11.Supported}");
        Console.WriteLine($"working set     : {System.Environment.WorkingSet / (1024 * 1024)} MB");
    }

    private static void Problems(List<string> problems, List<string> notes)
    {
        Console.WriteLine("== Permissions ==");

        foreach (var (hive, label) in new[]
                 {
                     (Microsoft.Win32.RegistryHive.LocalMachine, "HKLM"),
                     (Microsoft.Win32.RegistryHive.CurrentUser, "HKCU"),
                 })
        {
            try
            {
                using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    hive, Microsoft.Win32.RegistryView.Registry64);
                Console.WriteLine($"{label,-6} : readable");

                if (!Program.IsElevated)
                {
                    notes.Add($"{label} writes will fail without elevation; system leftovers may be reported but not removable.");
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{label} unreadable: {ex.Message}");
            }
        }

        string temp = Environment.GetEnvironmentVariable("TEMP") ?? string.Empty;
        bool canWrite = false;
        try
        {
            string probe = Path.Combine(temp, $"sweep-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            canWrite = true;
        }
        catch
        {
            // reported below
        }

        Console.WriteLine($"temp    : {(canWrite ? "writable" : "NOT WRITABLE")}");
        if (!canWrite)
        {
            problems.Add("TEMP is not writable; the log file cannot be written.");
        }

        try
        {
            Directory.CreateDirectory(LogStore.LogDirectory);
            Console.WriteLine($"logs    : {LogStore.LogDirectory}");
        }
        catch (Exception ex)
        {
            problems.Add($"Log directory unavailable: {ex.Message}");
        }
    }

    private static (List<InstalledApp> Apps, long Ms) ProgramList(List<string> problems)
    {
        Console.WriteLine("== Program list ==");

        var sw = Stopwatch.StartNew();
        var apps = RegistryScanner.Scan();
        sw.Stop();

        Console.WriteLine($"programs         : {apps.Count}");
        Console.WriteLine($"scan time        : {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"with uninstaller : {apps.Count(a => a.HasUninstaller)}");
        Console.WriteLine($"per-user         : {apps.Count(a => a.IsPerUser)}");
        Console.WriteLine($"32-bit view      : {apps.Count(a => a.Location.View == Microsoft.Win32.RegistryView.Registry32)}");

        if (apps.Count == 0)
        {
            problems.Add("No programs were enumerated; the uninstall registry keys could not be read.");
        }

        return (apps, sw.ElapsedMilliseconds);
    }

    private static void Tokens(List<InstalledApp> apps, List<string> problems)
    {
        Console.WriteLine("== Search tokens ==");

        int empty = 0, overBroad = 0;
        foreach (var app in apps)
        {
            var tokens = NameTokens.BuildTokenSet(app);
            if (tokens.Strong.Count == 0)
            {
                empty++;
            }

            // A token short enough to appear inside unrelated words is a false-positive
            // source. These are permitted for whole-word matching only.
            if (tokens.Strong.Any(t => t.Length < 4))
            {
                overBroad++;
            }
        }

        Console.WriteLine($"programs with no tokens : {empty}");
        Console.WriteLine($"using a 3-char token    : {overBroad}");

        if (empty > 0)
        {
            problems.Add($"{empty} program(s) produced no search tokens; their residue scan would be skipped.");
        }

        Console.WriteLine("sample:");
        foreach (var app in apps.Take(8))
        {
            Console.WriteLine($"  {Trim(app.DisplayName, 36),-36} -> [{string.Join(", ", NameTokens.BuildTokenSet(app).Strong)}]");
        }
    }

    private static void Plans(List<InstalledApp> apps, List<string> problems)
    {
        Console.WriteLine("== Uninstall plans ==");

        int noPlan = 0, missingExe = 0, silent = 0, unknown = 0;

        foreach (var app in apps)
        {
            var plan = UninstallRunner.BuildPlan(app, preferSilent: true);
            if (plan is null)
            {
                if (app.HasUninstaller)
                {
                    noPlan++;
                }

                continue;
            }

            if (plan.Silent)
            {
                silent++;
            }

            if (plan.Family == InstallerFamily.Unknown)
            {
                unknown++;
            }

            if (!File.Exists(plan.FileName))
            {
                missingExe++;
            }
        }

        Console.WriteLine($"plans built      : {apps.Count(a => a.HasUninstaller) - noPlan}");
        Console.WriteLine($"silent applied   : {silent}");
        Console.WriteLine($"unknown family   : {unknown}");
        Console.WriteLine($"exe not on disk  : {missingExe}");

        if (noPlan > 0)
        {
            problems.Add($"{noPlan} program(s) have an uninstall command that could not be parsed.");
        }

        if (missingExe > 0)
        {
            Console.WriteLine("  (a missing executable usually means a stale registry entry, not a bug)");
        }
    }

    private static async Task Icons(List<InstalledApp> apps, List<string> notes)
    {
        Console.WriteLine("== Icons ==");

        var sw = Stopwatch.StartNew();
        IconProvider.Clear();
        IconProvider.Prefetch(apps.Take(150), CancellationToken.None);
        sw.Stop();

        int loaded = 0, real = 0;
        foreach (var app in apps.Take(150))
        {
            if (!IconProvider.IsLoaded(app))
            {
                continue;
            }

            loaded++;

            // A badge is generated rather than extracted, which is honest but worth
            // counting so the gap stays visible.
            if (IconProvider.CandidateSources(app).Any())
            {
                real++;
            }
        }

        Console.WriteLine($"extracted         : {real} of {loaded}");
        Console.WriteLine($"time for 150      : {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"per icon          : {(loaded > 0 ? sw.Elapsed.TotalMilliseconds / loaded : 0):0.0} ms");

        if (sw.ElapsedMilliseconds > 3000)
        {
            notes.Add($"Icon extraction took {sw.ElapsedMilliseconds} ms for 150 programs; it runs off the UI thread but a long list will take time to populate.");
        }
    }

    private static async Task Residue(
        List<InstalledApp> apps, string? requested, List<string> problems, List<string> notes)
    {
        Console.WriteLine("== Residue scan ==");

        var target = apps.FirstOrDefault(a =>
                           !string.IsNullOrWhiteSpace(requested) &&
                           a.DisplayName.Contains(requested, StringComparison.OrdinalIgnoreCase))
                       ?? apps.FirstOrDefault(a => a.HasUninstaller);

        if (target is null)
        {
            problems.Add("No program available to test the residue scan against.");
            return;
        }

        Console.WriteLine($"target            : {target.DisplayName}");

        var options = new ScanOptions
        {
            FileDepth = 3,
            FileSeconds = 120,
            MaxFileMatches = 2000,
            MaxRegistryMatches = 300,
            RegistryDepth = 3,
            ScanRegistry = true,
            UsePublisherTokens = false,
        };

        var sw = Stopwatch.StartNew();
        var scanner = new ResidueScanner(target, options);
        var items = await scanner.ScanAsync(null, CancellationToken.None);
        sw.Stop();

        Console.WriteLine($"total time        : {sw.ElapsedMilliseconds} ms");
        foreach (var (phase, ticks) in scanner.PhaseTimings)
        {
            double ms = ticks * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine($"  {phase,-10} {ms,8:0} ms");
        }

        Console.WriteLine($"matches           : {items.Count}");
        foreach (var group in items.GroupBy(i => i.Kind).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Key,-18} {group.Count()}");
        }

        Console.WriteLine();
        Console.WriteLine("  guardrail check (nothing removable may point at a shared location):");
        foreach (var item in items.Where(i => i.Kind is ResidueKind.Folder or ResidueKind.File))
        {
            if (Safety.IsProtectedLocation(item.Path))
            {
                problems.Add($"GUARDRAIL BREACH: {item.Kind} at a protected location was offered: {item.Path}");
            }
        }

        int risky = items.Count(i => i.Confidence == Confidence.Risky);
        int moderate = items.Count(i => i.Confidence == Confidence.Moderate);
        Console.WriteLine($"  safe={items.Count - risky - moderate} moderate={moderate} risky={risky}");

        if (scanner.Warnings.Count > 0)
        {
            Console.WriteLine("  warnings:");
            foreach (string warning in scanner.Warnings)
            {
                Console.WriteLine($"    ! {warning}");
                notes.Add(warning);
            }
        }
    }

    private static void Memory()
    {
        var info = GC.GetGCMemoryInfo();
        Console.WriteLine("== Memory ==");
        Console.WriteLine($"managed heap    : {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
        Console.WriteLine($"gen0/1/2        : {info.GenerationInfo[0].SizeAfterBytes / 1024} / " +
                          $"{info.GenerationInfo[1].SizeAfterBytes / 1024} / " +
                          $"{info.GenerationInfo[2].SizeAfterBytes / 1024} KB");
        Console.WriteLine($"working set     : {System.Environment.WorkingSet / (1024 * 1024)} MB");
    }

    private static string Trim(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..(max - 3)] + "...";
}
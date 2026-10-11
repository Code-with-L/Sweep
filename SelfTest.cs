using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Sweep;

/// <summary>
/// Headless verification mode. Runs the real scanners against the real machine so the
/// behaviour can be checked without driving the UI.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> Run(string[] args)
    {
        Native.TryAttachParentConsole();

        try
        {
            string? requested = GetArg(args, "--app");
            int depth = int.TryParse(GetArg(args, "--depth"), out int d) ? d : 2;
            int seconds = int.TryParse(GetArg(args, "--seconds"), out int s) ? s : 45;
            bool skipFiles = args.Contains("--skip-files", StringComparer.OrdinalIgnoreCase);

            PrintEnvironment();
            Console.WriteLine();

            if (args.Contains("--dump-uninstallers", StringComparer.OrdinalIgnoreCase))
            {
                return DumpUninstallers();
            }

            if (args.Contains("--tokens", StringComparer.OrdinalIgnoreCase))
            {
                return DumpTokens();
            }

            if (args.Contains("--autorun", StringComparer.OrdinalIgnoreCase))
            {
                return DumpAutorun();
            }

            if (args.Contains("--guardrails", StringComparer.OrdinalIgnoreCase))
            {
                return DumpGuardrails();
            }

            if (args.Contains("--grouping", StringComparer.OrdinalIgnoreCase))
            {
                return DumpGrouping();
            }

            if (args.Contains("--storeapps", StringComparer.OrdinalIgnoreCase))
            {
                return DumpStoreApps();
            }

            var sw = Stopwatch.StartNew();
            var apps = RegistryScanner.Scan();
            sw.Stop();

            Console.WriteLine($"== Installed programs ==");
            Console.WriteLine($"found      : {apps.Count}");
            Console.WriteLine($"elapsed    : {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"with uninstaller: {apps.Count(a => a.HasUninstaller)}");
            Console.WriteLine($"per-user   : {apps.Count(a => a.IsPerUser)}");
            Console.WriteLine($"32-bit     : {apps.Count(a => a.Location.View == RegistryView.Registry32)}");
            Console.WriteLine();

            if (apps.Count == 0)
            {
                Console.Error.WriteLine("FAIL: no programs were enumerated.");
                return 2;
            }

            Console.WriteLine("sample rows:");
            foreach (var app in apps.Take(12))
            {
                Console.WriteLine($"  {Trim(app.DisplayName, 34),-34} {Trim(app.DisplayVersion, 12),-12} {Trim(app.Publisher, 22),-22} {Trim(app.ScopeLabel, 18),-18} {app.SizeDisplay,-9} {(app.HasUninstaller ? "yes" : "NO ")}");
            }

            Console.WriteLine();

            var target = PickTarget(apps, requested);
            if (target is null)
            {
                Console.Error.WriteLine("FAIL: no suitable target program found to test with.");
                return 3;
            }

            Console.WriteLine($"== Target: {target.DisplayName} ==");
            Console.WriteLine($"publisher   : {target.Publisher}");
            Console.WriteLine($"location    : {target.InstallLocation}");
            Console.WriteLine($"installed   : {target.InstallDate:yyyy-MM-dd}");
            Console.WriteLine($"registry    : {target.Location}");
            Console.WriteLine($"uninstall   : {target.UninstallString}");
            Console.WriteLine($"quiet       : {target.QuietUninstallString}");
            Console.WriteLine($"exe         : {target.ExecutablePath ?? "(not resolved)"}");

            var tokens = NameTokens.BuildTokenSet(target);
            Console.WriteLine($"tokens      : [{string.Join(", ", tokens.Strong)}]");
            Console.WriteLine($"weak tokens : [{string.Join(", ", tokens.Weak)}]");
            Console.WriteLine($"compact     : {tokens.CompactName}");
            Console.WriteLine();

            var plan = UninstallRunner.BuildPlan(target, preferSilent: true);
            Console.WriteLine("== Uninstall plan (not executed) ==");
            if (plan is null)
            {
                Console.WriteLine("no uninstall command");
            }
            else
            {
                Console.WriteLine($"family  : {UninstallRunner.Describe(plan.Family)}");
                Console.WriteLine($"exe     : {plan.FileName}");
                Console.WriteLine($"args    : {plan.Arguments}");
                Console.WriteLine($"silent  : {plan.Silent}");
                Console.WriteLine($"note    : {plan.Note}");
                Console.WriteLine($"exe exists: {File.Exists(plan.FileName)}");
            }

            Console.WriteLine();

            if (skipFiles)
            {
                Console.WriteLine("== Residue scan skipped by flag ==");
                return 0;
            }

            var options = new ScanOptions
            {
                FileDepth = depth,
                FileSeconds = seconds,
                MaxFileMatches = 200,
                MaxRegistryMatches = 100,
                RegistryDepth = 2,
                ScanRegistry = true,
                UsePublisherTokens = false,
            };

            var progress = new InlineProgress<ScanProgress>(r =>
                Console.WriteLine($"  [{r.Percent,3}%] {r.Stage,-9} {Trim(r.Detail, 60),-60} {r.Matches,4} found"));

            Console.WriteLine("== Residue scan ==");
            sw.Restart();
            var scanner = new ResidueScanner(target, options);
            var items = await scanner.ScanAsync(progress, CancellationToken.None);
            sw.Stop();

            Console.WriteLine();
            Console.WriteLine($"matches    : {items.Count}");
            Console.WriteLine($"elapsed    : {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"total size : {SizeCalculator.Format(items.Sum(i => i.SizeBytes))}");

            foreach (var group in items.GroupBy(i => i.Kind).OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"  {group.Key,-20} {group.Count()}");
            }

            if (scanner.Warnings.Count > 0)
            {
                Console.WriteLine("warnings:");
                foreach (string warning in scanner.Warnings)
                {
                    Console.WriteLine($"  ! {warning}");
                }
            }

            Console.WriteLine();

            foreach (var item in items.Take(25))
            {
                Console.WriteLine($"  {item.Kind,-13} {Trim(item.Path, 76),-76} {item.SizeDisplay,-10} ({item.MatchedToken})");
            }

            Console.WriteLine();
            Console.WriteLine("SELFTEST OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SELFTEST FAILED: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Asserts the group header rows line up with the program rows around them. The mapping is
    /// the part that silently breaks: get it wrong and selecting a program runs the wrong
    /// uninstaller, which is the worst possible failure for this app.
    /// </summary>
    private static int DumpGrouping()
    {
        int failures = 0;
        int checks = 0;

        void Check(string label, int expected, int actual)
        {
            checks++;
            bool ok = expected == actual;
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label,-46} expected={expected,4} actual={actual,4}");
        }

        void CheckName(string label, string expected, string actual)
        {
            checks++;
            bool ok = string.Equals(expected, actual, StringComparison.Ordinal);
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label,-46} expected={expected,-12} actual={actual}");
        }

        static InstalledApp App(string name, string installDate) =>
            new() { DisplayName = name, InstallDateRaw = installDate };

        string today = DateTime.Now.ToString("yyyyMMdd");
        string longAgo = DateTime.Now.AddYears(-3).ToString("yyyyMMdd");

        // Three recent, two old. Interleaved on purpose so a stable partition is observable.
        var apps = new List<InstalledApp>
        {
            App("Recent A", today),
            App("Old A", longAgo),
            App("Recent B", today),
            App("Old B", longAgo),
            App("Recent C", today),
        };

        var g = new GroupIndex { Enabled = true };
        g.Rebuild(apps, recentlyInstalledFirst: true, days: 30);

        Check("orders apps when grouping is on", 1, g.OrdersApps ? 1 : 0);
        Check("recent count", 3, g.RecentCount);

        // Layout: header, 3 recent, header, 2 old = 7 rows.
        Check("total rows", 7, g.TotalRows);
        Check("row 0 is a header", 1, g.IsGroupRow(0) ? 1 : 0);
        Check("row 4 is a header", 1, g.IsGroupRow(4) ? 1 : 0);
        Check("row 1 is not a header", 0, g.IsGroupRow(1) ? 1 : 0);
        Check("recent header count", 3, g.Groups[0].Count);
        Check("other header count", 2, g.Groups[4].Count);

        // Stable partition preserves the caller's sort order inside each section.
CheckName("first ordered app", "Recent A", g.Ordered[0].DisplayName);
        CheckName("last recent app", "Recent C", g.Ordered[2].DisplayName);
        CheckName("first other app", "Old A", g.Ordered[3].DisplayName);
        CheckName("last other app", "Old B", g.Ordered[4].DisplayName);

        // Program index -> grid row, and back again. This round trip is what selection uses.
        Check("grid row of program 0", 1, g.GridRowFor(0));
        Check("grid row of program 2", 3, g.GridRowFor(2));
        Check("grid row of program 3", 5, g.GridRowFor(3));
        Check("grid row of program 4", 6, g.GridRowFor(4));

        Check("grid row 4 maps to header", -1, g.DataRowFor(4));
        Check("grid row 1 maps to program 0", 0, g.DataRowFor(1));
        Check("grid row 3 maps to program 2", 2, g.DataRowFor(3));
        Check("grid row 5 maps to program 3", 3, g.DataRowFor(5));
        Check("grid row 6 maps to program 4", 4, g.DataRowFor(6));

        for (int i = 0; i < apps.Count; i++)
        {
            Check($"round trip program {i}", i, g.DataRowFor(g.GridRowFor(i)));
        }

        // Every data row must be accounted for exactly once.
        var seen = new HashSet<int>();
        for (int row = 0; row < g.TotalRows; row++)
        {
            int index = g.DataRowFor(row);
            if (index >= 0)
            {
                seen.Add(index);
            }
        }

        Check("distinct programs reachable", apps.Count, seen.Count);

        // Degenerate shapes: no recent apps, all recent apps, and grouping switched off.
        var allOld = new List<InstalledApp> { App("X", longAgo), App("Y", longAgo) };
        var g2 = new GroupIndex { Enabled = true };
        g2.Rebuild(allOld, true, 30);
        Check("all old: one header", 3, g2.TotalRows);
        Check("all old: row 0 is header", 1, g2.IsGroupRow(0) ? 1 : 0);
        Check("all old: program 0 at row 1", 1, g2.GridRowFor(0));
        Check("all old: row 1 -> program 0", 0, g2.DataRowFor(1));

        var allNew = new List<InstalledApp> { App("X", today), App("Y", today) };
        var g3 = new GroupIndex { Enabled = true };
        g3.Rebuild(allNew, true, 30);
        Check("all new: one header", 3, g3.TotalRows);
        Check("all new: program 1 at row 2", 2, g3.GridRowFor(1));

        var g4 = new GroupIndex { Enabled = false };
        g4.Rebuild(apps, true, 30);
        Check("grouping off: rows equal programs", apps.Count, g4.TotalRows);
        Check("grouping off: no headers", 0, g4.IsGroupRow(0) ? 1 : 0);
        Check("grouping off: row 3 -> program 3", 3, g4.DataRowFor(3));
        Check("grouping off: does not reorder", 0, g4.OrdersApps ? 1 : 0);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"GROUPING TESTS PASSED - {checks} checks"
            : $"GROUPING TESTS FAILED - {failures} of {checks} checks failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Asserts the safety deny-list still refuses what it must and permits what it must.
    /// The guardrail is the last thing standing between a name-matching mistake and a
    /// broken machine, so it gets an explicit test rather than implicit coverage.
    /// </summary>
    private static int DumpGuardrails()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        (string Path, bool ShouldBlock, string Why)[] cases =
        [
            (@"C:\", true, "drive root"),
            (windows, true, "Windows directory itself"),
            (Path.Combine(windows, "System32"), true, "System32"),
            (Path.Combine(windows, "System32", "drivers"), true, "nested under System32"),
            (Path.Combine(programData, "Microsoft"), true, "ProgramData\\Microsoft is shared OS state"),
            (Path.Combine(programData, "Package Cache"), true, "MSI repair cache"),
            (Path.Combine(local, "Packages"), true, "Store app sandbox"),
            (profile, true, "user profile root"),
            (@"C:\Program Files", true, "Program Files itself"),
            (@"C:\Program Files (x86)", true, "Program Files (x86) itself"),
            (Path.Combine(programData, "Microsoft", "Windows", "Start Menu"), true, "Start Menu root is shared"),
            
            // Must be permitted: these are ordinary per-application leftovers.
            (@"C:\Program Files\Some App", false, "an app's own folder in Program Files"),
            (@"C:\Program Files\Some App\bin", false, "nested under an app folder"),
            (@"C:\ProgramData\Some Vendor", false, "a vendor folder in ProgramData"),
            (Path.Combine(profile, "AppData", "Local", "SomeApp"), false, "AppData Local"),
            (Path.Combine(profile, "AppData", "Roaming", "SomeApp"), false, "AppData Roaming"),
            (Path.Combine(programData, "Microsoft", "Windows", "Start Menu", "Programs", "SomeApp"),
                false, "per-app Start Menu shortcut folder"),
            (Path.Combine(local, "SomeApp", "cache.dat"), false, "a file inside an app folder"),
        ];

        int failed = 0;
        Console.WriteLine("== Safety guardrail assertions ==");

        foreach (var (path, shouldBlock, why) in cases)
        {
            bool blocked = Safety.IsProtectedLocation(path);
            bool ok = blocked == shouldBlock;
            if (!ok)
            {
                failed++;
            }

            Console.WriteLine("  " + (ok ? "PASS" : "FAIL") + "  " + (blocked ? "blocked" : "allowed").PadRight(8) + "  " + Trim(path, 62).PadRight(62) + "  " + why);
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "GUARDRAIL TESTS PASSED" : $"GUARDRAIL TESTS FAILED: {failed}");
        return failed == 0 ? 0 : 5;
    }

    /// <summary>Exercises the startup/autorun enumerators that back the Startup entries view.</summary>
    private static int DumpAutorun()
    {
        var sw = Stopwatch.StartNew();

        var startup = SystemArtifacts.AllStartupEntries(CancellationToken.None).ToList();
        var services = SystemArtifacts.AllServices(CancellationToken.None).ToList();
        sw.Stop();

        Console.WriteLine("== Startup / autorun ==");
        Console.WriteLine($"startup entries  : {startup.Count}");
        foreach (var entry in startup.Take(10))
        {
            Console.WriteLine($"  {Trim(entry.Name, 40),-40} {Trim(entry.Command, 50)}");
        }

        Console.WriteLine();
        Console.WriteLine($"services         : {services.Count}");
        foreach (var service in services.Take(10))
        {
            Console.WriteLine($"  {Trim(service.Name, 34),-34} {SystemArtifacts.ServiceStartMode(service.StartType),-10} {Trim(service.ImagePath, 50)}");
        }

        Console.WriteLine();
        Console.WriteLine($"elapsed          : {sw.ElapsedMilliseconds} ms (services only)");

        var swTasks = Stopwatch.StartNew();
        var tasks = SystemArtifacts.AllScheduledTasks(CancellationToken.None).ToList();
        swTasks.Stop();
        Console.WriteLine($"scheduled tasks  : {tasks.Count} (excludes \\Microsoft\\Windows\\*) in {swTasks.ElapsedMilliseconds} ms");
        foreach (var task in tasks.Take(10))
        {
            Console.WriteLine($"  {Trim(task.Name, 44),-44} {Trim(task.Action, 44)}");
        }

        Console.WriteLine();
        Console.WriteLine("SELFTEST OK");
        return 0;
    }

    /// <summary>Exercises the Store/MSIX app enumeration.</summary>
    private static int DumpStoreApps()
    {
        var sw = Stopwatch.StartNew();
        var apps = WindowsApps.Scan();
        sw.Stop();

        Console.WriteLine("== Windows Apps (Store / MSIX) ==");
        Console.WriteLine($"count   : {apps.Count}");
        Console.WriteLine($"elapsed : {sw.ElapsedMilliseconds} ms");
        Console.WriteLine();

        foreach (var app in apps.Take(25))
        {
            Console.WriteLine($"  {Trim(app.Name, 34),-34} {Trim(app.Version, 12),-12} {Trim(app.PublisherDisplay, 30),-30} {(app.IsFramework ? "framework" : "")}");
        }

        Console.WriteLine();
        Console.WriteLine("SELFTEST OK");
        return apps.Count > 0 ? 0 : 4;
    }

    /// <summary>Prints the derived search tokens for every installed program.</summary>
    private static int DumpTokens()
    {
        var apps = RegistryScanner.Scan();
        int empty = 0;

        foreach (var app in apps)
        {
            var tokens = NameTokens.BuildTokenSet(app);
            if (tokens.Strong.Count == 0)
            {
                empty++;
            }

            Console.WriteLine($"  {Trim(app.DisplayName, 38),-38} -> [{string.Join(", ", tokens.Strong)}]");
        }

        Console.WriteLine();
        Console.WriteLine($"programs      : {apps.Count}");
        Console.WriteLine($"no tokens     : {empty}");
        return 0;
    }

    /// <summary>
    /// Exercises command parsing and silent-flag selection across every uninstall string on
    /// the machine. This is the part most likely to be quietly wrong for a given app.
    /// </summary>
    private static int DumpUninstallers()
    {
        var apps = RegistryScanner.Scan();
        int parsed = 0, silent = 0, unknown = 0, missingExe = 0;
        var families = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in apps)
        {
            var plan = UninstallRunner.BuildPlan(app, preferSilent: true);
            if (plan is null)
            {
                Console.WriteLine($"  !! NO PLAN   {Trim(app.DisplayName, 30)}  raw={app.UninstallString}");
                continue;
            }

            parsed++;
            families[UninstallRunner.Describe(plan.Family)] =
                families.GetValueOrDefault(UninstallRunner.Describe(plan.Family)) + 1;

            if (plan.Silent)
            {
                silent++;
            }

            if (plan.Family == InstallerFamily.Unknown)
            {
                unknown++;
            }

            bool exists = File.Exists(plan.FileName) ||
                          (!string.IsNullOrWhiteSpace(plan.FileName) &&
                           !Path.IsPathRooted(plan.FileName) &&
                           CommandLine.ResolveExecutable(plan.FileName) == plan.FileName
                               ? File.Exists(plan.FileName)
                               : true);
            if (!exists)
            {
                missingExe++;
            }

            Console.WriteLine(
                $"  {UninstallRunner.Describe(plan.Family),-18} silent={plan.Silent,-5} exe={(exists ? "ok" : "MISSING"),-7} {Trim(plan.FileName, 46),-46} {Trim(plan.Arguments, 40)}");
        }

        Console.WriteLine();
        Console.WriteLine($"total programs    : {apps.Count}");
        Console.WriteLine($"plans built       : {parsed}");
        Console.WriteLine($"silent applied    : {silent}");
        Console.WriteLine($"unknown family    : {unknown}");
        Console.WriteLine($"exe not on disk   : {missingExe}");
        Console.WriteLine("families:");
        foreach (var family in families.OrderByDescending(f => f.Value))
        {
            Console.WriteLine($"  {family.Key,-18} {family.Value}");
        }

        return 0;
    }

    private static InstalledApp? PickTarget(List<InstalledApp> apps, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return apps.FirstOrDefault(a =>
                a.DisplayName.Contains(requested, StringComparison.OrdinalIgnoreCase) ||
                a.Publisher.Contains(requested, StringComparison.OrdinalIgnoreCase));
        }

        // Prefer something small, per-user and with a real install folder: fast to scan,
        // and it exercises the same code paths as a heavyweight system app.
        return apps
            .Where(a => a.HasUninstaller)
            .OrderBy(a => a.EstimatedSizeBytes == 0 ? int.MaxValue : (int)a.EstimatedSizeBytes)
            .FirstOrDefault(a => a.IsPerUser)
        ?? apps.FirstOrDefault(a => a.HasUninstaller)
        ?? apps.FirstOrDefault();
    }

    private static void PrintEnvironment()
    {
        Console.WriteLine("== Environment ==");
        Console.WriteLine($"process arch : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"OS arch      : {RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"framework    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS           : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"elevated     : {Program.IsElevated}");
        Console.WriteLine($"working dir  : {Environment.CurrentDirectory}");
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..].Trim('"');
            }

            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1].Trim('"');
            }
        }

        return null;
    }

    private static string Trim(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..(max - 3)] + "...";

    /// <summary>
    /// Progress&lt;T&gt; marshals through the synchronisation context, which does not exist in a
    /// console run. This reports inline so output stays ordered.
    /// </summary>
    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public InlineProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}

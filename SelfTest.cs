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

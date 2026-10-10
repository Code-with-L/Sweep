using System.Security.Principal;

namespace Sweep;

internal static class Program
{
    internal const string SelftestFlag = "--selftest";
    private const string NoElevateFlag = "--no-elevate";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, SelftestFlag, StringComparison.OrdinalIgnoreCase)))
        {
            return SelfTest.Run(args).GetAwaiter().GetResult();
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // --ui-probe=<program> opens the leftovers pane for a named program. Used for
        // automated UI screenshots during development.
        string? probe = args
            .FirstOrDefault(a => a.StartsWith("--ui-probe=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1];

        // Self-elevate so HKLM writes and Program Files deletes are possible.
        if (!IsElevated && !args.Contains(NoElevateFlag, StringComparer.OrdinalIgnoreCase))
        {
            if (TryElevate(args))
            {
                return 0;
            }
        }

        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        var theme = SettingsStore.Load().Theme;
        Theme.ApplyMode(theme);

        Application.Run(new MainForm(probe));
        return 0;
    }

    internal static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool TryElevate(string[] args)
    {
        try
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            psi.ArgumentList.Add(NoElevateFlag);
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the UAC prompt.
            MessageBox.Show(
                "Administrator rights are needed to remove programs from Program Files and the HKLM registry hive.\r\n\r\n" +
                "Sweep will continue, but some removals will fail.",
                "Sweep",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not request administrator rights:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private static void ReportCrash(Exception? ex)
    {
        if (ex is null)
        {
            return;
        }

        try
        {
            LogStore.Error("Unhandled exception", ex);
            MessageBox.Show(
                $"An unexpected error occurred:\r\n\r\n{ex.Message}\r\n\r\nDetails were written to the log.",
                "Sweep - Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // Never throw from the crash handler.
        }
    }
}
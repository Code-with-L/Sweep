using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace Sweep;

public enum InstallerFamily
{
    Unknown,
    InnoSetup,
    Nsis,
    InstallShield,
    Msi,
    Squirrel,
    InstallAware,
}

public sealed record UninstallPlan(
    string FileName,
    string Arguments,
    bool Silent,
    InstallerFamily Family,
    string Note);

public sealed record UninstallOutcome(
    bool Launched,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? FailureReason)
{
    public string Summary => Launched
        ? $"Exit code {ExitCode}"
        : $"Did not start: {FailureReason}";
}

/// <summary>
/// Runs an application's registered uninstall command, adding vendor-appropriate
/// silent switches where they are known and safe.
/// </summary>
public static class UninstallRunner
{
    private static readonly TimeSpan RegistrySettleTimeout = TimeSpan.FromSeconds(45);

    public static InstallerFamily DetectFamily(string executable)
    {
        string name = Path.GetFileName(executable);
        string lower = name.ToLowerInvariant();

        if (lower.StartsWith("unins", StringComparison.Ordinal))
        {
            return InstallerFamily.InnoSetup;
        }

        if (lower is "uninstall.exe" or "uninst.exe" or "uninstaller.exe")
        {
            return InstallerFamily.Nsis;
        }

        if (lower.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerFamily.Msi;
        }

        if (lower.Equals("update.exe", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerFamily.Squirrel;
        }

        if (lower.StartsWith("_ai_", StringComparison.OrdinalIgnoreCase) ||
            lower.StartsWith("_is_", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerFamily.InstallAware;
        }

        if (lower is "setup.exe" || lower.StartsWith("setup_", StringComparison.Ordinal))
        {
            return InstallerFamily.InstallShield;
        }

        return InstallerFamily.Unknown;
    }

    public static UninstallPlan? BuildPlan(InstalledApp app, bool preferSilent)
    {
        if (!app.HasUninstaller)
        {
            return null;
        }

        string raw = app.UninstallString;
        bool silent = false;
        string note = "";

        if (preferSilent && app.HasQuietUninstaller)
        {
            raw = app.QuietUninstallString;
            silent = true;
            note = "Using the vendor's QuietUninstallString.";
        }

        var parts = CommandLine.Split(raw);
        string file = parts.FileName.Trim().Trim('"');
        string args = parts.Arguments;

        if (file.Length == 0)
        {
            return null;
        }

        file = CommandLine.ResolveExecutable(file);

        // "rundll32.exe msiexec.dll,ProductCode {GUID}" -> a first-class MSI uninstall.
        if (file.EndsWith("rundll32.exe", StringComparison.OrdinalIgnoreCase) &&
            args.Contains("ProductCode", StringComparison.OrdinalIgnoreCase))
        {
            string? guid = ExtractGuid(args);
            if (guid is not null)
            {
                file = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
                args = $"/X {guid}";
                silent = preferSilent;
                note = "Converted rundll32 MSI invocation to msiexec.";
            }
        }

        InstallerFamily family = DetectFamily(file);

        if (preferSilent)
        {
            IReadOnlyList<SwitchSpec>? specs = SilentSwitchesFor(family, file, args);
            if (specs is not null && specs.Count > 0)
            {
                args = CommandLine.ApplySwitches(args, specs);
                silent = true;
                if (note.Length == 0)
                {
                    note = $"Applied known silent switches for {Describe(family)}.";
                }
            }
            else if (note.Length == 0)
            {
                note = $"Uninstaller type not recognised ({Describe(family)}) - running with its normal arguments.";
            }
        }

        return new UninstallPlan(file, args, silent, family, note);
    }

    private static string? ExtractGuid(string args)
    {
        foreach (var part in args.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = part.Trim('"', '\'');
            if (candidate.Length == 38 && candidate[0] == '{' && candidate[^1] == '}' &&
                Guid.TryParse(candidate, out _))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<SwitchSpec>? SilentSwitchesFor(InstallerFamily family, string file, string existingArgs)
    {
        switch (family)
        {
            case InstallerFamily.InnoSetup:
                return
                [
                    // /SILENT and /VERYSILENT are alternatives; never send both.
                    new SwitchSpec("/VERYSILENT", ["/VERYSILENT", "/SILENT"]),
                    new SwitchSpec("/SUPPRESSMSGBOXES", ["/SUPPRESSMSGBOXES"]),
                    new SwitchSpec("/NORESTART", ["/NORESTART"]),
                    new SwitchSpec("/SP-", ["/SP-"]),
                ];

            case InstallerFamily.Nsis:
                return [new SwitchSpec("/S", ["/S"])];

            case InstallerFamily.InstallShield:
                return [new SwitchSpec("/s /v\"/qn REBOOT=ReallySuppress\"", ["/s", "/silent"])];

            case InstallerFamily.Msi:
                return
                [
                    new SwitchSpec("/qn", ["/qn", "/quiet"]),
                    new SwitchSpec("/norestart", ["/norestart"]),
                ];

            case InstallerFamily.Squirrel:
                return [new SwitchSpec("-s", ["-s", "--silent"])];

            default:
                // msiexec-style product codes can appear on an unknown host executable.
                return ExtractGuid(existingArgs) is not null
                    ? [new SwitchSpec("/qn /norestart", ["/qn", "/quiet"])]
                    : null;
        }
    }

    public static string Describe(InstallerFamily family) => family switch
    {
        InstallerFamily.InnoSetup => "Inno Setup",
        InstallerFamily.Nsis => "NSIS",
        InstallerFamily.InstallShield => "InstallShield",
        InstallerFamily.Msi => "Windows Installer",
        InstallerFamily.Squirrel => "Squirrel",
        InstallerFamily.InstallAware => "InstallAware",
        _ => "unknown installer",
    };

    public static async Task<UninstallOutcome> RunAsync(
        UninstallPlan plan,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = plan.FileName,
            Arguments = plan.Arguments,
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = Path.GetDirectoryName(plan.FileName) is { Length: > 0 } dir
                ? dir
                : Environment.CurrentDirectory,
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stdout.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderr.AppendLine(e.Data);
                }
            };

            if (!process.Start())
            {
                return new UninstallOutcome(false, -1, stdout.ToString(), stderr.ToString(), "Process.Start returned false");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return new UninstallOutcome(true, process.ExitCode, stdout.ToString(), stderr.ToString(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UninstallOutcome(false, -1, stdout.ToString(), stderr.ToString(), ex.Message);
        }
    }

    /// <summary>
    /// Many uninstallers re-launch themselves elevated and exit immediately, so the parent
    /// process going away does not mean the uninstall is finished. Wait for the registry
    /// entry to disappear instead.
    /// </summary>
    public static async Task<bool> WaitForEntryRemovalAsync(InstalledApp app, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + RegistrySettleTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!RegistryScanner.EntryExists(app.Location))
            {
                return true;
            }

            try
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>True when the registry entry lingers after the uninstaller exited successfully.</summary>
    public static bool IsStaleEntry(InstalledApp app) => RegistryScanner.EntryExists(app.Location);
}
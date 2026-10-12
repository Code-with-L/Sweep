using System.Text;
using Sweep;

namespace Sweep;

/// <summary>What happened to one program during a batch removal.</summary>
internal sealed record QuickUninstallOutcome(
    string Name,
    string Status,
    int ExitCode,
    int FilesRemoved,
    long BytesReclaimed);

/// <summary>
/// Quick Uninstall: run each selected program's own uninstaller, then clean up after it.
///
/// Revo does this silently and deletes leftovers in the background. This does the same work
/// but confirms the batch first, and puts every deletion behind the safety checks and the
/// quarantine that the single-program path already uses. Silent deletion is the one part of
/// Revo's behaviour worth not copying: a batch is exactly the situation where a wrong
/// selection does the most damage, and it is the situation where the user is least likely to
/// be watching.
/// </summary>
internal sealed class QuickUninstallRunner
{
    private readonly AppSettings _settings;
    private readonly Action<string, bool> _status;
    private readonly Action<bool> _busy;
    private readonly Func<string> _progressText;

    public QuickUninstallRunner(
        AppSettings settings,
        Action<string> status,
        Action<bool> busy,
        Func<string> progressText)
    {
        _settings = settings;
        _status = (text, _) => status(text);
        _busy = busy;
        _progressText = progressText;
    }

    /// <summary>
    /// Asks the user to confirm the batch before anything is removed.
    ///
    /// Returns false if they decline.
    /// </summary>
    public static bool Confirm(IReadOnlyList<InstalledApp> apps, AppSettings settings)
    {
        var text = new StringBuilder();
        text.AppendLine($"{apps.Count} program(s) are selected for removal:");
        text.AppendLine();

        foreach (InstalledApp app in apps)
        {
            string kind = app.HasUninstaller ? "will run its own uninstaller" : "has no uninstaller - scan only";
            text.AppendLine($"  - {app.DisplayName}  ({kind})");
        }

        text.AppendLine();
        text.AppendLine("For each one Sweep will:");
        text.AppendLine("  1. run its registered uninstall command");
        text.AppendLine("  2. scan for leftover files, folders and registry entries");
        text.AppendLine("  3. move leftovers to quarantine so they can be put back");

        if (settings.CreateRestorePoint)
        {
            text.AppendLine();
            text.AppendLine("A system restore point is created once, before the first program.");
        }

        text.AppendLine();
        text.AppendLine("Continue?");

        var answer = MessageBox.Show(
            text.ToString(), "Sweep - Quick Uninstall", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        return answer == DialogResult.Yes;
    }

    public async Task<List<QuickUninstallOutcome>> RunAsync(
        IReadOnlyList<InstalledApp> apps,
        CancellationToken token)
    {
        var results = new List<QuickUninstallOutcome>();

        // The restore point is created once for the whole batch, not per program: doing it
        // per program is slow and each one wipes the previous one's rollback.
        if (_settings.CreateRestorePoint && apps.Count > 0)
        {
            SystemRestore.TryCreate("Sweep Quick Uninstall", out string restoreMessage);
            _status(restoreMessage, false);
        }

        for (int i = 0; i < apps.Count; i++)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            InstalledApp app = apps[i];
            _busy(true);
            _progressText();
            _status($"Removing {app.DisplayName} ({i + 1} of {apps.Count})...", false);

            QuickUninstallOutcome outcome = await RemoveOneAsync(app, token);
            results.Add(outcome);

            _status($"{outcome.Name}: {outcome.Status}", outcome.ExitCode == 0);
        }

        _busy(false);
        return results;
    }

    private async Task<QuickUninstallOutcome> RunVendorAsync(InstalledApp app, CancellationToken token)
    {
        UninstallPlan? plan = UninstallRunner.BuildPlan(app, _settings.SilentUninstall);
        if (plan is null)
        {
            return new QuickUninstallOutcome(app.DisplayName, "no usable uninstall command", -1, 0, 0);
        }

        try
        {
            UninstallOutcome outcome = await UninstallRunner.RunAsync(plan, token);
            if (!outcome.Launched)
            {
                return new QuickUninstallOutcome(
                    app.DisplayName, $"uninstaller failed: {outcome.FailureReason}", -1, 0, 0);
            }

            if (outcome.ExitCode == 0)
            {
                await UninstallRunner.WaitForEntryRemovalAsync(app, token);
            }

            return new QuickUninstallOutcome(app.DisplayName, "uninstalled", outcome.ExitCode, 0, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogStore.Error($"Uninstaller for \"{app.DisplayName}\" threw", ex);
            return new QuickUninstallOutcome(app.DisplayName, $"uninstaller error: {ex.Message}", -1, 0, 0);
        }
    }

    private async Task<QuickUninstallOutcome> RemoveOneAsync(InstalledApp app, CancellationToken token)
    {
        QuickUninstallOutcome vendor = app.HasUninstaller
            ? await RunVendorAsync(app, token)
            : new QuickUninstallOutcome(app.DisplayName, "no uninstaller registered - scan only", 0, 0, 0);

        // The scan runs whether or not the vendor uninstaller worked. A failed uninstall is
        // precisely when leftovers are most likely.
        _status($"Scanning for leftovers of {app.DisplayName}...", false);

        int removed = 0;
        long bytes = 0;
        Quarantine? quarantine = _settings.UseQuarantine ? new Quarantine(app.DisplayName) : null;

        try
        {
            var options = ScanOptions.FromSettings(_settings);
            var scanner = new ResidueScanner(app, options);
            List<ResidueItem> items = await scanner.ScanAsync(null, token);

            // Only items the safety layer considers removable. Anything doubtful is left for
            // a human to look at, which is the whole point of the single-program path and
            // the reason this is not simply "delete everything that matched".
            foreach (ResidueItem item in items.Where(IsSafeToRemove))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                DeletionResult result = Removal.Execute(item, quarantine);
                if (result.Success)
                {
                    removed++;
                    bytes += item.SizeBytes;
                }
                else
                {
                    LogStore.Warn($"Could not remove {item.Path}: {result.Error}");
                }
            }

            // Anything we touched is now accounted for, interrupted or not.
            PendingUninstallStore.Clear();
        }
        catch (OperationCanceledException)
        {
            RecordInterrupted(app, vendor, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            LogStore.Error($"Leftover scan for \"{app.DisplayName}\" failed", ex);
            RecordInterrupted(app, vendor, $"scan failed: {ex.Message}");
            return new QuickUninstallOutcome(app.DisplayName, $"{vendor.Status}; scan failed", vendor.ExitCode, 0, 0);
        }

        string status = removed == 0
            ? $"{vendor.Status}; nothing left to remove"
            : $"{vendor.Status}; {removed} leftover(s) quarantined";

        return new QuickUninstallOutcome(app.DisplayName, status, vendor.ExitCode, removed, bytes);
    }

    /// <summary>
    /// Whether an item is safe to remove without a human reading it.
    ///
    /// Mirrors what the leftover window would have offered by default: confidently-matched
    /// items only. A batch removes things while nobody is watching, so the bar is set at
    /// what is safe unattended, not at what is safe to offer.
    /// </summary>
    private static bool IsSafeToRemove(ResidueItem item)
    {
        if (item.Confidence > Confidence.Moderate)
        {
            return false;
        }

        // Anything the scanner itself flagged is off-limits regardless of confidence.
        if (!string.IsNullOrEmpty(item.Note) &&
            item.Note.Contains("protected", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Notes an unfinished removal so it can be offered again next time.
    ///
    /// Without this a batch that stopped halfway leaves the rest of the machine's leftovers
    /// with nobody looking at them.
    /// </summary>
    private static void RecordInterrupted(InstalledApp app, QuickUninstallOutcome vendor, string reason) =>
        PendingUninstallStore.Save(new PendingUninstall(
            app.DisplayName,
            app.UninstallString,
            app.InstallLocation,
            app.Publisher,
            DateTime.UtcNow,
            string.IsNullOrEmpty(vendor.Status) ? reason : $"{reason} ({vendor.Status})"));

    /// <summary>Human-readable end-of-batch report.</summary>
    public static string Summarise(IReadOnlyList<QuickUninstallOutcome> results)
    {
        if (results.Count == 0)
        {
            return "Nothing was removed.";
        }

        long bytes = results.Sum(r => r.BytesReclaimed);
        int removed = results.Sum(r => r.FilesRemoved);

        var text = new StringBuilder();
        text.AppendLine($"{results.Count} program(s) processed.");

        foreach (QuickUninstallOutcome outcome in results)
        {
            text.AppendLine($"  {outcome.Name}: {outcome.Status}");
        }

        text.AppendLine();
        text.AppendLine($"{removed} leftover item(s) moved to quarantine.");

        if (bytes > 0)
        {
            text.AppendLine(bytes switch
        {
            <= 0 => string.Empty,
            < 1024L * 1024 => $"{bytes / 1024d:0.#} KB freed.",
            < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB freed.",
            _ => $"{bytes / (1024d * 1024 * 1024):0.##} GB freed.",
        });
        }

        if (results.Any(r => r.ExitCode != 0))
        {
            text.AppendLine();
            text.AppendLine("Some uninstallers reported a problem. Check the activity log for details.");
        }

        return text.ToString();
    }
}
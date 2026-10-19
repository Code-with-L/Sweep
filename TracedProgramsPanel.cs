using System.Diagnostics;

namespace Sweep;

/// <summary>
/// Traced Programs: run an installer once, see exactly what it added.
///
/// The trace is a snapshot-diff, not a live monitor. It catches new uninstall entries, new
/// top-level program/data folders and new software registry keys, which covers the ordinary
/// installer. It cannot see something written and deleted inside the same run, and it is
/// honest about that in the UI rather than pretending to be a filter driver.
/// </summary>
internal sealed class TracedProgramsPanel : Panel
{
    private readonly ListView _list = new();
    private readonly Button _trace = new();
    private readonly Button _selectAll = new();
    private readonly Button _remove = new();
    private readonly Label _status = new();

    private List<ResidueItem> _items = [];
    private IReadOnlyList<InstalledApp> _newPrograms = [];
    private string _traceName = "traced installation";
    private bool _busy;

    public TracedProgramsPanel()
    {
        var p = Theme.Current;

        Dock = DockStyle.Fill;
        BackColor = p.Background;
        ForeColor = p.Text;

        var intro = new Label
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(16, 10, 16, 6),
            ForeColor = p.TextDim,
            Text = "Choose an installer. Sweep snapshots the machine, runs it, and lists the " +
                   "folders and registry keys that appeared. Results stay until you close the window.",
        };

        Native.EnableDoubleBuffer(_list);
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.CheckBoxes = true;
        _list.FullRowSelect = true;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.ShowItemToolTips = true;
        _list.Columns.Add("Type", 110);
        _list.Columns.Add("Name", 240);
        _list.Columns.Add("Size", 100, HorizontalAlignment.Right);
        _list.Columns.Add("Confidence", 100);
        _list.Columns.Add("Path", 420);
        Theme.ApplyToList(_list, p);

        _list.SizeChanged += (_, _) => FitColumns();
        _list.ItemChecked += (_, _) => UpdateStatus();

        _trace.Text = "Trace an installation...";
        _selectAll.Text = "Select all";
        _remove.Text = "Remove selected";

        foreach (Button button in new[] { _trace, _selectAll, _remove })
        {
            button.Height = 32;
            button.Width = Math.Max(110, TextRenderer.MeasureText(button.Text, button.Font).Width + 44);
            Theme.StyleButton(button, p);
        }

        Theme.StyleButton(_trace, p, primary: true);

        _trace.Click += async (_, _) => await TraceAsync();
        _selectAll.Click += (_, _) => ToggleAll();
        _remove.Click += async (_, _) => await RemoveAsync();

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = p.TextDim;
        _status.Padding = new Padding(12, 0, 0, 0);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = p.Surface,
            Padding = new Padding(12, 9, 12, 9),
        };

        bar.Controls.Add(_trace);
        bar.Controls.Add(_selectAll);
        bar.Controls.Add(_remove);
        bar.Controls.Add(_status);

        Controls.Add(_list);
        Controls.Add(intro);
        Controls.Add(bar);

        _status.Text = "No trace yet.";
        FitColumns();
        UpdateButtons();
    }

    private async Task TraceAsync()
    {
        if (_busy)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Choose the installer to trace",
            Filter = "Installers (*.exe;*.msi;*.bat;*.cmd)|*.exe;*.msi;*.bat;*.cmd|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string installer = dialog.FileName;
        _traceName = Path.GetFileNameWithoutExtension(installer);

        var confirm = MessageBox.Show(this,
            $"Sweep will snapshot the machine, then run:\r\n\r\n{installer}\r\n\r\n" +
            "Install the program normally. When the installer closes, Sweep asks you to " +
            "confirm the installation is finished, then lists what appeared.",
            "Sweep - Trace an installation",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        SetBusy(true, "Snapshotting the machine before the install...");

        try
        {
            var progress = new Progress<string>(text => _status.Text = text);
            TraceSnapshot before = await TraceSnapshot.CaptureAsync(progress, CancellationToken.None);

            _status.Text = "Starting the installer...";
            Process? process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = installer,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(installer) ?? Environment.CurrentDirectory,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"The installer could not be started:\r\n{ex.Message}", "Sweep",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (process is not null)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        process.WaitForExit(45 * 60 * 1000);
                    }
                    catch
                    {
                        // A launcher that hands off to another process may exit early.
                    }
                });
            }

            var finished = MessageBox.Show(this,
                "The installer has exited.\r\n\r\n" +
                "Click OK once the installation is completely finished (including any first-run " +
                "or sign-in screens), or Cancel to abandon the trace.",
                "Sweep - Trace an installation",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);

            if (finished != DialogResult.OK)
            {
                _status.Text = "Trace cancelled.";
                return;
            }

            SetBusy(true, "Snapshotting the machine after the install...");

            TraceSnapshot after = await TraceSnapshot.CaptureAsync(progress, CancellationToken.None);
            TraceDiff diff = after.Compare(before);

            _items = diff.Removable.ToList();
            _newPrograms = diff.NewPrograms;
            Populate();

            string programs = diff.NewPrograms.Count == 0
                ? "No new uninstall entry was registered."
                : "New program(s): " + string.Join(", ",
                    diff.NewPrograms.Take(5).Select(app => app.DisplayName)) +
                  (diff.NewPrograms.Count > 5 ? " and more" : string.Empty);

            _status.Text = $"{_items.Count} removable item(s). {programs}";
            LogStore.Success($"Traced \"{_traceName}\": {diff.NewPrograms.Count} program(s), {_items.Count} item(s).");
        }
        catch (Exception ex)
        {
            LogStore.Error("Trace failed", ex);
            _status.Text = "Trace failed: " + ex.Message;
            MessageBox.Show(this, $"The trace failed:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void Populate()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();

            // Programs are informational: the uninstall entry is not the thing to delete, its
            // files and keys are, and those already appear as removable rows.
            foreach (InstalledApp app in _newPrograms)
            {
                var item = new ListViewItem("Program") { Tag = null };
                item.SubItems.Add(app.DisplayName);
                item.SubItems.Add(app.SizeDisplay);
                item.SubItems.Add("-");
                item.SubItems.Add(app.Location.ToString());
                item.ToolTipText = "New uninstall entry registered by this install.";
                item.ForeColor = Theme.Current.Accent;
                _list.Items.Add(item);
            }

            foreach (ResidueItem residue in _items)
            {
                var item = new ListViewItem(DescribeKind(residue.Kind)) { Tag = residue };
                item.SubItems.Add(residue.DisplayName);
                item.SubItems.Add(residue.SizeDisplay);
                item.SubItems.Add(residue.Confidence.ToString());
                item.SubItems.Add(residue.Path);
                item.ToolTipText = residue.Note ?? residue.Path;
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        UpdateStatus();
        UpdateButtons();
    }

    private void UpdateStatus()
    {
        int removable = _items.Count;
        int checkedCount = CheckedItems().Count;
        long bytes = CheckedItems().Sum(item => item.SizeBytes);

        _status.Text = removable == 0
            ? "Nothing removable was added by this install."
            : $"{checkedCount} of {removable} removable item(s) selected, " +
              $"{SizeCalculator.Format(bytes)}.";
    }

    private List<ResidueItem> CheckedItems() =>
        _list.Items
            .Cast<ListViewItem>()
            .Where(item => item.Checked && item.Tag is ResidueItem)
            .Select(item => (ResidueItem)item.Tag!)
            .ToList();

    private static string DescribeKind(ResidueKind kind) => kind switch
    {
        ResidueKind.Folder => "Folder",
        ResidueKind.File => "File",
        ResidueKind.RegistryKey => "Registry key",
        _ => kind.ToString(),
    };

    private void ToggleAll()
    {
        bool target = _list.Items.Cast<ListViewItem>().Any(item => item.Tag is ResidueItem && !item.Checked);

        _list.BeginUpdate();
        try
        {
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is ResidueItem)
                {
                    item.Checked = target;
                }
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        UpdateStatus();
    }

    private async Task RemoveAsync()
    {
        if (_busy)
        {
            return;
        }

        List<ResidueItem> selected = CheckedItems().Where(item => item.Target is not null).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one folder or registry key to remove.", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        long bytes = selected.Sum(item => item.SizeBytes);

        var answer = MessageBox.Show(this,
            $"Move {selected.Count} item(s) to quarantine?\r\n\r\n" +
            $"{SizeCalculator.Format(bytes)} will be moved aside.\r\n\r\n" +
            "Installed program(s) themselves are not uninstalled here - use their uninstaller " +
            "from All Programs for that.",
            "Sweep - Traced Programs",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        _busy = true;
        UseWaitCursor = true;
        UpdateButtons();

        try
        {
            var result = await Task.Run(() =>
            {
                var quarantine = new Quarantine(_traceName);
                int removed = 0;
                int failed = 0;
                var errors = new List<string>();
                var succeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (ResidueItem item in selected)
                {
                    DeletionResult deletion = Removal.Execute(item, quarantine);
                    if (deletion.Success)
                    {
                        removed++;
                        succeeded.Add(item.Path);
                    }
                    else
                    {
                        failed++;
                        if (errors.Count < 12)
                        {
                            errors.Add($"{item.Path}: {deletion.Error}");
                        }
                    }
                }

                bool manifestOk = quarantine.Count == 0 || quarantine.SaveManifest(out _);
                return (Removed: removed, Failed: failed, Errors: errors, ManifestOk: manifestOk,
                        Succeeded: succeeded, Root: quarantine.Root);
            });

            _items = _items.Where(item => !result.Succeeded.Contains(item.Path)).ToList();
            LogStore.Success($"Traced removal: {result.Removed} item(s) quarantined.");

            var message = new System.Text.StringBuilder();
            message.AppendLine($"Moved {result.Removed} item(s) to quarantine.");
            message.AppendLine($"Backup: {result.Root}");

            if (!result.ManifestOk)
            {
                message.AppendLine();
                message.AppendLine("WARNING: the manifest could not be written, so these items cannot be restored automatically.");
            }

            if (result.Failed > 0)
            {
                message.AppendLine();
                message.AppendLine($"{result.Failed} item(s) could not be removed:");
                foreach (string error in result.Errors.Take(6))
                {
                    message.AppendLine("  " + error);
                }
            }

            MessageBox.Show(this, message.ToString(), "Sweep - Traced Programs",
                MessageBoxButtons.OK,
                result.Failed > 0 || !result.ManifestOk
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);

            Populate();
        }
        catch (Exception ex)
        {
            LogStore.Error("Traced removal failed", ex);
            MessageBox.Show(this, $"Removal failed:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            UpdateButtons();
        }
    }

    private void SetBusy(bool busy, string? text)
    {
        _busy = busy;
        UseWaitCursor = busy;
        if (text is not null)
        {
            _status.Text = text;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _trace.Enabled = !_busy;
        _selectAll.Enabled = !_busy && _items.Count > 0;
        _remove.Enabled = !_busy && _items.Count > 0;
    }

    private void FitColumns()
    {
        if (_list.Columns.Count < 5)
        {
            return;
        }
        int fixedWidth =
            _list.Columns[0].Width +
            _list.Columns[1].Width +
            _list.Columns[2].Width +
            _list.Columns[3].Width;

        _list.Columns[4].Width = Math.Max(220, _list.ClientSize.Width - fixedWidth );
    }
}

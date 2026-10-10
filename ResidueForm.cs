namespace Sweep;

internal sealed class ResidueForm : Form
{
    private readonly InstalledApp _app;
    private readonly ScanOptions _options;
    private readonly ListView _list = new();
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Visible = false };
    private readonly Button _removeButton = new();
    private readonly Button _cancelScanButton = new();
    private readonly Button _selectAllButton = new();
    private readonly Button _selectNoneButton = new();
    private readonly Button _invertButton = new();

    private CancellationTokenSource? _cts;

    public ResidueForm(InstalledApp app, ScanOptions options)
    {
        _app = app;
        _options = options;

        Text = $"Leftovers - {app.DisplayName}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1080, 620);
        MinimumSize = new Size(760, 440);
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = BuildHeader();
        BuildList();
        var bottom = BuildBottomBar();
        var strip = BuildStatusStrip();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(bottom, 0, 2);
        root.Controls.Add(strip, 0, 3);

        Controls.Add(root);

        Shown += async (_, _) => await RunScanAsync();
        FormClosing += (_, _) => _cts?.Cancel();

        ApplyTheme();
    }

    private void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Background;
        ForeColor = p.Text;
        Theme.ApplyToList(_list, p);
        _status.ForeColor = p.TextDim;
        Theme.StyleButton(_removeButton, p, primary: true);
        Theme.StyleButton(_cancelScanButton, p);
        Theme.StyleButton(_selectAllButton, p);
        Theme.StyleButton(_selectNoneButton, p);
        Theme.StyleButton(_invertButton, p);
    }

    private Panel BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Current.Surface, Padding = new Padding(14, 8, 14, 8) };

        var title = new Label
        {
            Text = $"Leftover items for \"{_app.DisplayName}\"",
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            ForeColor = Theme.Current.Text,
            AutoSize = true,
            Location = new Point(16, 10),
        };

        var subtitle = new Label
        {
            Text = "Review the list before removing anything. Nothing is deleted until you press Remove selected.",
            ForeColor = Theme.Current.TextDim,
            AutoSize = true,
            Location = new Point(16, 34),
        };

        panel.Controls.AddRange([title, subtitle]);
        return panel;
    }

    private void BuildList()
    {
        Native.EnableDoubleBuffer(_list);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.CheckBoxes = true;
        _list.HideSelection = false;
        _list.MultiSelect = true;
        _list.Columns.Add("Type", 110);
        _list.Columns.Add("Name", 220);
        _list.Columns.Add("Size", 90);
        _list.Columns.Add("Matched on", 110);
        _list.Columns.Add("Path", 520);

        _list.ColumnClick += (_, e) =>
        {
            if (e.Column < 1 || _list.Items.Count == 0)
            {
                return;
            }

            // Clicking the checkbox column header selects everything, matching Explorer.
            if (e.Column == 0)
            {
                bool target = _list.Items.Cast<ListViewItem>().Any(i => !i.Checked);
                _list.BeginUpdate();
                foreach (ListViewItem item in _list.Items)
                {
                    item.Checked = target;
                }

                _list.EndUpdate();
            }
        };
    }

    private Panel BuildBottomBar()
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12, 9, 12, 9) };
        panel.BackColor = Theme.Current.Surface;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Current.Surface,
        };

        _selectAllButton.Text = "Select all";
        _selectAllButton.Size = new Size(84, 32);
        _selectAllButton.Click += (_, _) => SetAllChecked(true);

        _selectNoneButton.Text = "Select none";
        _selectNoneButton.Size = new Size(92, 32);
        _selectNoneButton.Click += (_, _) => SetAllChecked(false);

        _invertButton.Text = "Invert";
        _invertButton.Size = new Size(70, 32);
        _invertButton.Click += (_, _) =>
        {
            foreach (ListViewItem item in _list.Items)
            {
                item.Checked = !item.Checked;
            }
        };

        _cancelScanButton.Text = "Stop scanning";
        _cancelScanButton.Size = new Size(112, 32);
        _cancelScanButton.Visible = false;
        _cancelScanButton.Click += (_, _) => _cts?.Cancel();

        _removeButton.Text = "Remove selected";
        _removeButton.Size = new Size(140, 32);
        _removeButton.Enabled = false;
        _removeButton.Click += async (_, _) => await RemoveSelectedAsync();

        var close = new Button { Text = "Close", Size = new Size(80, 32) };
        Theme.StyleButton(close, Theme.Current);
        close.Click += (_, _) => Close();

        flow.Controls.AddRange([_selectAllButton, _selectNoneButton, _invertButton, _cancelScanButton, _removeButton, close]);
        panel.Controls.Add(flow);
        return panel;
    }

    private StatusStrip BuildStatusStrip()
    {
        var strip = new StatusStrip { SizingGrip = false };
        strip.BackColor = Theme.Current.SurfaceAlt;
        strip.Items.Add(_status);
        strip.Items.Add(_progress);
        return strip;
    }

    private void SetAllChecked(bool value)
    {
        _list.BeginUpdate();
        foreach (ListViewItem item in _list.Items)
        {
            item.Checked = value;
        }

        _list.EndUpdate();
    }

    // ---- Scanning -------------------------------------------------------

    private async Task RunScanAsync()
    {
        _cts = new CancellationTokenSource();
        _cancelScanButton.Visible = true;
        _removeButton.Enabled = false;
        _selectAllButton.Enabled = false;
        _selectNoneButton.Enabled = false;
        _invertButton.Enabled = false;
        _progress.Visible = true;
        _progress.Style = ProgressBarStyle.Marquee;
        _status.Text = "Searching for leftovers...";
        UseWaitCursor = true;

        var progress = new Progress<ScanProgress>(report =>
        {
            _status.Text = $"{report.Stage}: {report.Detail} ({report.Matches} found)";
        });

        List<ResidueItem>? items = null;
        try
        {
            var scanner = new ResidueScanner(_app, _options);
            items = await scanner.ScanAsync(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            LogStore.Error($"Residue scan failed for \"{_app.DisplayName}\"", ex);
            _status.Text = $"Scan failed: {ex.Message}";
            MessageBox.Show(this, $"The scan failed:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Visible = false;
            _cancelScanButton.Visible = false;
            _selectAllButton.Enabled = true;
            _selectNoneButton.Enabled = true;
            _invertButton.Enabled = true;
            UseWaitCursor = false;
            _cts.Dispose();
            _cts = null;
        }

        if (items is null)
        {
            return;
        }

        Populate(items);

        if (items.Count == 0)
        {
            _status.Text = "No leftover files or registry entries were found.";
            _removeButton.Enabled = false;
        }
        else
        {
            long total = items.Sum(i => i.SizeBytes);
            _status.Text = $"{items.Count} item(s) found, {SizeCalculator.Format(total)} total.";
            _removeButton.Enabled = true;
        }

        LogStore.Info($"Residue scan for \"{_app.DisplayName}\" finished with {items.Count} item(s).");
    }

private void Populate(List<ResidueItem> items)
    {
        var p = Theme.Current;

        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var item in items.OrderByDescending(i => i.Kind == ResidueKind.RegistryKey || i.Kind == ResidueKind.RegistryValue)
                                .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
        {
            var view = new ListViewItem(item.Kind.ToString()) { Tag = item, Checked = item.SelectedByDefault };
            view.SubItems.Add(item.DisplayName);
            view.SubItems.Add(item.SizeDisplay);
            view.SubItems.Add(item.MatchedToken);
            view.SubItems.Add(item.Path);
            view.ForeColor = p.Text;
            view.ToolTipText = item.Path;
            _list.Items.Add(view);
        }

        _list.EndUpdate();
    }

    // ---- Removal --------------------------------------------------------

    private async Task RemoveSelectedAsync()
    {
        var selected = new List<(ListViewItem View, ResidueItem Item)>();
        foreach (ListViewItem view in _list.Items)
        {
            if (view.Checked && view.Tag is ResidueItem item)
            {
                selected.Add((view, item));
            }
        }

        if (selected.Count == 0)
        {
            return;
        }

        bool useQuarantine = SettingsStore.Current.UseQuarantine;
        long totalBytes = selected.Sum(i => i.Item.SizeBytes);

        var folders = selected.Count(i => i.Item.Kind == ResidueKind.Folder);
        var files = selected.Count(i => i.Item.Kind == ResidueKind.File);
        var keys = selected.Count(i => i.Item.Kind is ResidueKind.RegistryKey or ResidueKind.RegistryValue);

        var message = new System.Text.StringBuilder();
        message.AppendLine($"Remove {selected.Count} selected item(s)?");
        message.AppendLine();
        message.AppendLine($"  Folders:  {folders}");
        message.AppendLine($"  Files:    {files}");
        message.AppendLine($"  Registry: {keys}");
        message.AppendLine($"  Size:     {SizeCalculator.Format(totalBytes)}");
        message.AppendLine();
        message.AppendLine(useQuarantine
            ? "Items will be MOVED to the quarantine folder and can be restored from the Options menu."
            : "Items will be DELETED PERMANENTLY.");

        var answer = MessageBox.Show(this, message.ToString(), "Confirm removal",
            MessageBoxButtons.YesNo, useQuarantine ? MessageBoxIcon.Warning : MessageBoxIcon.Exclamation);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        _removeButton.Enabled = false;
        _cancelScanButton.Visible = true;
        _cancelScanButton.Text = "Stop";
        _progress.Visible = true;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Maximum = selected.Count;
        UseWaitCursor = true;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        LogStore.Info($"Removing {selected.Count} leftover item(s) for \"{_app.DisplayName}\" ({(useQuarantine ? "quarantine" : "permanent")}).");

        var quarantine = useQuarantine ? new Quarantine(_app.DisplayName) : null;
        var failures = new List<string>();
        var pendingReboot = new List<string>();
        int done = 0;

        await Task.Run(() =>
        {
            foreach (var (_, residue) in selected)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                DeletionResult result = residue.Kind switch
                {
                    ResidueKind.Folder => quarantine is not null
                        ? quarantine.QuarantineDirectory(residue.Path)
                        : FileOps.DeleteDirectory(residue.Path),
                    ResidueKind.File => quarantine is not null
                        ? quarantine.QuarantineFile(residue.Path)
                        : FileOps.DeleteFile(residue.Path),
                    ResidueKind.RegistryKey => RegistryResidueOps.DeleteKey(residue.Reg),
                    ResidueKind.RegistryValue => RegistryResidueOps.DeleteValue(residue.Reg, residue.ValueName ?? string.Empty),
                    _ => new DeletionResult(true, residue.Path, false, null),
                };

                if (result.ScheduledForReboot)
                {
                    pendingReboot.Add(result.Path);
                }
                else if (!result.Success)
                {
                    failures.Add($"{residue.Path}: {result.Error}");
                }

                done++;
                BeginInvoke(() =>
                {
                    _progress.Value = Math.Min(done, _progress.Maximum);
                    _status.Text = $"Removing {done} of {selected.Count}...";
                });
            }
        });

        quarantine?.SaveManifest();

        _cts.Dispose();
        _cts = null;
        _cancelScanButton.Text = "Stop scanning";

        if (_list.IsHandleCreated)
        {
            foreach (var (view, residue) in selected)
            {
                bool removed = residue.Kind is ResidueKind.RegistryKey or ResidueKind.RegistryValue
                    ? !failures.Any(f => f.StartsWith(residue.Path, StringComparison.OrdinalIgnoreCase))
                    : !Directory.Exists(residue.Path) && !File.Exists(residue.Path);

                if (removed)
                {
                    _list.Items.Remove(view);
                }
            }
        }

        _removeButton.Enabled = _list.Items.Count > 0;
        _cancelScanButton.Visible = false;
        _progress.Visible = false;
        UseWaitCursor = false;

        int removedCount = selected.Count - failures.Count - pendingReboot.Count;
        LogStore.Success($"Removed {removedCount} leftover item(s) for \"{_app.DisplayName}\".");

        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"Removed: {removedCount}");

        if (pendingReboot.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine($"{pendingReboot.Count} item(s) are locked by another process and are queued for deletion on the next restart:");
            foreach (var path in pendingReboot.Take(8))
            {
                summary.AppendLine("  " + path);
            }
        }

        if (failures.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine($"{failures.Count} item(s) could not be removed:");
            foreach (var failure in failures.Take(8))
            {
                summary.AppendLine("  " + failure);
            }
        }

        summary.AppendLine();
        summary.AppendLine($"Quarantine: {(quarantine is not null ? quarantine.Root : "not used")}");

        _status.Text = $"Removed {removedCount} of {selected.Count} item(s).";
        MessageBox.Show(this, summary.ToString(), "Sweep", MessageBoxButtons.OK,
            failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }
}
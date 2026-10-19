namespace Sweep;

/// <summary>
/// History Cleaner: browser history stores and the Windows recent-items list.
///
/// Every removal is a move into quarantine, so an accidental clean is recoverable from the
/// Backup Manager. Browsers have to be closed first; a live browser holds its history file
/// open and would simply fail the move.
/// </summary>
internal sealed class HistoryCleanerPanel : Panel
{
    private readonly ListView _list = new();
    private readonly Button _refresh = new();
    private readonly Button _selectAll = new();
    private readonly Button _clean = new();
    private readonly Label _status = new();

    private IReadOnlyList<HistoryGroup> _groups = [];
    private bool _loaded;
    private bool _busy;
    private bool _populating;

    public HistoryCleanerPanel()
    {
        var p = Theme.Current;

        Dock = DockStyle.Fill;
        BackColor = p.Background;
        ForeColor = p.Text;

        Native.EnableDoubleBuffer(_list);
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.CheckBoxes = true;
        _list.FullRowSelect = true;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.ShowItemToolTips = true;
        _list.Columns.Add("Category", 320);
        _list.Columns.Add("Items", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Size", 100, HorizontalAlignment.Right);
        _list.Columns.Add("Detail", 460);
        Theme.ApplyToList(_list, p);

        _list.ItemChecked += (_, _) =>
        {
            if (!_populating)
            {
                UpdateSummary();
            }
        };
        _list.SizeChanged += (_, _) => FitColumns();

        _refresh.Text = "Refresh";
        _selectAll.Text = "Select all";
        _clean.Text = "Clean selected";

        foreach (Button button in new[] { _refresh, _selectAll, _clean })
        {
            button.Height = 32;
            button.Width = Math.Max(96, TextRenderer.MeasureText(button.Text, button.Font).Width + 44);
            Theme.StyleButton(button, p);
        }

        Theme.StyleButton(_clean, p, primary: true);

        _refresh.Click += async (_, _) => await ScanAsync();
        _selectAll.Click += (_, _) => ToggleAll();
        _clean.Click += async (_, _) => await CleanAsync();

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

        bar.Controls.Add(_refresh);
        bar.Controls.Add(_selectAll);
        bar.Controls.Add(_clean);
        bar.Controls.Add(_status);

        Controls.Add(_list);
        Controls.Add(bar);

        _status.Text = "Not scanned yet.";
        FitColumns();
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UseWaitCursor = true;
        _refresh.Enabled = false;
        _clean.Enabled = false;
        _status.Text = "Reading history stores...";

        try
        {
            _groups = await Task.Run(() => HistoryCleaner.Scan());
            Populate();
        }
        catch (Exception ex)
        {
            LogStore.Error("Could not read history stores", ex);
            _status.Text = "Could not scan: " + ex.Message;
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            _refresh.Enabled = true;
            _clean.Enabled = true;
        }
    }

    private void Populate()
    {
        _populating = true;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();

            foreach (HistoryGroup group in _groups)
            {
                var item = new ListViewItem(group.Name) { Tag = group, Checked = group.Items > 0 };
                item.SubItems.Add(group.Items.ToString("N0"));
                item.SubItems.Add(group.SizeDisplay);
                item.SubItems.Add(group.Detail);
                item.ToolTipText = group.Detail;
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
            _populating = false;
        }

        UpdateSummary();
    }

    private List<HistoryGroup> CheckedGroups() =>
        _list.Items
            .Cast<ListViewItem>()
            .Where(item => item.Checked && item.Tag is HistoryGroup)
            .Select(item => (HistoryGroup)item.Tag!)
            .Where(group => group.Items > 0)
            .ToList();

    private void UpdateSummary()
    {
        List<HistoryGroup> checkedGroups = CheckedGroups();
        long bytes = checkedGroups.Sum(group => group.Bytes);
        int items = checkedGroups.Sum(group => group.Items);

        _status.Text = _groups.Count == 0
            ? "No history stores found."
            : $"{checkedGroups.Count} of {_groups.Count} selected, {items:N0} item(s), " +
              $"{SizeCalculator.Format(bytes)}. Cleaned items move to quarantine.";
    }

    private void ToggleAll()
    {
        bool target = _list.Items.Cast<ListViewItem>().Any(item => !item.Checked);

        _populating = true;
        _list.BeginUpdate();
        try
        {
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is HistoryGroup group && group.Items > 0)
                {
                    item.Checked = target;
                }
            }
        }
        finally
        {
            _list.EndUpdate();
            _populating = false;
        }

        UpdateSummary();
    }

    private async Task CleanAsync()
    {
        if (_busy)
        {
            return;
        }

        List<HistoryGroup> selected = CheckedGroups();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one category with something in it.", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        IReadOnlyList<string> running = HistoryCleaner.RunningBrowsers();
        if (running.Count > 0)
        {
            MessageBox.Show(this,
                "Close these browsers first:\r\n\r\n  " + string.Join("\r\n  ", running) +
                "\r\n\r\nAn open browser keeps its history file locked, so the clean would fail.",
                "Sweep - History Cleaner",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        int items = selected.Sum(group => group.Items);
        long bytes = selected.Sum(group => group.Bytes);

        var answer = MessageBox.Show(this,
            $"Move {items:N0} item(s) to quarantine?\r\n\r\n" +
            $"{SizeCalculator.Format(bytes)} across {selected.Count} category(ies).\r\n\r\n" +
            "This clears browsing history and the Windows recent-items list. Cookies, " +
            "passwords and bookmarks are not touched. Everything moved can be restored from " +
            "the Backup Manager.",
            "Sweep - History Cleaner",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        _busy = true;
        UseWaitCursor = true;
        _refresh.Enabled = false;
        _clean.Enabled = false;
        _status.Text = "Moving history to quarantine...";

        try
        {
            var result = await Task.Run(() =>
            {
                var quarantine = new Quarantine("history");
                int removed = 0;
                int failed = 0;
                var errors = new List<string>();

                foreach (HistoryGroup group in selected)
                {
                    foreach (string file in group.Files)
                    {
                        DeletionResult deletion = quarantine.QuarantineFile(file);
                        if (deletion.Success)
                        {
                            removed++;
                        }
                        else
                        {
                            failed++;
                            if (errors.Count < 20)
                            {
                                errors.Add($"{file}: {deletion.Error}");
                            }
                        }
                    }

                    foreach (string directory in group.Directories)
                    {
                        DeletionResult deletion = quarantine.QuarantineDirectory(directory);
                        if (deletion.Success)
                        {
                            removed++;
                        }
                        else
                        {
                            failed++;
                            if (errors.Count < 20)
                            {
                                errors.Add($"{directory}: {deletion.Error}");
                            }
                        }
                    }
                }

                bool manifestOk = quarantine.Count == 0 || quarantine.SaveManifest(out _);
                return (Removed: removed, Failed: failed, Errors: errors, ManifestOk: manifestOk, Root: quarantine.Root);
            });

            LogStore.Success($"History clean: {result.Removed} item(s) quarantined.");

            var message = new System.Text.StringBuilder();
            message.AppendLine($"Moved {result.Removed:N0} item(s) to quarantine.");
            message.AppendLine($"Backup: {result.Root}");

            if (!result.ManifestOk)
            {
                message.AppendLine();
                message.AppendLine("WARNING: the manifest could not be written, so these items cannot be restored automatically.");
            }

            if (result.Failed > 0)
            {
                message.AppendLine();
                message.AppendLine($"{result.Failed} item(s) could not be moved:");
                foreach (string error in result.Errors.Take(6))
                {
                    message.AppendLine("  " + error);
                }
            }

            MessageBox.Show(this, message.ToString(), "Sweep - History Cleaner",
                MessageBoxButtons.OK,
                result.Failed > 0 || !result.ManifestOk
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogStore.Error("History clean failed", ex);
            MessageBox.Show(this, $"Cleaning failed:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            _refresh.Enabled = true;
            _clean.Enabled = true;
            await ScanAsync();
        }
    }

    private void FitColumns()
    {
        if (_list.Columns.Count < 4)
        {
            return;
        }
        int fixedWidth =
            _list.Columns[0].Width + _list.Columns[1].Width + _list.Columns[2].Width;

        _list.Columns[3].Width = Math.Max(240, _list.ClientSize.Width - fixedWidth );
    }
}

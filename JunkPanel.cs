namespace Sweep;

/// <summary>
/// Junk Files Cleaner: measures the disposable locations and deletes only the ones the user
/// ticks. Nothing is removed on open, and the folder roots themselves are always kept.
/// </summary>
internal sealed class JunkPanel : Panel
{
    private readonly ListView _list = new();
    private readonly Button _scan = new();
    private readonly Button _selectAll = new();
    private readonly Button _clean = new();
    private readonly Label _status = new();

    private IReadOnlyList<JunkGroup> _groups = [];
    private bool _loaded;
    private bool _busy;
    private bool _populating;

    public JunkPanel()
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
        _list.Columns.Add("Category", 230);
        _list.Columns.Add("Files", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Size", 100, HorizontalAlignment.Right);
        _list.Columns.Add("Location", 420);
        Theme.ApplyToList(_list, p);

        _list.ItemChecked += (_, _) =>
        {
            if (!_populating)
            {
                UpdateSummary();
            }
        };

        _list.SizeChanged += (_, _) => FitColumns();

        _scan.Text = "Scan";
        _selectAll.Text = "Select all";
        _clean.Text = "Clean selected";

        foreach (Button button in new[] { _scan, _selectAll, _clean })
        {
            button.Height = 32;
            button.Width = Math.Max(96, TextRenderer.MeasureText(button.Text, button.Font).Width + 44);
            Theme.StyleButton(button, p);
        }

        Theme.StyleButton(_clean, p, primary: true);

        _scan.Click += async (_, _) => await ScanAsync();
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

        bar.Controls.Add(_scan);
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
        _scan.Enabled = false;
        _clean.Enabled = false;
        _status.Text = "Measuring temporary files...";

        try
        {
            _groups = await Task.Run(() => JunkScanner.Scan());
            Populate();
            LogStore.Info("Measured disposable files.");
        }
        catch (Exception ex)
        {
            LogStore.Error("Could not measure temporary files", ex);
            _status.Text = "Could not scan: " + ex.Message;
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            _scan.Enabled = true;
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

            foreach (JunkGroup group in _groups)
            {
                var item = new ListViewItem(group.Name) { Tag = group, Checked = group.Bytes > 0 };
                item.SubItems.Add(group.Files.ToString("N0"));
                item.SubItems.Add(group.SizeDisplay);
                item.SubItems.Add(group.LocationDisplay);
                item.ToolTipText = group.Detail;

                if (!group.Exists)
                {
                    item.ForeColor = Theme.Current.TextDim;
                }

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

    private void UpdateSummary()
    {
        List<JunkGroup> checkedGroups = CheckedGroups(group => group.Bytes > 0);
        long bytes = checkedGroups.Sum(group => group.Bytes);
        int files = checkedGroups.Sum(group => group.Files);

        _status.Text = _groups.Count == 0
            ? "Not scanned yet."
            : $"{checkedGroups.Count} of {_groups.Count} categories selected, " +
              $"{files:N0} file(s), {SizeCalculator.Format(bytes)} reclaimable.";
    }

    private List<JunkGroup> CheckedGroups(Func<JunkGroup, bool> predicate) =>
        _list.Items
            .Cast<ListViewItem>()
            .Where(item => item.Checked && item.Tag is JunkGroup)
            .Select(item => (JunkGroup)item.Tag!)
            .Where(predicate)
            .ToList();

    private void ToggleAll()
    {
        bool target = _list.Items.Cast<ListViewItem>().Any(item => !item.Checked);

        _populating = true;
        _list.BeginUpdate();
        try
        {
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is JunkGroup group && group.Bytes > 0)
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

        List<JunkGroup> selected = CheckedGroups(group => group.Bytes > 0);
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one category with something in it.", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        long bytes = selected.Sum(group => group.Bytes);
        int files = selected.Sum(group => group.Files);

        var answer = MessageBox.Show(this,
            $"Delete {files:N0} file(s) from {selected.Count} location(s)?\r\n\r\n" +
            $"{SizeCalculator.Format(bytes)} will be freed.\r\n\r\n" +
            "Temporary files are deleted permanently - they do not go to quarantine or the " +
            "Recycle Bin. Files in use are skipped. The folders themselves are kept.",
            "Sweep - Junk Files Cleaner",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        _busy = true;
        UseWaitCursor = true;
        _scan.Enabled = false;
        _clean.Enabled = false;
        _status.Text = "Deleting...";

        try
        {
            var result = await Task.Run(() => JunkScanner.Clean(selected));

            LogStore.Success(
                $"Junk clean: {result.Deleted} item(s), {SizeCalculator.Format(result.Bytes)} freed" +
                (result.Failed > 0 ? $", {result.Failed} could not be deleted." : "."));

            var message = new System.Text.StringBuilder();
            message.AppendLine($"Deleted {result.Deleted:N0} item(s), freeing {SizeCalculator.Format(result.Bytes)}.");

            if (result.Failed > 0)
            {
                message.AppendLine();
                message.AppendLine($"{result.Failed} item(s) could not be deleted (usually because they are in use):");
                foreach (string error in result.Errors.Take(6))
                {
                    message.AppendLine("  " + error);
                }
            }

            MessageBox.Show(this, message.ToString(), "Sweep - Junk Files Cleaner",
                MessageBoxButtons.OK,
                result.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogStore.Error("Junk clean failed", ex);
            MessageBox.Show(this, $"Cleaning failed:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            _scan.Enabled = true;
            _clean.Enabled = true;
        }

        // After the busy flag is clear: ScanAsync returns immediately while it is set, which
        // is why the list used to keep showing the sizes from before the clean.
        await ScanAsync();
    }

    private void FitColumns()
    {
        if (_list.Columns.Count < 4)
        {
            return;
        }
        int fixedWidth = _list.Columns[0].Width + _list.Columns[1].Width + _list.Columns[2].Width;
        _list.Columns[3].Width = Math.Max(240, _list.ClientSize.Width - fixedWidth );
    }
}

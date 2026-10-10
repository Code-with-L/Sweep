namespace Sweep;

/// <summary>Store / MSIX app inventory, which the classic program list cannot see.</summary>
internal sealed class WindowsAppsForm : Form
{
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, HideSelection = false };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Visible = false, Style = ProgressBarStyle.Marquee };
    private readonly Button _removeButton = new();
    private readonly Button _refreshButton = new();
    private readonly CheckBox _hideFrameworks = new() { Text = "Hide framework packages", AutoSize = true };
    private readonly TextBox _search = new();
    private List<WindowsApp> _all = [];

    public WindowsAppsForm()
    {
        var p = Theme.Current;

        Text = "Windows Apps (Store / MSIX)";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1000, 600);
        MinimumSize = new Size(720, 420);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = p.Background;
        ForeColor = p.Text;

        Native.EnableDoubleBuffer(_list);
        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Name", 260);
        _list.Columns.Add("Version", 90);
        _list.Columns.Add("Publisher", 220);
        _list.Columns.Add("Package", 380);

        var searchLabel = new Label { Text = "Search", AutoSize = true, Location = new Point(14, 15) };
        _search.Location = new Point(58, 11);
        _search.Size = new Size(260, 26);
        _search.TextChanged += (_, _) => Populate();

        _hideFrameworks.Checked = true;
        _hideFrameworks.CheckedChanged += (_, _) => Populate();

        var header = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = p.Surface };
        header.Controls.AddRange([searchLabel, _search, _hideFrameworks]);
        _hideFrameworks.Location = new Point(340, 14);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 14, 10),
            BackColor = p.Surface,
        };

        _removeButton.Text = "Remove selected";
        _removeButton.Size = new Size(140, 32);
        _removeButton.Enabled = false;
        _removeButton.Click += async (_, _) => await RemoveSelectedAsync();

        _refreshButton.Text = "Refresh";
        _refreshButton.Size = new Size(90, 32);
        _refreshButton.Click += async (_, _) => await LoadAsync();

        var close = new Button { Text = "Close", Size = new Size(80, 32) };
        close.Click += (_, _) => Close();

        Theme.StyleButton(_removeButton, p, primary: true);
        Theme.StyleButton(_refreshButton, p);
        Theme.StyleButton(close, p);
        Theme.StyleTextBox(_search, p);

        toolbar.Controls.Add(close);
        toolbar.Controls.Add(_removeButton);
        toolbar.Controls.Add(_refreshButton);

        var strip = new StatusStrip { SizingGrip = false };
        strip.BackColor = p.SurfaceAlt;
        strip.Items.Add(_status);
        strip.Items.Add(_progress);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(toolbar, 0, 2);
        root.Controls.Add(strip, 0, 3);

        Controls.Add(root);

        _list.SelectedIndexChanged += (_, _) => _removeButton.Enabled = _list.SelectedItems.Count > 0;
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _status.Text = "Reading Store app packages...";
        _progress.Visible = true;
        _refreshButton.Enabled = false;
        UseWaitCursor = true;

        try
        {
            _all = await Task.Run(() => WindowsApps.Scan());
            Populate();
            LogStore.Info($"Listed {_all.Count} Store/Windows apps.");
        }
        catch (Exception ex)
        {
            LogStore.Error("Could not read Store apps", ex);
            _status.Text = $"Could not read Store apps: {ex.Message}";
            MessageBox.Show(this, $"Could not read Store apps:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _progress.Visible = false;
            _refreshButton.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private void Populate()
    {
        string query = _search.Text.Trim();

        var filtered = _all
            .Where(a => !_hideFrameworks.Checked || !a.IsFramework)
            .Where(a => query.Length == 0 ||
                        a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        a.PublisherDisplay.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        var p = Theme.Current;

        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var app in filtered)
        {
            var item = new ListViewItem(app.Name) { Tag = app };
            item.SubItems.Add(app.Version);
            item.SubItems.Add(app.PublisherDisplay);
            item.SubItems.Add(app.PackageFullName);
            item.ForeColor = app.IsFramework ? p.TextDim : p.Text;
            _list.Items.Add(item);
        }

        _list.EndUpdate();

        _status.Text = $"{filtered.Count} shown of {_all.Count} Store apps.";
        _removeButton.Enabled = _list.SelectedItems.Count > 0;
    }

    private async Task RemoveSelectedAsync()
    {
        var selected = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => (View: i, App: (WindowsApp)i.Tag!))
            .ToList();

        if (selected.Count == 0)
        {
            return;
        }

        string names = string.Join(Environment.NewLine, selected.Take(10).Select(s => "  " + s.App.Name));
        var answer = MessageBox.Show(this,
            $"Remove {selected.Count} Store app(s)?{Environment.NewLine}{Environment.NewLine}{names}" +
            $"{Environment.NewLine}{Environment.NewLine}The app and its per-user data will be removed.",
            "Sweep", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        _removeButton.Enabled = false;
        _refreshButton.Enabled = false;
        _progress.Visible = true;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Maximum = selected.Count;
        UseWaitCursor = true;

        var failures = new List<string>();
        int done = 0;

        try
        {
            foreach (var (_, app) in selected)
            {
                DeletionResult result = await Task.Run(() => WindowsApps.Remove(app));
                if (!result.Success)
                {
                    failures.Add($"{app.Name}: {result.Error}");
                }

                int snapshot = ++done;
                BeginInvoke(() =>
                {
                    _progress.Value = Math.Min(snapshot, _progress.Maximum);
                    _status.Text = $"Removing {snapshot} of {selected.Count}...";
                });
            }
        }
        finally
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.Visible = false;
            _removeButton.Enabled = true;
            _refreshButton.Enabled = true;
            UseWaitCursor = false;
        }

        await LoadAsync();

        if (failures.Count > 0)
        {
            MessageBox.Show(this,
                $"{failures.Count} app(s) could not be removed:\r\n\r\n" + string.Join(Environment.NewLine, failures.Take(8)),
                "Sweep", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
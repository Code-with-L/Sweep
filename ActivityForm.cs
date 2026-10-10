namespace Sweep;

/// <summary>Shows the session activity log, which is also the record of what was changed.</summary>
internal sealed class ActivityForm : Form
{
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, HideSelection = false };

    public ActivityForm()
    {
        var p = Theme.Current;

        Text = "Activity log";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(900, 460);
        MinimumSize = new Size(640, 320);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = p.Background;
        ForeColor = p.Text;

        _list.Columns.Add("Time", 90);
        _list.Columns.Add("Level", 70);
        _list.Columns.Add("Details", 700);
        Theme.ApplyToList(_list, p);

        Reload();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 14, 8),
            BackColor = p.Background,
        };

        var openFolder = new Button { Text = "Open log folder", Size = new Size(120, 32) };
        var export = new Button { Text = "Export session...", Size = new Size(120, 32) };
        var clear = new Button { Text = "Clear view", Size = new Size(100, 32) };
        Theme.StyleButton(openFolder, p, primary: true);
        Theme.StyleButton(export, p);
        Theme.StyleButton(clear, p);

        clear.Click += (_, _) => Reload();

        export.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "Text file (*.txt)|*.txt",
                FileName = $"sweep-activity-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            try
            {
                LogStore.ExportSession(dialog.FileName);
                LogStore.Success($"Activity log exported to {dialog.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not export the log:\r\n{ex.Message}", "Sweep",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        openFolder.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = LogStore.LogDirectory,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not open the log folder:\r\n{ex.Message}", "Sweep",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        toolbar.Controls.Add(export);
        toolbar.Controls.Add(openFolder);
        toolbar.Controls.Add(clear);

        Controls.Add(_list);
        Controls.Add(toolbar);
    }

    private void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var entry in LogStore.Snapshot())
        {
            var item = new ListViewItem(entry.TimeDisplay);
            item.SubItems.Add(entry.LevelDisplay);
            item.SubItems.Add(entry.Message);
            item.ForeColor = Theme.StatusColorFor(entry.Level);
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        ResizeLastColumn();
    }

    private void ResizeLastColumn()
    {
        if (_list.Columns.Count == 0)
        {
            return;
        }

        int fixedWidth = _list.Columns.Cast<ColumnHeader>().Take(_list.Columns.Count - 1).Sum(c => c.Width);
        _list.Columns[_list.Columns.Count - 1].Width = Math.Max(200, _list.ClientSize.Width - fixedWidth - 4);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated)
        {
            ResizeLastColumn();
        }
    }
}
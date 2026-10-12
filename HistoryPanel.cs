namespace Sweep;

/// <summary>
/// Uninstall History: past removals, grouped by date, expandable to what each one did.
///
/// Grouped by date then time, matching how people actually look for something ("what did I
/// uninstall last night"), rather than a flat list that buries recent entries.
/// </summary>
internal sealed class HistoryPanel : Panel
{
    private readonly TreeView _tree = new();
    private readonly Button _export = new();
    private readonly Button _refresh = new();
    private readonly Label _status = new();

    public HistoryPanel()
    {
        var p = Theme.Current;

        Dock = DockStyle.Fill;
        BackColor = p.Background;
        ForeColor = p.Text;

        Native.EnableDoubleBuffer(_tree);
        _tree.Dock = DockStyle.Fill;
        _tree.BackColor = p.Surface;
        _tree.ForeColor = p.Text;
        _tree.BorderStyle = BorderStyle.None;
        _tree.ShowLines = false;
        _tree.ShowRootLines = false;
        _tree.FullRowSelect = true;
        _tree.HideSelection = false;
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node is { Tag: UninstallRecord })
            {
                // TreeNode.Expanded is read-only; expansion goes through the node's
                // TreeView, or through the node's own Toggle when it is attached.
                if (e.Node.TreeView is { } view)
                {
                    e.Node.Toggle();
                    view.Invalidate();
                }
            }
        };

        _export.Text = "Export to text";
        _export.Click += (_, _) => Export();
        _refresh.Text = "Refresh";
        _refresh.Click += (_, _) => Populate();

        foreach (Button button in new[] { _export, _refresh })
        {
            button.Size = new Size(Math.Max(90, TextRenderer.MeasureText(button.Text, button.Font).Width + 44), 32);
        }

        Theme.StyleButton(_export, p);
        Theme.StyleButton(_refresh, p);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = p.Surface,
            Padding = new Padding(0, 8, 12, 8),
        };

        toolbar.Controls.Add(_refresh);
        toolbar.Controls.Add(_export);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = p.TextDim;
        _status.Padding = new Padding(12, 0, 0, 0);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = p.Surface };
        footer.Controls.Add(_status);

        Controls.Add(_tree);
        Controls.Add(footer);
        Controls.Add(toolbar);

        Populate();
    }

    private void Populate()
    {
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();

            List<UninstallRecord> records = UninstallHistory.Load();

            foreach (IGrouping<DateTime, UninstallRecord> day in records
                .GroupBy(r => r.When.Date)
                .OrderByDescending(g => g.Key))
            {
                string label = day.Key == DateTime.Today
                    ? "Today"
                    : day.Key == DateTime.Today.AddDays(-1)
                        ? "Yesterday"
                        : day.Key.ToString("yyyy-MM-dd");

                var dayNode = new TreeNode($"{label} ({day.Count()})");
                _tree.Nodes.Add(dayNode);

                foreach (UninstallRecord record in day.OrderByDescending(r => r.When))
                {
                    var node = new TreeNode(
                        $"{record.When:HH:mm}  {record.Name}  -  {record.Summary}")
                    {
                        Tag = record,
                    };

                    // Failed removals are shown first so a partial removal is not buried.
                    foreach (string failed in record.Failed)
                    {
                        node.Nodes.Add(new TreeNode($"! {failed}")
                        {
                            ForeColor = p_warning,
                        });
                    }

                    foreach (string removed in record.Removed)
                    {
                        node.Nodes.Add(new TreeNode($"+ {removed}"));
                    }

                    node.Nodes.Add(new TreeNode($"operation: {record.Operation}, exit code {record.ExitCode}"));
                    dayNode.Nodes.Add(node);
                }

                dayNode.Expand();
            }

            _status.Text = records.Count == 0
                ? "No removals recorded yet"
                : $"{records.Count} removal(s) recorded";
        }
        finally
        {
            _tree.EndUpdate();
        }
    }

    private static Color p_warning => Theme.Current.Warning;

    private void Export()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export uninstall history",
            FileName = $"sweep-uninstall-history-{DateTime.Now:yyyy-MM-dd}.txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, UninstallHistory.ExportAll());
            LogStore.Success($"Exported uninstall history to {dialog.FileName}.");
            MessageBox.Show(this, $"Written to:\r\n{dialog.FileName}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogStore.Error("Could not export uninstall history", ex);
            MessageBox.Show(this, $"Could not write that file:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
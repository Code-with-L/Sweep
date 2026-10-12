using System.Diagnostics;

namespace Sweep;

/// <summary>One launchable Windows maintenance utility.</summary>
internal sealed record WindowsTool(string Name, string Command, string Category);

/// <summary>
/// Windows Tools: the built-in maintenance utilities, with the command line shown for each.
///
/// Showing the command line is the point of this module. Half of these are only reachable
/// through Control Panel or a Run dialog, and once the command is on screen it can be
/// reused from a terminal or a shortcut instead of hunted for again.
/// </summary>
internal sealed class WindowsToolsPanel : Panel
{
    private readonly DataGridView _grid = new();
    private readonly List<WindowsTool> _tools = new();
    private readonly TextBox _search = new();
    private readonly Button _add = new();
    private readonly Button _remove = new();
    private readonly Label _status = new();

    public WindowsToolsPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Current.Surface;
        Padding = Padding.Empty;

        LoadBuiltins();

        _search.Dock = DockStyle.Top;
        _search.Height = 30;
        _search.PlaceholderText = "Search tools";
        _search.BorderStyle = BorderStyle.FixedSingle;
        Theme.StyleTextBox(_search, Theme.Current);
        _search.TextChanged += (_, _) => ApplyFilter(_search.Text);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = Theme.Current.Surface;
        _grid.BorderStyle = BorderStyle.None;
        _grid.GridColor = Theme.Current.Border;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 30;
        _grid.RowTemplate.Height = 30;
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                LaunchSelected();
            }
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Tool", FillWeight = 120, MinimumWidth = 200 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "category", HeaderText = "Category", FillWeight = 60, MinimumWidth = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "command", HeaderText = "Command-line", FillWeight = 220, MinimumWidth = 260 });

        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.HeaderCell.Style.ForeColor = Theme.Current.TextDim;
            column.HeaderCell.Style.SelectionBackColor = Theme.Current.SurfaceAlt;
            column.HeaderCell.Style.BackColor = Theme.Current.SurfaceAlt;
            column.DefaultCellStyle.ForeColor = Theme.Current.Text;
            column.DefaultCellStyle.BackColor = Theme.Current.Surface;
            column.DefaultCellStyle.SelectionBackColor = Theme.Current.Selection;
            column.DefaultCellStyle.SelectionForeColor = Theme.Current.Text;
            column.DefaultCellStyle.Padding = new Padding(4, 2, 4, 2);
        }

        _grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == 1)
            {
                e.CellStyle!.ForeColor = Theme.Current.TextDim;
            }
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Current.Surface,
            Padding = new Padding(12, 8, 12, 6),
        };

        var run = new Button { Text = "Run", Width = 90, Height = 30 };
        Theme.StyleButton(run, Theme.Current);
        run.Click += (_, _) => LaunchSelected();

        _add.Text = "Add tool";
        _add.Width = 100;
        _add.Height = 30;
        Theme.StyleButton(_add, Theme.Current);
        _add.Click += (_, _) => AddCustomTool();

        _remove.Text = "Remove";
        _remove.Width = 100;
        _remove.Height = 30;
        Theme.StyleButton(_remove, Theme.Current);
        _remove.Click += (_, _) => RemoveSelected();

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Theme.Current.TextDim;
        _status.Padding = new Padding(12, 0, 0, 0);

        buttons.Controls.Add(run);
        buttons.Controls.Add(_add);
        buttons.Controls.Add(_remove);
        buttons.Controls.Add(_status);

        // Added last so they dock first: strip on top, list fills the middle.
        Controls.Add(_grid);
        Controls.Add(_search);
        Controls.Add(buttons);

        ApplyFilter(string.Empty);
        UpdateStatus();
    }

    private void LoadBuiltins()
    {
        foreach (WindowsTool tool in Builtins())
        {
            _tools.Add(tool);
        }

        // User-added entries come back after the built-ins so they read as an appendix.
        foreach ((string name, CustomTool tool) in SettingsStore.Current.CustomTools)
        {
            _tools.Add(new WindowsTool(name, tool.Command, tool.Category));
        }
    }

    private void ApplyFilter(string query)
    {
        _grid.Rows.Clear();

        foreach (WindowsTool tool in _tools.Where(t =>
            string.IsNullOrWhiteSpace(query) ||
            t.Name.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
            t.Category.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)))
        {
            _grid.Rows.Add(tool.Name, tool.Category, tool.Command);
        }

        UpdateStatus();
    }

    private void UpdateStatus() =>
        _status.Text = $"{_grid.Rows.Count} of {_tools.Count} tools";

    private WindowsTool? SelectedTool()
    {
        if (_grid.CurrentRow is not DataGridViewRow current || current.Index < 0
            || current.Index >= _grid.Rows.Count)
        {
            return null;
        }

        int index = current.Index;
        return new WindowsTool(
            _grid.Rows[index].Cells["name"].Value?.ToString() ?? string.Empty,
            _grid.Rows[index].Cells["category"].Value?.ToString() ?? string.Empty,
            _grid.Rows[index].Cells["command"].Value?.ToString() ?? string.Empty);
    }

    private void LaunchSelected()
    {
        if (SelectedTool() is not WindowsTool tool)
        {
            return;
        }

        CommandLineParts parts = CommandLine.Split(tool.Command);
        if (string.IsNullOrWhiteSpace(parts.FileName))
        {
            MessageBox.Show(this, $"That command could not be understood:\r\n{tool.Command}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Environment variables such as %SystemRoot% are not expanded for us when handing a
        // command straight to Process.Start.
        string file = Environment.ExpandEnvironmentVariables(parts.FileName);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = parts.Arguments ?? string.Empty,
                UseShellExecute = true,
            });
            LogStore.Success($"Launched {tool.Name}.");
        }
        catch (Exception ex)
        {
            LogStore.Error($"Could not launch {tool.Name}", ex);
            MessageBox.Show(this, $"Could not launch {tool.Name}:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void AddCustomTool()
    {
        using var dialog = new ToolEntryDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Tool is not { } tool)
        {
            return;
        }

        _tools.Add(tool);
        SettingsStore.AddCustomTool(tool.Name, tool.Command, tool.Category);
        ApplyFilter(_search.Text);
    }

    private void RemoveSelected()
    {
        if (SelectedTool() is not WindowsTool tool || !SettingsStore.IsCustomTool(tool.Name))
        {
            MessageBox.Show(this, "Only tools you added yourself can be removed.", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _tools.Remove(tool);
        SettingsStore.RemoveCustomTool(tool.Name);
        ApplyFilter(_search.Text);
    }

    public void Restyle()
    {
        BackColor = Theme.Current.Surface;
        _grid.BackgroundColor = Theme.Current.Surface;
        _grid.DefaultCellStyle.BackColor = Theme.Current.Surface;
        _grid.DefaultCellStyle.ForeColor = Theme.Current.Text;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Current.SurfaceAlt;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Current.TextDim;
        Theme.StyleTextBox(_search, Theme.Current);
        _status.ForeColor = Theme.Current.TextDim;
        ApplyFilter(_search.Text);
    }

    /// <summary>
    /// The built-in utilities, grouped the way the Control Panel groups them.
    ///
    /// Every one of these ships with Windows; none of them are downloaded, which keeps the
    /// module working with no network.
    /// </summary>
    internal static IEnumerable<WindowsTool> Builtins() =>
    [
        new("System Restore", @"%SystemRoot%\system32\rstrui.exe", "Maintenance"),
        new("Network Information", @"%SystemRoot%\system32\cmd.exe /k ipconfig /all", "Diagnostics"),
        new("Security Center", @"%SystemRoot%\system32\wscui.cpl", "Security"),
        new("System Properties", @"%SystemRoot%\system32\sysdm.cpl", "System"),
        new("System Information", @"%ProgramFiles%\Common Files\Microsoft Shared\MSInfo\msinfo32.exe", "Diagnostics"),
        new("TCP/IP Netstat", @"%SystemRoot%\system32\cmd.exe /k netstat -a -b", "Diagnostics"),
        new("On-Screen Keyboard", @"%SystemRoot%\system32\osk.exe", "Accessibility"),
        new("Disk Defragmenter", @"%SystemRoot%\system32\dfrgui.exe", "Maintenance"),
        new("Services", @"%SystemRoot%\system32\services.msc", "System"),
        new("Shared Folders", @"%SystemRoot%\system32\fsmgmt.msc", "System"),
        new("Add/Remove Windows Components", @"%SystemRoot%\system32\OptionalFeatures.exe", "System"),
        new("Backup and Restore", @"%SystemRoot%\system32\sdclt.exe", "Maintenance"),
        new("Task Scheduler", @"%SystemRoot%\system32\taskschd.msc", "System"),
        new("Check Disk", @"%SystemRoot%\system32\cmd.exe /k chkdsk.exe", "Maintenance"),
        new("System File Checker", @"%SystemRoot%\system32\cmd.exe /k sfc.exe /scannow", "Maintenance"),
        new("Disk Cleanup", @"%SystemRoot%\system32\cleanmgr.exe", "Maintenance"),
        new("Resource Monitor", @"%SystemRoot%\system32\resmon.exe", "Diagnostics"),
        new("Deployment Image Servicing", @"%SystemRoot%\system32\cmd.exe /k dism.exe /Online /Cleanup-Image /RestoreHealth", "Maintenance"),
        new("Flush DNS Cache", @"%SystemRoot%\system32\cmd.exe /k ipconfig.exe /flushdns", "Network"),
        new("DirectX Diagnostic", @"%SystemRoot%\system32\dxdiag.exe", "Diagnostics"),
        new("Registry Editor", @"%SystemRoot%\regedit.exe", "System"),
        new("Local Users and Groups", @"%SystemRoot%\system32\lusrmgr.msc", "Security"),
        new("Windows Firewall", @"%SystemRoot%\system32\WF.msc", "Security"),
        new("Device Manager", @"%SystemRoot%\system32\devmgmt.msc", "System"),
        new("Memory Diagnostic", @"%SystemRoot%\system32\mdsched.exe", "Diagnostics"),
        new("System Configuration", @"%SystemRoot%\system32\msconfig.exe", "System"),
        new("Performance Monitor", @"%SystemRoot%\system32\perfmon.exe", "Diagnostics"),
        new("Event Viewer", @"%SystemRoot%\system32\eventvwr.msc", "Diagnostics"),
        new("Control Panel", @"%SystemRoot%\system32\control.exe", "System"),
    ];
}
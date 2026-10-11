using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Sweep;

internal sealed class MainForm : ThemedForm
{
    private readonly DataGridView _grid = new();
    private readonly TextBox _search = new();
    private readonly MenuStrip _menu = new();
    private readonly ContextMenuStrip _contextMenu = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Visible = false };
    private readonly Label _adminBadge = new();
    private readonly Label _selectionInfo = new();
    private readonly Button _uninstallButton = new();
    private readonly Button _scanButton = new();

    private List<InstalledApp> _all = [];
    private List<InstalledApp> _filtered = [];
    private CancellationTokenSource? _cts;
    private AppSettings _settings = new();
    private ActivityForm? _activity;
    private ToolStripMenuItem _themeDarkItem = null!;
    private ToolStripMenuItem _themeLightItem = null!;
    private ToolStripMenuItem _lastUninstalledItem = null!;
    private bool _busy;

    /// <summary>Diagnostic hook: opens the leftovers pane for a named program at startup.</summary>
    private readonly string? _probeApp;
    private readonly string? _probeView;
    private bool _probeOpened;

    public MainForm(string? probeApp = null, string? probeView = null)
    {
        _probeApp = probeApp;
        _probeView = probeView;
        _settings = SettingsStore.Load();

        Text = "Sweep - App Uninstaller";
        StartPosition = FormStartPosition.CenterScreen;

        // Fit the working area instead of assuming a large monitor. MinimumSize is assigned
        // first: it was previously read while still (0,0), so WinForms silently overrode
        // the computed size on small displays.
        MinimumSize = new Size(820, 480);

        Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1180, 700);
        ClientSize = new Size(
            Math.Max(MinimumSize.Width, Math.Min(1180, work.Width - 40)),
            Math.Max(MinimumSize.Height, Math.Min(700, work.Height - 40)));
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

BuildMenu();
        var toolbar = BuildToolbar();
        var filters = BuildFilterBar();
        var bottom = BuildBottomBar();
        var status = BuildStatusStrip();
        BuildGrid();
        BuildContextMenu();

        // An explicit table avoids WinForms dock-order surprises: the fill control used to
        // swallow the status strip and selection bar.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(_menu, 0, 0);
        root.Controls.Add(toolbar, 0, 1);
        root.Controls.Add(filters, 0, 2);
        root.Controls.Add(_grid, 0, 3);
        root.Controls.Add(bottom, 0, 4);
        root.Controls.Add(status, 0, 5);

        Controls.Add(root);

        ApplyTheme();

        _grid.SelectionChanged += (_, _) => UpdateSelection();
        _search.TextChanged += (_, _) => ApplyFilter();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0)
            {
                return;
            }

            var app = SelectedApp();
            if (app is not null)
            {
                OpenInstallLocation(app);
            }
        };

        Shown += async (_, _) => await LoadAppsAsync();
        FormClosing += (_, _) => _cts?.Cancel();
    }

    // ---- Construction ---------------------------------------------------

    private void ApplyTheme()
    {
        var p = Theme.Current;
        BackColor = p.Background;
        ForeColor = p.Text;
        Theme.StyleMenu(_menu, p);
        Theme.StyleTextBox(_search, p);
        Theme.ApplyToGrid(_grid, p);
        Theme.StyleButton(_uninstallButton, p, primary: true);
        Theme.StyleButton(_scanButton, p);
        _search.BackColor = p.SurfaceAlt;
        _search.ForeColor = p.Text;
        _statusLabel.ForeColor = p.TextDim;
        _selectionInfo.ForeColor = p.TextDim;

        var elevated = Program.IsElevated;
        _adminBadge.Text = elevated ? "Elevated" : "Not elevated";
        _adminBadge.ForeColor = elevated ? p.Success : p.Warning;

        _themeDarkItem.Checked = _settings.Theme == ThemeMode.Dark;
        _themeLightItem.Checked = _settings.Theme == ThemeMode.Light;

        // The native title bar is themed through DWM, so it has to be told again.
        if (IsHandleCreated)
        {
            ApplyWindows11Chrome();
        }
    }

    private void BuildMenu()
    {
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Refresh list", null, async (_, _) => await LoadAppsAsync())
        {
            ShortcutKeys = Keys.F5,
            Name = "refresh",
        });
        file.DropDownItems.Add(new ToolStripMenuItem("&Export list...", null, (_, _) => ExportList()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()) { Name = "exit" });

        var removal = new ToolStripMenuItem("&Removal");
        removal.DropDownItems.Add(new ToolStripMenuItem("&Uninstall...", null, async (_, _) => await UninstallSelectedAsync())
        {
            ShortcutKeys = Keys.Delete,
            Name = "uninstall",
        });
        removal.DropDownItems.Add(new ToolStripMenuItem("Scan &leftovers...", null, (_, _) => ScanSelected())
        {
            Name = "scan",
        });
        _lastUninstalledItem = new ToolStripMenuItem("Scan leftovers for &last removed program...", null, (_, _) => ScanLastUninstalled())
        {
            Name = "scanLast",
            Enabled = false,
        };
        removal.DropDownItems.Add(_lastUninstalledItem);

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(new ToolStripMenuItem("&Startup entries...", null, (_, _) => OpenForm<AutorunForm>())
        {
            Name = "startup",
        });
        view.DropDownItems.Add(new ToolStripMenuItem("&Windows Apps (Store)...", null, (_, _) => OpenForm<WindowsAppsForm>())
        {
            Name = "storeApps",
        });
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(_themeDarkItem = new ToolStripMenuItem("&Dark theme", null, (_, _) => SetTheme(ThemeMode.Dark)));
        view.DropDownItems.Add(_themeLightItem = new ToolStripMenuItem("&Light theme", null, (_, _) => SetTheme(ThemeMode.Light)));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(new ToolStripMenuItem("Activity &log...", null, (_, _) => ShowActivity())
        {
            Name = "activity",
            ShortcutKeys = Keys.Control | Keys.L,
        });

        var options = new ToolStripMenuItem("&Options");
        options.DropDownItems.Add(new ToolStripMenuItem("&Settings...", null, (_, _) => ShowSettings())
        {
            Name = "settings",
            ShortcutKeys = Keys.Control | Keys.Oemcomma,
        });
        options.DropDownItems.Add(new ToolStripMenuItem("&Export all settings...", null, (_, _) => ExportSettings()));
        options.DropDownItems.Add(new ToolStripMenuItem("&Import all settings...", null, (_, _) => ImportSettings()));
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(new ToolStripMenuItem("Open &quarantine folder", null, (_, _) => OpenQuarantineFolder())
        {
            Name = "quarantineOpen",
        });
        options.DropDownItems.Add(new ToolStripMenuItem("&Restore from quarantine...", null, (_, _) => RestoreFromQuarantine()));
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(new ToolStripMenuItem("Open &data folder", null, (_, _) => OpenPath(SettingsStore.SettingsPath, mustBeDirectory: true))
        {
            Name = "dataOpen",
        });

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About Sweep", null, (_, _) => ShowAbout()) { Name = "about" });

        _menu.Items.AddRange([file, removal, view, options, help]);
        _menu.Name = "mainMenu";
    }

private Control BuildToolbar()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 9, 12, 9) };
        panel.BackColor = Theme.Current.Surface;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            BackColor = Theme.Current.Surface,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 540));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = Theme.Current.Surface,
        };

        var searchLabel = new Label
        {
            Text = "Search",
            AutoSize = true,
            ForeColor = Theme.Current.TextDim,
            Margin = new Padding(2, 8, 8, 0),
        };

        _search.Size = new Size(320, 26);
        _search.Margin = new Padding(0, 2, 10, 0);
        _search.BorderStyle = BorderStyle.FixedSingle;
        Native.SetCueBanner(_search, "Search programs, publishers or folders");

        var refresh = new Button { Text = "Refresh", Size = new Size(88, 30), Margin = new Padding(0, 0, 0, 0) };
        refresh.Click += async (_, _) => await LoadAppsAsync();
        Theme.StyleButton(refresh, Theme.Current);

        left.Controls.AddRange([searchLabel, _search, refresh]);

        // RightToLeft flow keeps the actions against the right edge without any manual maths.
        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = Theme.Current.Surface,
        };

        _uninstallButton.Text = "Uninstall";
        _uninstallButton.Size = new Size(112, 30);
        _uninstallButton.Margin = new Padding(0, 0, 8, 0);
        _uninstallButton.Click += async (_, _) => await UninstallSelectedAsync();

        _scanButton.Text = "Scan leftovers";
        _scanButton.Size = new Size(132, 30);
        _scanButton.Margin = new Padding(0, 0, 16, 0);
        _scanButton.Click += (_, _) => ScanSelected();

        _adminBadge.AutoSize = true;
        _adminBadge.Margin = new Padding(0, 8, 0, 0);

        right.Controls.AddRange([_uninstallButton, _scanButton, _adminBadge]);

        layout.Controls.Add(left, 0, 0);
        layout.Controls.Add(right, 1, 0);
        panel.Controls.Add(layout);
        return panel;
    }

    private void LayoutToolbar(Panel panel, Button refresh)
    {
        int right = panel.ClientSize.Width - 14;
        _scanButton.Location = new Point(right - _scanButton.Width, 11);
        _uninstallButton.Location = new Point(right - _scanButton.Width - 8 - _uninstallButton.Width, 11);
        _adminBadge.Location = new Point(right - _scanButton.Width - 120 - _uninstallButton.Width, 17);
    }

    private Panel BuildFilterBar()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Height = 40, Padding = new Padding(12, 6, 12, 6) };
        panel.BackColor = Theme.Current.Surface;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Current.Surface,
            Margin = Padding.Empty,
        };

        _typeFilter.Items.AddRange(["All", "32-bit", "64-bit"]);
        _typeFilter.SelectedIndex = 0;
        _sizeFilter.Items.AddRange(["All", "> 10 MB", "> 100 MB", "> 1024 MB"]);
        _sizeFilter.SelectedIndex = 0;
        _driveFilter.Items.Add("All");

        foreach (var box in new[] { _typeFilter, _sizeFilter, _driveFilter })
        {
            box.BackColor = Theme.Current.SurfaceAlt;
            box.ForeColor = Theme.Current.Text;
            box.Margin = new Padding(0, 1, 10, 0);
            box.SelectedIndexChanged += (_, _) => ApplyFilter();
        }

        _countLabel.AutoSize = false;
        _countLabel.Size = new Size(150, 24);
        _countLabel.TextAlign = ContentAlignment.MiddleLeft;
        _countLabel.ForeColor = Theme.Current.TextDim;
        _countLabel.Margin = new Padding(6, 0, 0, 0);

        flow.Controls.Add(MakeLabel("Type:"));
        flow.Controls.Add(_typeFilter);
        flow.Controls.Add(MakeLabel("Size:"));
        flow.Controls.Add(_sizeFilter);
        flow.Controls.Add(MakeLabel("Drive:"));
        flow.Controls.Add(_driveFilter);
        flow.Controls.Add(_countLabel);

        panel.Controls.Add(flow);
        return panel;
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 7, 5, 0),
    };

    /// <summary>Rebuilds the drive list once programs have been enumerated.</summary>
private void RefreshDriveFilter()
    {
        string selected = _driveFilter.SelectedItem?.ToString() ?? "All";
        _driveFilter.Items.Clear();
        _driveFilter.Items.Add("All");

        foreach (string drive in _all
                     .Select(a => a.Drive)
                     .Where(d => !string.IsNullOrEmpty(d))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            _driveFilter.Items.Add(drive);
        }

        int index = _driveFilter.Items.IndexOf(selected);
        _driveFilter.SelectedIndex = index >= 0 ? index : 0;
    }

    private Panel BuildBottomBar()
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(14, 8, 14, 8) };
        panel.BackColor = Theme.Current.Surface;

        _selectionInfo.AutoSize = false;
        _selectionInfo.Dock = DockStyle.Fill;
        _selectionInfo.TextAlign = ContentAlignment.MiddleLeft;

        var activity = new Button { Text = "Activity log", Dock = DockStyle.Right, Width = 110, Height = 28 };
        Theme.StyleButton(activity, Theme.Current);
        activity.Click += (_, _) => ShowActivity();

        panel.Controls.Add(_selectionInfo);
        panel.Controls.Add(activity);
        return panel;
    }

    private StatusStrip BuildStatusStrip()
    {
        var strip = new StatusStrip { SizingGrip = false };
        strip.BackColor = Theme.Current.SurfaceAlt;
        strip.ForeColor = Theme.Current.TextDim;
        strip.Items.Add(_statusLabel);
        strip.Items.Add(_progress);
        return strip;
    }

    private void BuildGrid()
    {
        Native.EnableDoubleBuffer(_grid);

        _grid.Dock = DockStyle.Fill;
        _grid.VirtualMode = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.ShowCellToolTips = true;

        // The cell is sized to the icon and the icon is rendered at the cell's exact
        // pixel size, so GDI+ draws it 1:1. A fixed 32px icon in a fixed 38px row was
        // scaled by the grid at higher DPIs and came out squashed and clipped.
        var iconStyle = new DataGridViewCellStyle
        {
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 4, 0),
        };

        _grid.Columns.Add(new DataGridViewImageColumn { Name = "icon", Width = 46, Resizable = DataGridViewTriState.False });

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Application", ToolTipText = "Application", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 200 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "Size", ToolTipText = "Installed size", Width = 92 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "version", HeaderText = "Version", ToolTipText = "Version", Width = 118 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "arch", HeaderText = "Type", ToolTipText = "32-bit or 64-bit program", Width = 76 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "installed", HeaderText = "Install Date", ToolTipText = "Install Date", Width = 104 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "publisher", HeaderText = "Company", ToolTipText = "Company / Publisher", Width = 170 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "website", HeaderText = "Website", ToolTipText = "Website", Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "comment", HeaderText = "Comment", ToolTipText = "Comment", Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "uninstall", HeaderText = "Uninstall String", ToolTipText = "Registered uninstall command", Width = 240, Visible = false });

        _grid.CellValueNeeded += OnCellValueNeeded;
        _grid.CellFormatting += OnCellFormatting;

        // Without this, one bad cell value silently blanks the entire grid instead of
        // reporting which cell was wrong. That is exactly how the list came up empty:
        // a null icon raised FormatException, WinForms rethrew it out of the paint,
        // and not one row drew.
        _grid.DataError += (_, e) =>
        {
            string column = e.ColumnIndex >= 0 && e.ColumnIndex < _grid.Columns.Count
                ? _grid.Columns[e.ColumnIndex].Name
                : "?";

            LogStore.Error(
                $"DataGridView data error at row={e.RowIndex} col={e.ColumnIndex} column={column}: " +
                $"{e.Exception?.GetType().Name}: {e.Exception?.Message}");
            e.ThrowException = false;
        };

        _grid.ColumnHeaderMouseClick += (_, e) => OnColumnHeaderClick(e.ColumnIndex);
    }

    /// <summary>
    /// Row height and icon column width follow the DPI.
    ///
    /// A hardcoded 38 looked right at 100% and clipped descenders at 150%, because the row
    /// scaled with the DPI while the metrics did not.
    ///
    /// This deliberately does NOT run from the constructor. Resizing a column before the
    /// grid has been laid out leaves the column header cells holding stale clip rectangles,
    /// and every column title then renders cut off.
    /// </summary>
    private void ApplyRowMetrics()
    {
        IconProvider.SetDpi(DeviceDpi);

        int iconSize = IconProvider.IconSize;
        int textHeight = (int)Math.Ceiling(_grid.Font.Height * 1.7);
        int height = Math.Max(iconSize, textHeight) + 10;

        _grid.RowTemplate.Height = height;

        if (_grid.Columns["icon"] is { } iconColumn)
        {
            iconColumn.Width = iconSize + 12;
        }

        _grid.Invalidate();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyRowMetrics();
        IconProvider.Clear();
        PrefetchIcons();
        ApplyWindows11Chrome();
    }

    private void OnColumnHeaderClick(int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= _grid.Columns.Count)
        {
            return;
        }

        string column = _grid.Columns[columnIndex].Name;
        if (column == "icon")
        {
            return;
        }

        if (_sortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        foreach (DataGridViewColumn col in _grid.Columns)
        {
            col.HeaderCell.SortGlyphDirection = col.Name == _sortColumn
                ? (_sortAscending ? SortOrder.Ascending : SortOrder.Descending)
                : SortOrder.None;
        }

        ApplyFilter();
    }

private void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        // A group row occupies a slot in the same row index space.
        if (_groups.IsGroupRow(e.RowIndex))
        {
            e.Value = e.ColumnIndex switch
            {
                0 => IconProvider.Placeholder,
                1 => _groups.GroupLabel(e.RowIndex),
                _ => null,
            };

            return;
        }

        int dataRow = _groups.DataRowFor(e.RowIndex);
        if (dataRow < 0 || dataRow >= _filtered.Count)
        {
            return;
        }

        var app = _filtered[dataRow];
        string column = _grid.Columns[e.ColumnIndex].Name;

        e.Value = column switch
        {
            "icon" => IconProvider.Get(app),
            "name" => app.DisplayName,
            "version" => Dash(app.VersionDisplay),
            "arch" => app.Architecture,
            "publisher" => Dash(app.Company),
            "size" => Dash(app.SizeText),
            "installed" => app.InstallDateDisplay?.ToString("dd/MM/yyyy") ?? "-",
            "website" => Dash(app.WebsiteDisplay),
            "comment" => Dash(app.CommentDisplay),
            "uninstall" => Dash(app.UninstallString),
            _ => null,
        };
    }

    private static string Dash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    /// <summary>Styles the group header rows so they read as headers, not as programs.</summary>
    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || !_groups.IsGroupRow(e.RowIndex))
        {
            return;
        }

        var p = Theme.Current;
        e.CellStyle.BackColor = p.SurfaceAlt;
        e.CellStyle.ForeColor = p.Accent;
        e.CellStyle.Font = _groupFont ??= new Font(_grid.Font, FontStyle.Bold);
        e.CellStyle.SelectionBackColor = p.SurfaceAlt;
        e.CellStyle.SelectionForeColor = p.Accent;
    }

    /// <summary>
    /// Built once and reused. Allocating a Font inside CellFormatting runs it for every
    /// group cell on every repaint, and each one is a GDI handle that nobody disposes.
    /// </summary>
    private Font? _groupFont;

    private void BuildContextMenu()
    {
        _contextMenu.Items.Add(new ToolStripMenuItem("Uninstall...", null, async (_, _) => await UninstallSelectedAsync()));
        _contextMenu.Items.Add(new ToolStripMenuItem("Scan leftovers...", null, (_, _) => ScanSelected()));
        _contextMenu.Items.Add(new ToolStripMenuItem("&Kill running processes", null, (_, _) => KillProcesses(false))
        {
            Name = "kill",
        });
        _contextMenu.Items.Add(new ToolStripMenuItem("Kill processes and remove entry", null, (_, _) => KillProcesses(true))
        {
            Name = "killDelete",
        });
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem("&Run", null, (_, _) => RunProgram())
        {
            Name = "run",
        });
        _contextMenu.Items.Add(new ToolStripMenuItem("Open containing folder", null, (_, _) =>
        {
            var app = SelectedApp();
            if (app is not null)
            {
                OpenInstallLocation(app);
            }
        }));
        _contextMenu.Items.Add(new ToolStripMenuItem("Open registry key", null, (_, _) => OpenRegistryKey()));
        _contextMenu.Items.Add(new ToolStripMenuItem("Open &website", null, (_, _) => OpenUrlForSelected(a => a.WebsiteDisplay))
        {
            Name = "website",
        });
        _contextMenu.Items.Add(new ToolStripMenuItem("Open help link", null, (_, _) => OpenUrlForSelected(a => a.HelpLink))
        {
            Name = "help",
        });
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem("Copy to clipboard", null, (_, _) => CopyDetails()));
        _contextMenu.Items.Add(new ToolStripMenuItem("Remove leftover registry entry", null, (_, _) => RemoveEntryOnly())
        {
            Name = "removeEntry",
        });

        _grid.ContextMenuStrip = _contextMenu;
        _contextMenu.Opening += (_, _) => UpdateSelection();
    }

    private void UpdateContextMenuState()
    {
        var app = SelectedApp();
        bool has = app is not null;

        SetContextEnabled("kill", has);
        SetContextEnabled("killDelete", has);
        SetContextEnabled("run", has);
        SetContextEnabled("website", has && !string.IsNullOrWhiteSpace(app!.WebsiteDisplay));
        SetContextEnabled("help", has && !string.IsNullOrWhiteSpace(app!.HelpLink));
    }

    private void SetContextEnabled(string name, bool value)
    {
        if (_contextMenu.Items[name] is ToolStripItem item)
        {
            item.Enabled = value;
        }
    }

    private void RunProgram()
    {
        var app = SelectedApp();
        string? exe = app?.ExecutablePath;
        if (app is null || string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            MessageBox.Show(this, "This program does not report an executable that can be launched.", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            });
            LogStore.Info($"Launched \"{app.DisplayName}\".");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not launch \"{app.DisplayName}\":\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenRegistryKey()
    {
        var app = SelectedApp();
        if (app is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "regedit.exe",
                UseShellExecute = true,
                ArgumentList = { app.Location.SubKey },
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open regedit:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenUrlForSelected(Func<InstalledApp, string> selector)
    {
        var app = SelectedApp();
        if (app is null)
        {
            return;
        }

        string url = selector(app);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        // Registry values frequently omit the scheme.
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the link:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Stops processes belonging to the selected program. Optionally also drops its
    /// Add/Remove Programs entry, which is the "stuck entry" case where a program is still
    /// listed after its files are already gone.
    /// </summary>
    private void KillProcesses(bool alsoRemoveEntry)
    {
        var app = SelectedApp();
        if (app is null || _busy)
        {
            return;
        }

        var running = ProcessHunter.FindFor(app);
        if (running.Count == 0)
        {
            SetStatus($"No running processes belong to \"{app.DisplayName}\".");
            return;
        }

        string list = string.Join(Environment.NewLine, running.Select(p => $"  {p.Name} (PID {p.ProcessId})"));
        var answer = MessageBox.Show(this,
            $"{running.Count} process(es) belonging to \"{app.DisplayName}\" are running:" +
            $"{Environment.NewLine}{list}{Environment.NewLine}{Environment.NewLine}Stop them now?",
            "Sweep", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        int killed = ProcessHunter.Kill(running);
        LogStore.Success($"Stopped {killed} process(es) for \"{app.DisplayName}\".");
        SetStatus($"Stopped {killed} process(es).");

        if (!alsoRemoveEntry)
        {
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Also remove the Add/Remove Programs entry for \"{app.DisplayName}\"?" +
            $"{Environment.NewLine}{Environment.NewLine}" +
            "This only hides it from the list. It does not remove the program's files.",
            "Sweep", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (confirm == DialogResult.Yes)
        {
            RegistryScanner.DeleteEntry(app.Location);
            _ = LoadAppsAsync();
        }
    }

    // ---- Data -----------------------------------------------------------

    private async Task LoadAppsAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "Reading installed programs...");

        try
        {
            // First thing after the window is laid out, so the row height and icon column
            // width are applied against real geometry.
            ApplyRowMetrics();

            var apps = await Task.Run(() => RegistryScanner.Scan());
            _all = apps;
            IconProvider.Clear();
            RefreshDriveFilter();
            ApplyFilter();

            string elevated = Program.IsElevated ? "elevated" : "not elevated";
            SetStatus($"{apps.Count} programs found ({elevated}).");
            LogStore.Info($"Listed {apps.Count} installed programs.");
        }
        catch (Exception ex)
        {
            LogStore.Error("Could not read the installed-programs list", ex);
            SetStatus("Could not read the installed-programs list.");
            MessageBox.Show(this, $"Could not read the installed-programs list:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }

        RunProbeIfRequested();
    }

    /// <summary>Diagnostic hook for automated UI screenshots; runs once the list is loaded.</summary>
    private void RunProbeIfRequested()
    {
        if (_probeView is not null && !_probeOpened)
        {
            _probeOpened = true;
            BeginInvoke((Action)(() =>
            {
                switch (_probeView)
                {
                    case "startup": OpenForm<AutorunForm>(); break;
                    case "store": OpenForm<WindowsAppsForm>(); break;
                    case "settings": ShowSettings(); break;
                    case "activity": ShowActivity(); break;
                    case "about": ShowAbout(); break;
                    default: LogStore.Warn($"Unknown UI probe view '{_probeView}'."); break;
                }
            }));
            return;
        }

        if (_probeApp is null || _probeOpened)
        {
            return;
        }

        _probeOpened = true;
        var target = _all.FirstOrDefault(a =>
            a.DisplayName.Contains(_probeApp, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            LogStore.Warn($"UI probe could not find a program matching '{_probeApp}'.");
            return;
        }

        BeginInvoke((Action)(() => OpenResidueForm(target)));
    }

private void ApplyFilter()
    {
        string query = _search.Text.Trim();

        IEnumerable<InstalledApp> results = _all;

        if (query.Length > 0)
        {
            results = results.Where(a =>
                Contains(a.DisplayName, query) ||
                Contains(a.Publisher, query) ||
                Contains(a.InstallLocation, query) ||
                Contains(a.DisplayVersion, query) ||
                Contains(a.Comments, query) ||
                Contains(a.WebsiteDisplay, query));
        }

        // Type filter: 32-bit / 64-bit, read from the real PE header.
        string? typeFilter = _typeFilter.SelectedIndex switch
        {
            1 => "32-bit",
            2 => "64-bit",
            _ => null,
        };
        if (typeFilter is not null)
        {
            results = results.Where(a => a.Architecture == typeFilter);
        }

        // Size filter. Entries with no reported size are excluded by a size filter, since
        // nothing can be asserted about them.
        long? minimumSize = _sizeFilter.SelectedIndex switch
        {
            1 => 10L * 1024 * 1024,
            2 => 100L * 1024 * 1024,
            3 => 1024L * 1024 * 1024,
            _ => null,
        };
        if (minimumSize is not null)
        {
            results = results.Where(a => a.EstimatedSizeBytes >= minimumSize.Value);
        }

        string drive = _driveFilter.SelectedItem?.ToString() ?? "All";
        if (drive != "All")
        {
            results = results.Where(a => a.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase));
        }

        _filtered = Sort(results).ToList();

        _groups.Rebuild(_filtered, _recentFirst, _recentWindowDays);

        // Grouping does not just add header rows, it reorders the list so each program sits
        // under the heading it belongs to. The grid reads _filtered by data index, so it has
        // to adopt the grouped order or the headers would lie about their contents.
        if (_groups.OrdersApps)
        {
            _filtered = _groups.Ordered.ToList();
        }

        _grid.RowCount = _groups.TotalRows;

        // Deliberately NOT setting _grid.CurrentCell here synchronously. Doing so re-enters
        // SelectionChanged -> UpdateSelection -> SelectedApp -> _grid.CurrentCell while the
        // setter is still on the stack, which deadlocks the grid on every filter change.
        // Posting it lets the current event finish first, and leaves the grid with a
        // current cell so the header region lays out its cells properly.
        int firstDataRow = _groups.GridRowFor(0);
        if (_filtered.Count > 0 && firstDataRow < _grid.RowCount)
        {
            BeginInvoke((Action)(() =>
            {
                try
                {
                    _grid.CurrentCell = _grid.Rows[firstDataRow].Cells["name"];
                    _grid.ClearSelection();
                }
                catch (InvalidOperationException)
                {
                    // Grid was re-filtered or closed before the post ran.
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Row count changed underneath us.
                }
            }));
        }

        _countLabel.Text = _filtered.Count == _all.Count
            ? $"{_all.Count} programs"
            : $"{_filtered.Count} of {_all.Count} programs";

        UpdateSelection();
        PrefetchIcons();
    }

    private IEnumerable<InstalledApp> Sort(IEnumerable<InstalledApp> source)
    {
        switch (_sortColumn)
        {
            case "size":
                return _sortAscending
                    ? source.OrderBy(a => a.EstimatedSizeBytes)
                    : source.OrderByDescending(a => a.EstimatedSizeBytes);

            case "installed":
                return _sortAscending
                    ? source.OrderBy(a => a.InstallDate ?? DateTime.MinValue)
                    : source.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue);

            default:
                Func<InstalledApp, string> key = _sortColumn switch
                {
                    "version" => a => a.DisplayVersion,
                    "arch" => a => a.Architecture,
                    "publisher" => a => a.Publisher,
                    "website" => a => a.WebsiteDisplay,
                    "comment" => a => a.Comments,
                    _ => a => a.DisplayName,
                };

                return _sortAscending
                    ? source.OrderBy(key, StringComparer.CurrentCultureIgnoreCase)
                    : source.OrderByDescending(key, StringComparer.CurrentCultureIgnoreCase);
        }
    }

    private readonly ComboBox _typeFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96 };
    private readonly ComboBox _sizeFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 108 };
    private readonly ComboBox _driveFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96 };
    private readonly Label _countLabel = new() { AutoSize = true };
    private readonly GroupIndex _groups = new() { Enabled = true };
    private bool _recentFirst = true;
    private int _recentWindowDays = 30;
    private string _sortColumn = "name";
    private bool _sortAscending = true;

    private CancellationTokenSource? _iconCts;

    /// <summary>
    /// Enriches the visible rows off the UI thread: icons and, where the registry has no
    /// estimate, the on-disk size.
    ///
    /// Both used to run inside CellValueNeeded, which is a UI-thread callback. Reading a
    /// PE header and walking an install folder there froze the window on every repaint.
    /// </summary>
    private void PrefetchIcons()
    {
        _iconCts?.Cancel();
        _iconCts?.Dispose();
        _iconCts = new CancellationTokenSource();
        var token = _iconCts.Token;

        var snapshot = _filtered.Take(400).ToList();

        _ = Task.Run(() =>
        {
try
            {
                IconProvider.Prefetch(snapshot, token);

                // Resolving metadata the registry lacks means touching the disk: the install
                // folder size, its creation date, the binary's company and version. All of
                // it is far too slow for the UI thread, which is why the grid's cell callback
                // only ever reads cache.
                var budget = TimeSpan.FromSeconds(20);
                var perItem = TimeSpan.FromMilliseconds(
                    Math.Clamp(budget.TotalMilliseconds / Math.Max(1, snapshot.Count), 40, 2000));

                for (int i = 0; i < snapshot.Count && !token.IsCancellationRequested; i++)
                {
                    snapshot[i].EnsureMetadataResolved(perItem, token);

                    // Repainting the whole grid per row wastes GDI work.
                    if (i % 40 == 0)
                    {
                        RepaintGrid(token);
                    }
                }
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Row enrichment stopped: {ex.Message}");
                return;
            }

            RepaintGrid(token);
        }, token);
    }

    private void RepaintGrid(CancellationToken token)
    {
        if (token.IsCancellationRequested || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
                try
                {
                    _grid.Invalidate();
                }
                catch (ObjectDisposedException)
                {
                    // Window closed mid-enrichment.
                }
            }));
        }
        catch (ObjectDisposedException)
        {
            // Window closed.
        }
        catch (InvalidOperationException)
        {
            // Handle destroyed.
        }
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private InstalledApp? SelectedApp()
    {
        int row = _grid.CurrentCell?.RowIndex ?? -1;
        if (row < 0)
        {
            return null;
        }

        int dataRow = _groups.DataRowFor(row);
        return dataRow >= 0 && dataRow < _filtered.Count ? _filtered[dataRow] : null;
    }

    private void UpdateSelection()
    {
        var app = SelectedApp();

        bool hasApp = app is not null;
        _uninstallButton.Enabled = hasApp && app!.HasUninstaller && !_busy;
        _scanButton.Enabled = hasApp && !_busy;
        _contextMenu.Items["removeEntry"]!.Enabled = hasApp;
        UpdateContextMenuState();

        if (app is null)
        {
            _selectionInfo.Text = _all.Count == 0 ? string.Empty : "No program selected.";
            return;
        }

        string uninstaller = app.HasUninstaller ? "registered" : "NOT registered";
        _selectionInfo.Text =
            $"{app.DisplayName}  -  {app.ScopeLabel}  -  uninstaller {uninstaller}" +
            (_filtered.Count != _all.Count ? $"  ({_filtered.Count} of {_all.Count} shown)" : string.Empty);
    }

    // ---- Actions --------------------------------------------------------

    private async Task UninstallSelectedAsync()
    {
        var app = SelectedApp();
        if (app is null || _busy)
        {
            return;
        }

        if (!app.HasUninstaller)
        {
            MessageBox.Show(this,
                $"\"{app.DisplayName}\" has no registered uninstall command.\r\n\r\n" +
                "Sweep can still search for leftover files and registry entries.",
                "Sweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var plan = UninstallRunner.BuildPlan(app, _settings.SilentUninstall);
        if (plan is null)
        {
            MessageBox.Show(this, "The registered uninstall command could not be parsed.", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new UninstallConfirmDialog(app, plan, _settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (dialog.CreateRestorePoint)
        {
            // The outcome used to be discarded, so a failed restore point left the user
            // believing they had an undo.
            SystemRestore.TryCreate(app.DisplayName, out string restoreMessage);
            if (!restoreMessage.StartsWith("Restore point created", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, restoreMessage, "Sweep", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        LogStore.Info($"Uninstalling \"{app.DisplayName}\" using {UninstallRunner.Describe(plan.Family)}.");

        _cts = new CancellationTokenSource();
        SetBusy(true, $"Running {plan.FileName}...");

        UninstallOutcome outcome;
        try
        {
            outcome = await UninstallRunner.RunAsync(plan, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Wait cancelled - the uninstaller may still be running.");

            // The uninstaller may well have continued without us.
            RememberLastUninstalled(app);
            return;
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
        }

        if (!outcome.Launched)
        {
            LogStore.Error($"\"{app.DisplayName}\" uninstaller did not start: {outcome.FailureReason}");
            RememberLastUninstalled(app);
            MessageBox.Show(this,
                $"The uninstaller could not be started.\r\n\r\n{outcome.FailureReason}\r\n\r\n" +
                $"Command: {plan.FileName} {plan.Arguments}\r\n\r\n" +
                "You can still scan for leftover files and registry entries.",
                "Sweep", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        LogStore.Info($"\"{app.DisplayName}\" uninstaller exited with code {outcome.ExitCode}.");
        RememberLastUninstalled(app);

        if (outcome.ExitCode == 0)
        {
            await WaitForRemovalAsync(app);
        }

        SetStatus($"\"{app.DisplayName}\" finished (exit code {outcome.ExitCode}).");
        await LoadAppsAsync();

        // Always offer the leftover scan. It used to be gated on the "silent" checkbox,
        // which meant a user who unticked it to watch the uninstaller could never scan
        // for what was left behind.
        PromptForLeftovers(app, outcome);
    }

    private InstalledApp? _lastUninstalled;

    /// <summary>
    /// Keeps the just-removed program reachable after it disappears from the list, so its
    /// leftovers can still be scanned without hunting for it again.
    /// </summary>
    private void RememberLastUninstalled(InstalledApp app)
    {
        _lastUninstalled = app;
        _lastUninstalledItem.Enabled = true;
        UpdateSelection();
    }

    private async Task WaitForRemovalAsync(InstalledApp app)
    {
        SetBusy(true, "Waiting for the uninstaller to finish...");
        try
        {
            bool removed = await UninstallRunner.WaitForEntryRemovalAsync(app, CancellationToken.None);
            if (!removed)
            {
                LogStore.Warn($"The registry entry for \"{app.DisplayName}\" is still present after the uninstaller exited.");
            }
        }
        catch
        {
            // Waiting is best-effort.
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PromptForLeftovers(InstalledApp app, UninstallOutcome outcome)
    {
        var text = new StringBuilder();
        text.AppendLine($"Uninstaller for \"{app.DisplayName}\" exited with code {outcome.ExitCode}.");

        if (outcome.ExitCode != 0)
        {
            text.AppendLine();
            text.AppendLine("A non-zero exit code usually means the uninstall did not complete cleanly.");
            text.AppendLine("Leftovers are therefore more likely than usual.");
        }

        string detail = (outcome.StandardError ?? string.Empty).Trim();
        if (detail.Length == 0)
        {
            detail = (outcome.StandardOutput ?? string.Empty).Trim();
        }

        if (detail.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("Uninstaller output:");
            foreach (string line in detail.Split('\n').Take(10))
            {
                text.AppendLine("  " + line.TrimEnd());
            }
        }

        text.AppendLine();
        text.AppendLine("Search for leftover files and registry entries now?");

        var answer = MessageBox.Show(this, text.ToString(), "Sweep", MessageBoxButtons.YesNo,
            outcome.ExitCode == 0 ? MessageBoxIcon.Question : MessageBoxIcon.Warning);

        if (answer == DialogResult.Yes)
        {
            OpenResidueForm(app);
        }
    }

    private void ScanSelected()
    {
        var app = SelectedApp();
        if (app is not null && !_busy)
        {
            OpenResidueForm(app);
        }
    }

    /// <summary>Reopens the leftover scan for the program removed most recently.</summary>
    private void ScanLastUninstalled()
    {
        if (_lastUninstalled is null || _busy)
        {
            return;
        }

        OpenResidueForm(_lastUninstalled);
    }

    private void OpenResidueForm(InstalledApp app)
    {
        using var form = new ResidueForm(app, ScanOptions.FromSettings(_settings));
        form.ShowDialog(this);
    }

    private void RemoveEntryOnly()
    {
        var app = SelectedApp();
        if (app is null)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Delete only the Add/Remove Programs registry entry for \"{app.DisplayName}\"?\r\n\r\n" +
            "This does NOT uninstall the program. It only hides it from this list.\r\n\r\n" +
            app.Location.ToString(),
            "Sweep", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        bool ok = RegistryScanner.DeleteEntry(app.Location);
        if (ok)
        {
            LogStore.Success($"Removed registry entry for \"{app.DisplayName}\".");
        }

        _ = LoadAppsAsync();
    }

    private void CopyDetails()
    {
        var app = SelectedApp();
        if (app is null)
        {
            return;
        }

        string text = string.Join(Environment.NewLine,
            $"Name:        {app.DisplayName}",
            $"Version:     {app.DisplayVersion}",
            $"Publisher:   {app.Publisher}",
            $"Scope:       {app.ScopeLabel}",
            $"Installed:   {app.InstallDate:yyyy-MM-dd}",
            $"Size:        {app.SizeDisplay}",
            $"Location:    {app.InstallLocation}",
            $"Uninstall:   {app.UninstallString}",
            $"Quiet:       {app.QuietUninstallString}",
            $"Registry:    {app.Location}");

        try
        {
            Clipboard.SetText(text);
            SetStatus("Program details copied to the clipboard.");
        }
        catch (Exception ex)
        {
            LogStore.Warn($"Clipboard copy failed: {ex.Message}");
        }
    }

    private void OpenInstallLocation(InstalledApp app)
    {
        string? path = app.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            OpenPath(Path.GetDirectoryName(path) ?? path, mustBeDirectory: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            OpenPath(app.InstallLocation, mustBeDirectory: true);
            return;
        }

        MessageBox.Show(this, "This program does not report an install folder.", "Sweep",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenPath(string? path, bool mustBeDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string target = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (mustBeDirectory && File.Exists(target))
        {
            target = Path.GetDirectoryName(target) ?? target;
        }

        if (Directory.Exists(target) || File.Exists(target))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Could not open {target}: {ex.Message}");
            }
        }
    }

    private void OpenQuarantineFolder()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Sweep",
            "Quarantine");

        if (!Directory.Exists(dir))
        {
            MessageBox.Show(this, "The quarantine folder is empty - nothing has been quarantined yet.", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        OpenPath(dir, mustBeDirectory: true);
    }

    private void RestoreFromQuarantine()
    {
        var roots = Quarantine.ListQuarantineRoots();
        if (roots.Count == 0)
        {
            MessageBox.Show(this, "There are no quarantined items to restore.", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Select a quarantine session to restore",
            CheckFileExists = true,
            FileName = "manifest.json",
            InitialDirectory = roots[0],
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            string? folder = Path.GetDirectoryName(dialog.FileName);
            if (string.IsNullOrEmpty(folder))
            {
                MessageBox.Show(this, "Select the manifest.json inside a quarantine folder.", "Sweep",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int restored = Quarantine.RestoreFrom(folder);
            int total = Quarantine.CountEntries(folder);
            LogStore.Success($"Restored {restored} of {total} quarantined item(s).");
            MessageBox.Show(this,
                restored == total
                    ? $"Restored {restored} item(s)."
                    : $"Restored {restored} of {total} item(s). The rest could not be moved back; see the Activity log.",
                "Sweep", MessageBoxButtons.OK,
                restored == total ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Restore failed:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportList()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv|Text file (*.txt)|*.txt",
            FileName = $"installed-programs-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            using var writer = new StreamWriter(dialog.FileName);
            writer.WriteLine("Name,Version,Publisher,Scope,Installed,Size,Location,RegistryKey");
            foreach (var app in _all)
            {
                writer.WriteLine(string.Join(',',
                    Csv(app.DisplayName), Csv(app.DisplayVersion), Csv(app.Publisher),
                    Csv(app.ScopeLabel), Csv(app.InstallDate?.ToString("yyyy-MM-dd") ?? ""),
                    Csv(app.SizeDisplay), Csv(app.InstallLocation), Csv(app.Location.ToString())));
            }

            LogStore.Success($"Exported {_all.Count} programs to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string Csv(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

    private void OpenForm<T>() where T : Form, new()
    {
        using var form = new T();
        form.ShowDialog(this);
    }

    private void ExportSettings()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Settings file (*.json)|*.json",
            FileName = $"sweep-settings-{DateTime.Now:yyyyMMdd}.json",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            SettingsStore.ExportTo(dialog.FileName, _settings);
            LogStore.Success($"Settings exported to {dialog.FileName}");
            SetStatus("Settings exported.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not export settings:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportSettings()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Settings file (*.json)|*.json",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _settings = SettingsStore.ImportFrom(dialog.FileName);
            SettingsStore.Save(_settings);
            Theme.ApplyMode(_settings.Theme);
            ApplyTheme();
            LogStore.Success("Settings imported.");
            SetStatus("Settings imported.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not import settings:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowSettings()
    {
        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings = dialog.Result;
        SettingsStore.Save(_settings);
        Theme.ApplyMode(_settings.Theme);
        ApplyTheme();
        LogStore.Info("Settings updated.");
    }

    private void SetTheme(ThemeMode mode)
    {
        if (_settings.Theme == mode)
        {
            return;
        }

        _settings.Theme = mode;
        SettingsStore.Save(_settings);
        Theme.ApplyMode(mode);
        ApplyTheme();
    }

    private void ShowActivity()
    {
        if (_activity is { IsDisposed: false })
        {
            _activity.Activate();
            return;
        }

        _activity = new ActivityForm();
        _activity.FormClosed += (_, _) => _activity = null;
        _activity.Show(this);
    }

    private void ShowAbout()
    {
        var assembly = typeof(MainForm).Assembly;
        string rawVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                            ?? assembly.GetName().Version?.ToString()
                            ?? "1.0.0";

        // The SDK appends the commit hash after '+'; only the product version is useful here.
        string info = rawVersion.Split('+')[0];

        string runtime = RuntimeInformation.FrameworkDescription;
        string arch = RuntimeInformation.ProcessArchitecture.ToString();
        string os = RuntimeInformation.OSDescription;
        bool arm64 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

        var text = new StringBuilder();
        text.AppendLine($"Sweep {info}");
        text.AppendLine();
        text.AppendLine($"Runtime     {runtime}");
        text.AppendLine($"Architecture{arch}{(arch == "Arm64" ? "  (native)" : string.Empty)}");
        text.AppendLine($"OS          {os}");
        text.AppendLine($"Snap layouts  {(arm64 ? "available" : "not available on this Windows version")}");
        text.AppendLine($"Elevated    {(Program.IsElevated ? "yes" : "no")}");
        text.AppendLine();
        text.AppendLine("Lists installed programs, runs their registered uninstaller,");
        text.AppendLine("then searches for leftover files, registry entries and system");
        text.AppendLine("artefacts. Leftovers go to a reversible quarantine folder by");
        text.AppendLine("default, and a shared deny-list refuses anything that belongs to");
        text.AppendLine("Windows or to another application.");
        text.AppendLine();
        text.AppendLine("Plain C# and WinForms. No third-party packages, no telemetry,");
        text.AppendLine("no cloud database.");
        text.AppendLine();
        text.AppendLine("https://github.com/Code-with-L/Sweep");
        text.AppendLine("MIT licence");

        MessageBox.Show(this, text.ToString(), "About Sweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---- Status ---------------------------------------------------------

    private void SetBusy(bool busy, string message = "")
    {
        _busy = busy;
        _progress.Visible = busy;

        if (message.Length > 0)
        {
            _statusLabel.Text = message;
        }

        UseWaitCursor = busy;
        UpdateSelection();
    }

    private void SetStatus(string message) => _statusLabel.Text = message;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _search.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F))
        {
            _search.Focus();
            _search.SelectAll();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }
}

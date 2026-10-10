using System.Text;

namespace Sweep;

internal sealed class MainForm : Form
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
    private bool _probeOpened;

    public MainForm(string? probeApp = null)
    {
        _probeApp = probeApp;
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
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(_menu, 0, 0);
        root.Controls.Add(toolbar, 0, 1);
        root.Controls.Add(_grid, 0, 2);
        root.Controls.Add(bottom, 0, 3);
        root.Controls.Add(status, 0, 4);

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
        _themeDarkItem = new ToolStripMenuItem("&Dark theme", null, (_, _) => SetTheme(ThemeMode.Dark));
        _themeLightItem = new ToolStripMenuItem("&Light theme", null, (_, _) => SetTheme(ThemeMode.Light));
        view.DropDownItems.AddRange([_themeDarkItem, _themeLightItem]);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(new ToolStripMenuItem("&Activity log...", null, (_, _) => ShowActivity())
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
        _grid.Columns.Add(new DataGridViewImageColumn { Name = "icon", Width = 46, Resizable = DataGridViewTriState.False });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Program", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "version", HeaderText = "Version", Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "publisher", HeaderText = "Publisher", Width = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "Size", Width = 95 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "installed", HeaderText = "Installed", Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "scope", HeaderText = "Scope", Width = 135 });

        _grid.CellValueNeeded += OnCellValueNeeded;
    }

    private void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _filtered.Count)
        {
            return;
        }

        var app = _filtered[e.RowIndex];
        string column = _grid.Columns[e.ColumnIndex].Name;

        e.Value = column switch
        {
            "icon" => IconProvider.Get(app),
            "name" => app.DisplayName,
            "version" => string.IsNullOrEmpty(app.DisplayVersion) ? "-" : app.DisplayVersion,
            "publisher" => string.IsNullOrEmpty(app.Publisher) ? "-" : app.Publisher,
            "size" => string.IsNullOrEmpty(app.SizeDisplay) ? "-" : app.SizeDisplay,
            "installed" => app.InstallDate?.ToString("yyyy-MM-dd") ?? "-",
            "scope" => app.ScopeLabel,
            _ => null,
        };
    }

    private void BuildContextMenu()
    {
        _contextMenu.Items.Add(new ToolStripMenuItem("Uninstall...", null, async (_, _) => await UninstallSelectedAsync()));
        _contextMenu.Items.Add(new ToolStripMenuItem("Scan leftovers...", null, async (_, _) => ScanSelected()));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem("Open install folder", null, (_, _) =>
        {
            var app = SelectedApp();
            if (app is not null)
            {
                OpenInstallLocation(app);
            }
        }));
        _contextMenu.Items.Add(new ToolStripMenuItem("Open data folder", null, (_, _) =>
        {
            var app = SelectedApp();
            if (app is not null)
            {
                OpenPath(app.InstallLocation, mustBeDirectory: true);
            }
        }));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem("Copy details", null, (_, _) => CopyDetails()));
        _contextMenu.Items.Add(new ToolStripMenuItem("Remove leftover registry entry", null, (_, _) => RemoveEntryOnly())
        {
            Name = "removeEntry",
        });

        _grid.ContextMenuStrip = _contextMenu;
        _contextMenu.Opening += (_, _) => UpdateSelection();
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
            var apps = await Task.Run(() => RegistryScanner.Scan());
            _all = apps;
            IconProvider.Clear();
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

        _filtered = query.Length == 0
            ? _all
            : _all.Where(a =>
                Contains(a.DisplayName, query) ||
                Contains(a.Publisher, query) ||
                Contains(a.InstallLocation, query) ||
                Contains(a.DisplayVersion, query)).ToList();

        _grid.RowCount = _filtered.Count;

        if (_filtered.Count > 0)
        {
            _grid.CurrentCell = _grid.Rows[0].Cells["name"];
        }

        UpdateSelection();
        PrefetchIcons();
    }

    private CancellationTokenSource? _iconCts;

    /// <summary>
    /// Icons are extracted off the UI thread. CellValueNeeded then only ever performs a
    /// dictionary lookup, so scrolling and repainting never touch the filesystem.
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
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Icon prefetch stopped: {ex.Message}");
                return;
            }

            if (token.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            try
            {
                BeginInvoke((Action)(() => _grid.Invalidate()));
            }
            catch (ObjectDisposedException)
            {
                // Window closed mid-prefetch.
            }
        }, token);
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private InstalledApp? SelectedApp()
    {
        int index = _grid.CurrentCell?.RowIndex ?? -1;
        return index >= 0 && index < _filtered.Count ? _filtered[index] : null;
    }

    private void UpdateSelection()
    {
        var app = SelectedApp();

        bool hasApp = app is not null;
        _uninstallButton.Enabled = hasApp && app!.HasUninstaller && !_busy;
        _scanButton.Enabled = hasApp && !_busy;
        _contextMenu.Items["removeEntry"]!.Enabled = hasApp;

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
        MessageBox.Show(this,
            $"Sweep {typeof(MainForm).Assembly.GetName().Version}\r\n" +
            $"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\r\n" +
            $"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\r\n" +
            $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\r\n\r\n" +
            "Lists installed programs, runs their registered uninstaller, then searches for\r\n" +
            "leftover files and registry entries. Leftovers are moved to a reversible quarantine\r\n" +
            "folder by default.",
            "About Sweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
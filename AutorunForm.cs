namespace Sweep;

public enum AutorunKind
{
    StartupEntry,
    Service,
    ScheduledTask,
}

public sealed record AutorunItem(
    AutorunKind Kind,
    string Name,
    string Command,
    string State,
    RegLocation Location,
    string? ValueName,
    string? FilePath)
{
    public string CommandDisplay => Command.Length > 100 ? Command[..100] + "..." : Command;
}

/// <summary>
/// Startup / autorun manager.
///
/// Disabling uses the real APIs - the Service Control Manager and schtasks - rather than
/// editing registry values, because SCM caches start types and a registry edit appears to
/// work and then silently reverts. Both changes are reversible.
/// </summary>
internal sealed class AutorunForm : ThemedForm
{
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
    };

    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Visible = false, Style = ProgressBarStyle.Marquee };
    private readonly Button _disableButton = new();
    private readonly Button _enableButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _openButton = new();
    private readonly CheckBox _includeServices = new() { Text = "Services", Checked = true, AutoSize = true };
    private readonly CheckBox _includeTasks = new() { Text = "Scheduled tasks", Checked = true, AutoSize = true };
    private readonly CheckBox _includeStartup = new() { Text = "Startup entries", Checked = true, AutoSize = true };
    private readonly TextBox _search = new();
    private List<AutorunItem> _all = [];

    public AutorunForm()
    {
        var p = Theme.Current;

        Text = "Startup entries";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1060, 620);
        MinimumSize = new Size(760, 440);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = p.Background;
        ForeColor = p.Text;

        Native.EnableDoubleBuffer(_list);
        Theme.ApplyToList(_list, p);
        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Type", 110);
        _list.Columns.Add("Name", 200);
        _list.Columns.Add("State", 90);
        _list.Columns.Add("Command", 600);

        var searchLabel = new Label { Text = "Search", AutoSize = true, Location = new Point(14, 15) };
        _search.Location = new Point(58, 11);
        _search.Size = new Size(230, 26);
        _search.TextChanged += (_, _) => Populate();
        Theme.StyleTextBox(_search, p);

        var header = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = p.Surface };
        header.Controls.AddRange([searchLabel, _search, _includeStartup, _includeServices, _includeTasks]);

        // AutoSize on a fixed panel overlaps as soon as the system font changes, so the
        // positions are computed from each control's measured width.
        int x = _search.Right + 16;
        _includeStartup.ForeColor = p.Text;
        header.Controls.Add(_includeStartup);
        x = LayoutChip(_includeStartup, x);

        _includeServices.ForeColor = p.Text;
        header.Controls.Add(_includeServices);
        x = LayoutChip(_includeServices, x);

        _includeTasks.ForeColor = p.Text;
        header.Controls.Add(_includeTasks);
        LayoutChip(_includeTasks, x);

        _includeStartup.CheckedChanged += (_, _) => Reload();
        _includeServices.CheckedChanged += (_, _) => Reload();
        _includeTasks.CheckedChanged += (_, _) => Reload();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 14, 10),
            BackColor = p.Surface,
        };

        _disableButton.Text = "Stop auto starting";
        _disableButton.Size = new Size(140, 32);
        _disableButton.Click += (_, _) => SetEnabled(false);

        _enableButton.Text = "Enable auto start";
        _enableButton.Size = new Size(130, 32);
        _enableButton.Click += (_, _) => SetEnabled(true);

        _openButton.Text = "Open location";
        _openButton.Size = new Size(110, 32);
        _openButton.Click += (_, _) => OpenLocation();

        _refreshButton.Text = "Refresh";
        _refreshButton.Size = new Size(90, 32);
        _refreshButton.Click += async (_, _) => await LoadAsync();

        var close = new Button { Text = "Close", Size = new Size(80, 32) };
        close.Click += (_, _) => Close();

        Theme.StyleButton(_disableButton, p, primary: true);
        Theme.StyleButton(_enableButton, p);
        Theme.StyleButton(_openButton, p);
        Theme.StyleButton(_refreshButton, p);
        Theme.StyleButton(close, p);

        toolbar.Controls.Add(close);
        toolbar.Controls.Add(_openButton);
        toolbar.Controls.Add(_disableButton);
        toolbar.Controls.Add(_enableButton);
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

        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        Shown += async (_, _) => await LoadAsync();
    }

    private bool _reloading;
    private int _generation;

    /// <summary>
    /// Positions a filter chip after the previous one. Fixed pixel offsets overlap as soon
    /// as the system font or DPI changes.
    /// </summary>
    private static int LayoutChip(Control chip, int x)
    {
        chip.Location = new Point(x, 14);
        return x + chip.PreferredSize.Width + 18;
    }

    private async void Reload()
    {
        if (_reloading || !IsHandleCreated)
        {
            return;
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _reloading = true;
        int generation = ++_generation;

        _status.Text = "Reading startup entries...";
        _progress.Visible = true;
        _refreshButton.Enabled = false;
        UseWaitCursor = true;

        bool services = _includeServices.Checked;
        bool tasks = _includeTasks.Checked;
        bool startup = _includeStartup.Checked;

        try
        {
            var collected = new List<AutorunItem>();

            if (startup)
            {
                foreach (var entry in SystemArtifacts.AllStartupEntries(CancellationToken.None))
                {
                    bool isFolderShortcut = entry.Location.SubKey is not { Length: > 0 };

                    collected.Add(new AutorunItem(
                        AutorunKind.StartupEntry,
                        entry.Name,
                        entry.Command,
                        "Enabled",
                        entry.Location,
                        isFolderShortcut ? null : entry.Name,
                        isFolderShortcut ? entry.Command : null));
                }
            }

            if (services)
            {
                foreach (var service in SystemArtifacts.AllServices(CancellationToken.None))
                {
                    collected.Add(new AutorunItem(
                        AutorunKind.Service,
                        service.Name,
                        service.ImagePath,
                        SystemArtifacts.ServiceStartMode(service.StartType),
                        default,
                        null,
                        null));
                }
            }

            if (tasks)
            {
                foreach (var task in SystemArtifacts.AllScheduledTasks(CancellationToken.None))
                {
                    collected.Add(new AutorunItem(
                        AutorunKind.ScheduledTask,
                        task.Name,
                        task.Action,
                        "Enabled",
                        default,
                        null,
                        null));
                }
            }

            // A slow scheduled-task query can outlive a filter toggle.
            if (generation != _generation || IsDisposed)
            {
                return;
            }

            _all = collected;
            Populate();
        }
        catch (Exception ex)
        {
            LogStore.Error("Could not read startup entries", ex);
            _status.Text = $"Could not read startup entries: {ex.Message}";
        }
        finally
        {
            _reloading = false;
            if (!IsDisposed)
            {
                _progress.Visible = false;
                _refreshButton.Enabled = true;
                UseWaitCursor = false;
            }
        }
    }

    private void UpdateButtons()
    {
        bool has = _list.SelectedItems.Count > 0;
        _disableButton.Enabled = has;
        _enableButton.Enabled = has;
        _openButton.Enabled = has;
    }

    private void Populate()
    {
        string query = _search.Text.Trim();
        var p = Theme.Current;

        var filtered = _all
            .Where(i => query.Length == 0 ||
                        i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        i.Command.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var item in filtered)
        {
            var view = new ListViewItem(item.Kind.ToString()) { Tag = item };
            view.SubItems.Add(item.Name);
            view.SubItems.Add(item.State);
            view.SubItems.Add(item.CommandDisplay);
            view.ForeColor = item.State == "Disabled" ? p.TextDim : p.Text;
            _list.Items.Add(view);
        }

        _list.EndUpdate();

        _status.Text = $"{filtered.Count} startup item(s).";
        UpdateButtons();
    }

    private void SetEnabled(bool enabled)
    {
        var selected = _list.SelectedItems.Cast<ListViewItem>()
            .Select(i => (View: i, Item: (AutorunItem)i.Tag!))
            .ToList();

        if (selected.Count == 0)
        {
            return;
        }

        var failures = new List<string>();
        int changed = 0;

        foreach (var (view, item) in selected)
        {
            bool ok;
            string newState;

            switch (item.Kind)
            {
                case AutorunKind.Service:
                    if (ServiceOps.SetStartMode(item.Name, enabled, out string error))
                    {
                        ok = true;
                        newState = SystemArtifacts.ServiceStartMode(
                            enabled ? SystemArtifacts.ServiceAutoStartValue : SystemArtifacts.ServiceDisabledValue);
                    }
                    else
                    {
                        failures.Add($"{item.Name}: {error}");
                        ok = false;
                        newState = item.State;
                    }

                    break;

                case AutorunKind.ScheduledTask:
                    if (SetTaskEnabled(item.Name, enabled, out string taskError))
                    {
                        ok = true;
                        newState = enabled ? "Enabled" : "Disabled";
                    }
                    else
                    {
                        failures.Add($"{item.Name}: {taskError}");
                        ok = false;
                        newState = item.State;
                    }

                    break;

                default:
                    // Run/RunOnce entries have no native "disabled" state. Windows only
                    // offers StartupApproved, which is fragile and undocumented, so this is
                    // reported rather than faked.
                    failures.Add($"{item.Name}: startup entries have no disable state. " +
                                 "Remove the entry instead, or disable it in Task Manager.");
                    ok = false;
                    newState = item.State;
                    break;
            }

            if (ok)
            {
                changed++;
                int index = _all.FindIndex(x => x.Kind == item.Kind && x.Name == item.Name);
                if (index >= 0)
                {
                    _all[index] = item with { State = newState };
                }
            }
        }

        if (failures.Count > 0)
        {
            MessageBox.Show(this,
                failures.Count == selected.Count
                    ? "Nothing was changed:\r\n\r\n" + string.Join("\r\n", failures.Take(8))
                    : $"{failures.Count} change(s) failed:\r\n\r\n" + string.Join("\r\n", failures.Take(8)),
                "Sweep", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        if (changed > 0)
        {
            LogStore.Success($"{(enabled ? "Enabled" : "Disabled")} {changed} startup item(s).");
        }

        Populate();
    }

    private static bool SetTaskEnabled(string taskName, bool enabled, out string error)
    {
        error = string.Empty;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/Change");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(taskName);
            psi.ArgumentList.Add(enabled ? "/ENABLE" : "/DISABLE");

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                error = "could not start schtasks";
                return false;
            }

            process.WaitForExit(30_000);
            if (process.ExitCode == 0)
            {
                return true;
            }

            error = $"schtasks exited with {process.ExitCode}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void OpenLocation()
    {
        if (_list.SelectedItems.Count == 0)
        {
            return;
        }

        var item = (AutorunItem)_list.SelectedItems[0].Tag!;
        string? path = item.FilePath;

        if (path is null)
        {
            path = SystemArtifacts.MatchSegmented(
                item.Command,
                new ResidueTokenSet([], [], string.Empty),
                allowWeak: false) is null
                ? FirstExistingSegment(item.Command)
                : null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Could not work out a location for this entry.", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open that location:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Pulls the first quoted or path-shaped token out of a command line.</summary>
    private static string? FirstExistingSegment(string command)
    {
        foreach (char c in new[] { '\\', '/', ':', '"', ' ', '\t' })
        {
            command = command.Replace(c.ToString(), "");
        }

        foreach (string segment in command.Split('', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(segment) || Directory.Exists(segment))
                {
                    return segment;
                }
            }
            catch
            {
                // Ignore malformed segments.
            }
        }

        return null;
    }
}
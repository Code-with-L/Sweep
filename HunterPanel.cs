using System.Diagnostics;

namespace Sweep;

/// <summary>
/// Hunter Mode: every running process, with the details needed to decide what to do about it.
///
/// Revo's version is a drag-a-crosshair-onto-an-icon interaction that hides the main window.
/// That is a desktop-shell gesture rather than a list, and it is genuinely useful for the
/// case it targets: a tray app you cannot identify. This is the list form of the same idea,
/// so the information is there without a mode you have to learn.
///
/// Commands that stop, kill or disable a process all act on the selection.
/// </summary>
internal sealed class HunterPanel : Panel
{
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = true,
    };

    private readonly TextBox _search = new();
    private readonly Button _kill = new();
    private readonly Button _stopAutoStart = new();
    private readonly Button _openFolder = new();
    private readonly Button _refresh = new();
    private readonly Label _status = new();
    private List<RunningProcessInfo> _all = [];
    private bool _loaded;

    /// <summary>Set by the host so a command can hand off to another module.</summary>
    public void SetNavigator(Action<string> navigate) => _navigate = navigate;

    public HunterPanel()
    {
        var p = Theme.Current;

        Dock = DockStyle.Fill;
        BackColor = p.Background;
        ForeColor = p.Text;

        Native.EnableDoubleBuffer(_list);
        Theme.ApplyToList(_list, p);
        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Process", 240);
        _list.Columns.Add("PID", 70);
        _list.Columns.Add("Executable", 320);

        // Sized to fill rather than -1. The header strip past the final column is not drawn by
        // owner-draw and stayed white against the dark theme.
        _list.Columns.Add("Window", 200);
        _list.SizeChanged += (_, _) => FitColumns();

        _search.Dock = DockStyle.Top;
        _search.Height = 30;
        _search.PlaceholderText = "Search processes";
        _search.BorderStyle = BorderStyle.FixedSingle;
        Theme.StyleTextBox(_search, p);
        _search.TextChanged += (_, _) => Populate();

        _kill.Text = "Kill";
        _kill.Click += (_, _) => KillSelected();
        _stopAutoStart.Text = "Stop auto starting";
        _stopAutoStart.Click += (_, _) => StopAutoStart();
        _openFolder.Text = "Open containing folder";
        _openFolder.Click += (_, _) => OpenFolder();
        _refresh.Text = "Refresh";
        _refresh.Click += (_, _) => _ = LoadAsync();

        foreach (Button button in new[] { _kill, _stopAutoStart, _openFolder, _refresh })
        {
            button.Size = new Size(Math.Max(90, TextRenderer.MeasureText(button.Text, button.Font).Width + 44), 32);
        }

        Theme.StyleButton(_kill, p, primary: true);
        Theme.StyleButton(_stopAutoStart, p);
        Theme.StyleButton(_openFolder, p);
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
        toolbar.Controls.Add(_openFolder);
        toolbar.Controls.Add(_stopAutoStart);
        toolbar.Controls.Add(_kill);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = p.TextDim;
        _status.Padding = new Padding(12, 0, 0, 0);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = p.Surface };
        footer.Controls.Add(_status);

        // Added last so it docks first: list fills, search sits on top of it.
        Controls.Add(_list);
        Controls.Add(_search);
        Controls.Add(footer);
        Controls.Add(toolbar);

        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        Populate();
    }

    /// <summary>Gives the window-title column whatever width the other three leave over.</summary>
    private void FitColumns()
    {
        if (_list.Columns.Count == 0)
        {
            return;
        }

        int used = 0;
        for (int i = 0; i < _list.Columns.Count - 1; i++)
        {
            used += _list.Columns[i].Width;
        }

        // A little more slack than strictly needed: the horizontal scrollbar appears as soon as the
        // columns total the client width exactly.
        int available = _list.ClientSize.Width - used - 10;
        if (available > 80)
        {
            _list.Columns[^1].Width = available;
        }
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _status.Text = "Reading running processes...";

        try
        {
            // Reading process info touches the kernel, so it stays off the UI thread.
            var found = await Task.Run(() => Process.GetProcesses()
                .Select(proc =>
                {
                    string path = string.Empty;
                    string title = string.Empty;

                    try
                    {
                        path = ProcessHunter.DescribePath(proc);
                    }
                    catch
                    {
                        // Access denied for processes owned by other accounts.
                    }

                    try
                    {
                        title = proc.MainWindowTitle;
                    }
                    catch
                    {
                        // The window can disappear between enumerating and asking.
                    }

                    return new RunningProcessInfo(proc.Id, proc.ProcessName, path, title);
                })
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList());

            _all = found;
            Populate();
            LogStore.Info($"Hunter Mode listed {found.Count} processes.");
        }
        catch (Exception ex)
        {
            LogStore.Error("Could not read running processes", ex);
            _status.Text = $"Could not read processes: {ex.Message}";
        }
    }

    private void Populate()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            string query = _search.Text.Trim();

            foreach (RunningProcessInfo proc in _all.Where(p =>
                query.Length == 0 ||
                p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                p.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            {
                var item = new ListViewItem(proc.Name) { Tag = proc };
                item.SubItems.Add(proc.ProcessId.ToString());
                item.SubItems.Add(proc.Path.Length == 0 ? "-" : proc.Path);
                item.SubItems.Add(proc.WindowTitle);
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _status.Text = _list.Items.Count == _all.Count
            ? $"{_all.Count} processes"
            : $"{_list.Items.Count} of {_all.Count} processes";

        UpdateButtons();
    }

    private List<RunningProcessInfo> Selected()
    {
        var selected = new List<RunningProcessInfo>();

        foreach (ListViewItem item in _list.SelectedItems)
        {
            if (item.Tag is RunningProcessInfo proc)
            {
                selected.Add(proc);
            }
        }

        return selected;
    }

    private void UpdateButtons()
    {
        bool any = _list.SelectedItems.Count > 0;
        _kill.Enabled = any;
        _stopAutoStart.Enabled = any;
        _openFolder.Enabled = any && Selected().Any(p => !string.IsNullOrEmpty(p.Path));
    }

    private void KillSelected()
    {
        List<RunningProcessInfo> selected = Selected();
        if (selected.Count == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            selected.Count == 1
                ? $"Stop \"{selected[0].Name}\"? Anything it has not saved is lost."
                : $"Stop {selected.Count} processes? Anything they have not saved is lost.",
            "Sweep - Hunter Mode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        int stopped = 0;
        foreach (RunningProcessInfo proc in selected)
        {
            try
            {
                using Process process = Process.GetProcessById(proc.ProcessId);
                process.Kill();
                stopped++;
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Could not stop {proc.Name} ({proc.ProcessId}): {ex.Message}");
            }
        }

        LogStore.Success($"Stopped {stopped} process(es).");
        _ = LoadAsync();
    }

    /// <summary>
    /// Sends the user to the Autorun Manager, which is where startup entries are disabled.
    ///
    /// Doing it inline here would duplicate that logic and give two code paths for the same
    /// change; the Autorun Manager already knows how to disable an entry through the Service
    /// Control Manager and schtasks rather than by editing Run values.
    /// </summary>
    private void StopAutoStart()
    {
        List<RunningProcessInfo> selected = Selected().Where(p => !string.IsNullOrEmpty(p.Path)).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "None of the selected processes has a known location.", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        MessageBox.Show(
            this,
            "Startup entries are managed in the Autorun Manager, which can disable them " +
            "properly rather than only removing them from Run keys.\r\n\r\n" +
            $"{selected.Count} selected process(es) will be listed there by name.",
            "Sweep", MessageBoxButtons.OK, MessageBoxIcon.Information);

        _navigate?.Invoke("autorun");
    }

    private Action<string>? _navigate;

    private void OpenFolder()
    {
        string? path = Selected().Select(p => p.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            LogStore.Error($"Could not open {path}", ex);
            MessageBox.Show(this, $"Could not open that folder:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
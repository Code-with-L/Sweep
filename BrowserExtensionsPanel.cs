using System.Diagnostics;

namespace Sweep;

/// <summary>
/// Browser Extensions: what is installed in each profile, which browser keeps it, and where
/// it lives. Chromium extensions can be quarantined (the folder is the extension); Firefox
/// is read-only because its state is held in the profile database, not in a folder.
/// </summary>
internal sealed class BrowserExtensionsPanel : Panel
{
    private readonly ListView _list = new();
    private readonly Button _refresh = new();
    private readonly Button _open = new();
    private readonly Button _remove = new();
    private readonly Label _status = new();

    private IReadOnlyList<BrowserExtension> _extensions = [];
    private bool _loaded;
    private bool _busy;

    public BrowserExtensionsPanel()
    {
        var p = Theme.Current;

        Dock = DockStyle.Fill;
        BackColor = p.Background;
        ForeColor = p.Text;

        Native.EnableDoubleBuffer(_list);
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.ShowItemToolTips = true;
        _list.Columns.Add("Browser", 100);
        _list.Columns.Add("Extension", 260);
        _list.Columns.Add("Version", 90);
        _list.Columns.Add("Profile", 110);
        _list.Columns.Add("State", 90);
        _list.Columns.Add("Location", 360);
        Theme.ApplyToList(_list, p);

        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.SizeChanged += (_, _) => FitColumns();
        _list.DoubleClick += (_, _) => OpenFolder();

        _refresh.Text = "Refresh";
        _open.Text = "Open folder";
        _remove.Text = "Remove";

        foreach (Button button in new[] { _refresh, _open, _remove })
        {
            button.Height = 32;
            button.Width = Math.Max(96, TextRenderer.MeasureText(button.Text, button.Font).Width + 44);
            Theme.StyleButton(button, p);
        }

        Theme.StyleButton(_remove, p, primary: true);

        _refresh.Click += async (_, _) => await LoadAsync();
        _open.Click += (_, _) => OpenFolder();
        _remove.Click += async (_, _) => await RemoveAsync();

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
        bar.Controls.Add(_open);
        bar.Controls.Add(_remove);
        bar.Controls.Add(_status);

        Controls.Add(_list);
        Controls.Add(bar);

        _status.Text = "Loading extensions...";
        FitColumns();
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
        if (_busy)
        {
            return;
        }

        _busy = true;
        UseWaitCursor = true;
        _refresh.Enabled = false;
        _status.Text = "Reading browser profiles...";

        try
        {
            _extensions = await Task.Run(() => BrowserExtensions.Scan());
            Populate();
        }
        catch (Exception ex)
        {
            LogStore.Error("Could not read browser extensions", ex);
            _status.Text = "Could not read extensions: " + ex.Message;
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            _refresh.Enabled = true;
            UpdateButtons();
        }
    }

    private void Populate()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();

            foreach (BrowserExtension extension in _extensions)
            {
                var item = new ListViewItem(extension.Browser) { Tag = extension };
                item.SubItems.Add(extension.Name);
                item.SubItems.Add(extension.Version);
                item.SubItems.Add(extension.Profile);
                item.SubItems.Add(extension.State);
                item.SubItems.Add(extension.Path);
                item.ToolTipText = extension.Removable
                    ? "Chromium-style extension folder."
                    : "Firefox manages this extension in its profile database.";
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        int removable = _extensions.Count(extension => extension.Removable);
        _status.Text = _extensions.Count == 0
            ? "No extensions found."
            : $"{_extensions.Count} extension(s) across {_extensions.Select(e => e.Browser).Distinct().Count()} browser(s). " +
              $"{removable} can be removed here; Firefox entries are managed in the browser.";
    }

    private void UpdateButtons()
    {
        bool hasSelection = _list.SelectedItems.Count > 0;
        _open.Enabled = hasSelection && !_busy;
        _remove.Enabled = hasSelection && !_busy &&
            _list.SelectedItems.Cast<ListViewItem>()
                .All(item => item.Tag is BrowserExtension { Removable: true });
    }

    private List<BrowserExtension> Selected() =>
        _list.SelectedItems.Cast<ListViewItem>()
            .Where(item => item.Tag is BrowserExtension)
            .Select(item => (BrowserExtension)item.Tag!)
            .ToList();

    private void OpenFolder()
    {
        BrowserExtension? extension = Selected().FirstOrDefault();
        if (extension is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = extension.Path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open that folder:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task RemoveAsync()
    {
        List<BrowserExtension> selected = Selected();
        if (selected.Count == 0 || _busy)
        {
            return;
        }

        if (selected.Any(extension => !extension.Removable))
        {
            MessageBox.Show(this, "Firefox extensions are removed from inside Firefox.", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (string browser in selected.Select(extension => extension.Browser).Distinct())
        {
            string? running = BrowserExtensions.RunningProcess(browser);
            if (running is not null)
            {
                MessageBox.Show(this,
                    $"{browser} is running ({running}).\r\n\r\n" +
                    "Close it first: an extension folder cannot be moved while the browser holds it open.",
                    "Sweep - Browser Extensions",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        var names = string.Join(", ", selected.Take(3).Select(extension => extension.Name));
        if (selected.Count > 3)
        {
            names += $" and {selected.Count - 3} more";
        }

        var answer = MessageBox.Show(this,
            $"Remove {selected.Count} extension(s)?\r\n\r\n{names}\r\n\r\n" +
            "The extension folders are moved to quarantine so they can be restored from the " +
            "Backup Manager. The browser may download a synced extension again the next time " +
            "it starts.",
            "Sweep - Browser Extensions",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        _busy = true;
        UseWaitCursor = true;
        UpdateButtons();

        try
        {
            var result = await Task.Run(() =>
            {
                var quarantine = new Quarantine("browser extensions");
                int removed = 0;
                var failures = new List<string>();

                foreach (BrowserExtension extension in selected)
                {
                    DeletionResult deletion = quarantine.QuarantineDirectory(extension.Path);
                    if (deletion.Success)
                    {
                        removed++;
                    }
                    else
                    {
                        failures.Add($"{extension.Name}: {deletion.Error}");
                    }
                }

                bool manifest = quarantine.Count == 0 || quarantine.SaveManifest(out _);
                return (Removed: removed, Failures: failures, ManifestOk: manifest, Root: quarantine.Root);
            });

            LogStore.Success($"Removed {result.Removed} browser extension(s).");

            var message = new System.Text.StringBuilder();
            message.AppendLine($"Moved {result.Removed} extension folder(s) to quarantine.");

            if (result.Root.Length > 0)
            {
                message.AppendLine($"Backup: {result.Root}");
            }

            if (!result.ManifestOk)
            {
                message.AppendLine();
                message.AppendLine("WARNING: the quarantine manifest could not be written, so these cannot be restored automatically.");
            }

            if (result.Failures.Count > 0)
            {
                message.AppendLine();
                message.AppendLine("Could not move:");
                foreach (string failure in result.Failures.Take(8))
                {
                    message.AppendLine("  " + failure);
                }
            }

            MessageBox.Show(this, message.ToString(), "Sweep - Browser Extensions",
                MessageBoxButtons.OK,
                result.Failures.Count > 0 || !result.ManifestOk
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogStore.Error("Could not remove browser extensions", ex);
            MessageBox.Show(this, $"Removal failed:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            await LoadAsync();
        }
    }

    private void FitColumns()
    {
        if (_list.Columns.Count < 6)
        {
            return;
        }
        int fixedWidth =
            _list.Columns[0].Width +
            _list.Columns[1].Width +
            _list.Columns[2].Width +
            _list.Columns[3].Width +
            _list.Columns[4].Width;

        _list.Columns[5].Width = Math.Max(220, _list.ClientSize.Width - fixedWidth );
    }
}

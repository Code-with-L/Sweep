using System.Diagnostics;

namespace Sweep;

/// <summary>
/// Backup Manager: the quarantine sessions taken before removals.
///
/// This is the undo history of the app, not a second backup system: every removal made with
/// quarantine enabled already writes a session and a manifest. All this panel does is show
/// them in one place and put Restore, Open and Delete within reach.
/// </summary>
internal sealed class BackupManagerPanel : Panel
{
    private readonly ListView _list = new();
    private readonly Button _restore = new();
    private readonly Button _open = new();
    private readonly Button _delete = new();
    private readonly Button _refresh = new();
    private readonly Label _status = new();

    private IReadOnlyList<QuarantineSession> _sessions = [];
    private bool _loaded;
    private bool _busy;

    public BackupManagerPanel()
    {
        var p = Theme.Current;

        Dock = DockStyle.Fill;
        BackColor = p.Background;
        ForeColor = p.Text;

        Native.EnableDoubleBuffer(_list);
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.ShowItemToolTips = true;
        _list.Columns.Add("Application", 210);
        _list.Columns.Add("Created", 140);
        _list.Columns.Add("Items", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Size", 100, HorizontalAlignment.Right);
        _list.Columns.Add("Location", 380);
        Theme.ApplyToList(_list, p);

        _list.DoubleClick += (_, _) => OpenFolder();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.SizeChanged += (_, _) => FitColumns();

        _restore.Text = "Restore";
        _open.Text = "Open folder";
        _delete.Text = "Delete backup";
        _refresh.Text = "Refresh";

        foreach (Button button in new[] { _restore, _open, _delete, _refresh })
        {
            button.Height = 32;
            button.Width = Math.Max(96, TextRenderer.MeasureText(button.Text, button.Font).Width + 44);
            Theme.StyleButton(button, p);
        }

        // Deliberately not the "primary" style: with no backup selected the button is
        // disabled, and a bright blue disabled button still reads as clickable.
        _restore.Click += async (_, _) => await RestoreAsync();
        _open.Click += (_, _) => OpenFolder();
        _delete.Click += async (_, _) => await DeleteAsync();
        _refresh.Click += async (_, _) => await LoadAsync();

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

        bar.Controls.Add(_restore);
        bar.Controls.Add(_open);
        bar.Controls.Add(_delete);
        bar.Controls.Add(_refresh);
        bar.Controls.Add(_status);

        Controls.Add(_list);
        Controls.Add(bar);

        _status.Text = "Loading backups...";
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
        _status.Text = "Measuring backups...";
        _refresh.Enabled = false;

        try
        {
            _sessions = await Task.Run(() => Quarantine.Sessions());
            Populate();
        }
        catch (Exception ex)
        {
            LogStore.Error("Could not list backups", ex);
            _status.Text = "Could not list backups: " + ex.Message;
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

            foreach (QuarantineSession session in _sessions)
            {
                var item = new ListViewItem(session.App) { Tag = session };
                item.SubItems.Add(session.CreatedDisplay);
                item.SubItems.Add(session.Items.ToString("N0"));
                item.SubItems.Add(session.SizeDisplay);
                item.SubItems.Add(session.Root);
                item.ToolTipText = session.Items == 0
                    ? "This session has a manifest but no recorded items."
                    : $"{session.Items} item(s) in {session.Root}";
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        long bytes = _sessions.Sum(session => session.Bytes);
        _status.Text = _sessions.Count == 0
            ? "No backups yet - removals made with quarantine enabled appear here."
            : $"{_sessions.Count} backup(s), {SizeCalculator.Format(bytes)}. " +
              "Restoring puts items back where they came from.";

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool hasSelection = Selected() is not null;
        _restore.Enabled = hasSelection && !_busy;
        _open.Enabled = hasSelection && !_busy;
        _delete.Enabled = hasSelection && !_busy;
    }

    private QuarantineSession? Selected() =>
        _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as QuarantineSession : null;

    private void OpenFolder()
    {
        QuarantineSession? session = Selected();
        if (session is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = session.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open that folder:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task RestoreAsync()
    {
        QuarantineSession? session = Selected();
        if (session is null || _busy)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Restore the backup taken for \"{session.App}\" on {session.CreatedDisplay}?\r\n\r\n" +
            $"{session.Items} item(s) will be moved back to their original locations. " +
            "Anything that already exists at a destination is left alone.",
            "Sweep - Restore backup",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
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
            int restored = await Task.Run(() => Quarantine.RestoreFrom(session.Root));

            LogStore.Success(
                $"Restored {restored} of {session.Items} item(s) for \"{session.App}\" from {session.Root}.");

            MessageBox.Show(this,
                restored == session.Items
                    ? $"Restored {restored} item(s)."
                    : $"Restored {restored} of {session.Items} item(s). " +
                      "The rest could not be moved back; see the Activity log.",
                "Sweep - Restore backup",
                MessageBoxButtons.OK,
                restored == session.Items ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            LogStore.Error("Could not restore backup", ex);
            MessageBox.Show(this, $"Restore failed:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            await LoadAsync();
        }
    }

    private async Task DeleteAsync()
    {
        QuarantineSession? session = Selected();
        if (session is null || _busy)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Permanently delete this backup?\r\n\r\n" +
            $"\"{session.App}\", {session.CreatedDisplay}, {session.Items} item(s), " +
            $"{session.SizeDisplay}.\r\n\r\n" +
            "The quarantined files are destroyed and cannot be restored afterwards.",
            "Sweep - Delete backup",
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
            await Task.Run(() => Directory.Delete(session.Root, recursive: true));
            LogStore.Success($"Deleted the backup for \"{session.App}\" ({session.Root}).");
        }
        catch (Exception ex)
        {
            LogStore.Error("Could not delete backup", ex);
            MessageBox.Show(this, $"Could not delete that backup:\r\n{ex.Message}", "Sweep",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        if (_list.Columns.Count < 5)
        {
            return;
        }
        int fixedWidth =
            _list.Columns[0].Width +
            _list.Columns[1].Width +
            _list.Columns[2].Width +
            _list.Columns[3].Width;

        _list.Columns[4].Width = Math.Max(240, _list.ClientSize.Width - fixedWidth );
    }
}

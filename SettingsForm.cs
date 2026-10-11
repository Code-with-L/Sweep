namespace Sweep;

internal sealed class SettingsForm : ThemedForm
{
    private readonly AppSettings _settings;
    private readonly CheckBox _silent = new();
    private readonly CheckBox _restore = new();
    private readonly CheckBox _quarantine = new();
    private readonly CheckBox _scanRegistry = new();
    private readonly CheckBox _publisherTokens = new();
    private readonly NumericUpDown _fileDepth = new() { Minimum = 1, Maximum = 6 };
    private readonly NumericUpDown _regDepth = new() { Minimum = 1, Maximum = 5 };
    private readonly NumericUpDown _fileSeconds = new() { Minimum = 10, Maximum = 600, Increment = 10 };
    private readonly NumericUpDown _maxFile = new() { Minimum = 50, Maximum = 20000, Increment = 50 };
    private readonly NumericUpDown _maxReg = new() { Minimum = 20, Maximum = 5000, Increment = 20 };
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        var p = Theme.Current;

        Text = "Sweep settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 470);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = p.Background;
        ForeColor = p.Text;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(18, 16, 18, 8),
            BackColor = p.Background,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        void AddSection(string text)
        {
            var header = new Label
            {
                Text = text.ToUpperInvariant(),
                ForeColor = p.Accent,
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 10, 0, 6),
            };
            table.Controls.Add(header, 0, table.RowCount++);
            table.SetColumnSpan(header, 3);
        }

        void AddCheck(CheckBox box, string text, string hint, bool value)
        {
            box.Text = text;
            box.Checked = value;
            box.ForeColor = p.Text;
            box.AutoSize = true;
            table.Controls.Add(box, 0, table.RowCount++);
            table.SetColumnSpan(box, 3);

            var help = new Label
            {
                Text = hint,
                ForeColor = p.TextDim,
                AutoSize = true,
                Margin = new Padding(22, 0, 0, 6),
            };
            table.Controls.Add(help, 0, table.RowCount++);
            table.SetColumnSpan(help, 3);
        }

        void AddNumeric(NumericUpDown upDown, string label, int value, string hint)
        {
            var lbl = new Label { Text = label, ForeColor = p.Text, AutoSize = true, Anchor = AnchorStyles.Left };
            upDown.Value = Math.Clamp(value, (int)upDown.Minimum, (int)upDown.Maximum);
            upDown.Width = 100;
            upDown.BackColor = p.SurfaceAlt;
            upDown.ForeColor = p.Text;

            table.Controls.Add(lbl, 0, table.RowCount);
            table.Controls.Add(upDown, 1, table.RowCount);
            table.RowCount++;

            var help = new Label { Text = hint, ForeColor = p.TextDim, AutoSize = true, Anchor = AnchorStyles.Left };
            table.Controls.Add(help, 2, table.RowCount);
            table.RowCount++;
        }

        AddSection("Uninstalling");
        AddCheck(_silent, "Prefer silent uninstall", "Adds the vendor's documented quiet switches where they are known.", settings.SilentUninstall);
        AddCheck(_restore, "Create a restore point first", "Adds a few seconds to each removal and can fail if System Protection is off.", settings.CreateRestorePoint);

        AddSection("Leftover scan");
        AddCheck(_scanRegistry, "Scan the registry too", "Also looks under HKCU\\Software and the machine hive for leftover keys.", settings.ScanRegistry);
        AddCheck(_publisherTokens, "Match publisher names", "Catches more, but produces noticeably more false positives.", settings.UsePublisherTokens);
        AddCheck(_quarantine, "Move leftovers to quarantine instead of deleting", "Reversible from the Quarantine menu.", settings.UseQuarantine);
        AddNumeric(_fileDepth, "Folder scan depth", settings.FileScanDepth, "Levels below each root folder.");
        AddNumeric(_fileSeconds, "File scan time limit (s)", settings.FileScanSeconds, "Partial results are clearly marked.");
        AddNumeric(_maxFile, "Max file matches", settings.MaxFileMatches, "Protects against runaway results.");
        AddNumeric(_regDepth, "Registry scan depth", settings.RegistryScanDepth, "Shallow is faster and usually sufficient.");
        AddNumeric(_maxReg, "Max registry matches", settings.MaxRegistryMatches, string.Empty);

        AddSection("Appearance");
        _theme.Items.AddRange(["Dark", "Light"]);
        _theme.SelectedIndex = settings.Theme == ThemeMode.Dark ? 0 : 1;
        Theme.StyleComboBox(_theme, p);
        var themeLabel = new Label { Text = "Theme", ForeColor = p.Text, AutoSize = true, Anchor = AnchorStyles.Left };
        table.Controls.Add(themeLabel, 0, table.RowCount);
        table.Controls.Add(_theme, 1, table.RowCount);
        table.RowCount++;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 54,
            Padding = new Padding(0, 8, 14, 8),
            BackColor = p.Background,
        };

        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Size = new Size(90, 32) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(90, 32) };
        Theme.StyleButton(save, p, primary: true);
        Theme.StyleButton(cancel, p);

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(table);
        Controls.Add(buttons);
    }

    public AppSettings Result => new()
    {
        SilentUninstall = _silent.Checked,
        CreateRestorePoint = _restore.Checked,
        ScanRegistry = _scanRegistry.Checked,
        UsePublisherTokens = _publisherTokens.Checked,
        UseQuarantine = _quarantine.Checked,
        FileScanDepth = (int)_fileDepth.Value,
        RegistryScanDepth = (int)_regDepth.Value,
        FileScanSeconds = (int)_fileSeconds.Value,
        MaxFileMatches = (int)_maxFile.Value,
        MaxRegistryMatches = (int)_maxReg.Value,
        Theme = _theme.SelectedIndex == 0 ? ThemeMode.Dark : ThemeMode.Light,
        MinimizeToTray = _settings.MinimizeToTray,
    };
}
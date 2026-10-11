namespace Sweep;

/// <summary>Confirmation shown before an uninstall is launched.</summary>
internal sealed class UninstallConfirmDialog : ThemedForm
{
    private readonly CheckBox _silent = new() { Text = "Run uninstaller silently where supported", AutoSize = true };
    private readonly CheckBox _restore = new() { Text = "Create a system restore point first", AutoSize = true };

    public UninstallConfirmDialog(InstalledApp app, UninstallPlan plan, AppSettings settings)
    {
        var p = Theme.Current;

        Text = "Confirm removal";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 340);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = p.Background;
        ForeColor = p.Text;

        var title = new Label
        {
            Text = app.DisplayName,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 14f, FontStyle.Bold),
            ForeColor = p.Text,
            AutoSize = false,
            Bounds = new Rectangle(20, 18, 520, 28),
        };

        var details = new Label
        {
            Text = string.Join(Environment.NewLine,
                $"Publisher:   {Or(app.Publisher)}",
                $"Version:     {Or(app.DisplayVersion)}",
                $"Scope:       {app.ScopeLabel}",
                $"Size:        {Or(app.SizeDisplay, "not reported")}",
                $"Uninstaller: {UninstallRunner.Describe(plan.Family)}",
                $"Command:     {Truncate(plan.FileName + " " + plan.Arguments, 70)}"),
            ForeColor = p.TextDim,
            Bounds = new Rectangle(22, 52, 516, 120),
        };

        var planNote = new Label
        {
            Text = plan.Note,
            ForeColor = p.TextDim,
            Bounds = new Rectangle(22, 176, 516, 40),
        };

        _silent.Checked = settings.SilentUninstall && plan.Silent;
        _silent.ForeColor = p.Text;
        _silent.Bounds = new Rectangle(22, 222, 400, 22);

        _restore.Checked = settings.CreateRestorePoint;
        _restore.ForeColor = p.Text;
        _restore.Bounds = new Rectangle(22, 248, 400, 22);

        var warning = new Label
        {
            Text = plan.Silent
                ? "The uninstaller will run without prompts. Some apps keep running in the background until you reboot."
                : "The app's own uninstaller window will open. Follow its prompts.",
            ForeColor = p.Warning,
            Bounds = new Rectangle(22, 278, 516, 34),
        };

        var ok = new Button { Text = "Uninstall", DialogResult = DialogResult.OK, Bounds = new Rectangle(370, 296, 84, 32) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(460, 296, 84, 32) };

        Theme.StyleButton(ok, p, primary: true);
        Theme.StyleButton(cancel, p);

        AcceptButton = ok;
        CancelButton = cancel;

        Controls.AddRange([title, details, planNote, _silent, _restore, warning, ok, cancel]);
    }

    public bool RunSilent => _silent.Checked;

    public bool CreateRestorePoint => _restore.Checked;

    private static string Or(string value, string fallback = "not reported") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
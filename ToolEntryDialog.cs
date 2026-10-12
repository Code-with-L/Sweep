namespace Sweep;

/// <summary>Collects the three fields needed to add a custom entry to Windows Tools.</summary>
internal sealed class ToolEntryDialog : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _command = new();
    private readonly TextBox _category = new();

    public ToolEntryDialog()
    {
        Text = "Add a tool";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(520, 230);
        BackColor = Theme.Current.Surface;
        ForeColor = Theme.Current.Text;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(16),
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(layout, 0, "Name", _name, "System Restore");
        AddRow(layout, 1, "Command", _command, @"%SystemRoot%\system32\cleanmgr.exe");
        AddRow(layout, 2, "Category", _category, "Maintenance");

        var ok = new Button { Text = "Add", DialogResult = DialogResult.OK, Width = 90, Height = 30 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
        Theme.StyleButton(ok, Theme.Current, primary: true);
        Theme.StyleButton(cancel, Theme.Current);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(0, 8, 0, 0),
        };

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(layout);
        Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;

        // A tool with no command cannot be run, so do not let OK through without one.
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_command.Text))
            {
                MessageBox.Show(this, "A tool needs at least a name and a command.", "Sweep",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                ok.DialogResult = DialogResult.None;
            }
        };
    }

    private void AddRow(TableLayoutPanel layout, int row, string label, Control field, string placeholder)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var caption = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Current.TextDim,
            Padding = new Padding(0, 0, 4, 0),
        };

        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0, 4, 0, 6);

        if (field is TextBox box)
        {
            box.PlaceholderText = placeholder;
            Theme.StyleTextBox(box, Theme.Current);
        }

        layout.Controls.Add(caption, 0, row);
        layout.Controls.Add(field, 1, row);
    }

    public WindowsTool? Tool =>
        string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_command.Text)
            ? null
            : new WindowsTool(
                _name.Text.Trim(),
                _command.Text.Trim(),
                string.IsNullOrWhiteSpace(_category.Text) ? "Custom" : _category.Text.Trim());
}
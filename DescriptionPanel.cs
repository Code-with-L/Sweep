namespace Sweep;

/// <summary>
/// The collapsible Description Panel along the bottom of the window.
///
/// It carries three things: what the current module does, what is currently selected, and -
/// where it matters - a warning that names the action about to happen and states plainly
/// whether it can be undone. For a tool that deletes things, that line is the difference
/// between a user knowing what they pressed and not.
/// </summary>
internal sealed class DescriptionPanel : Panel
{
    private readonly Label _heading = new();
    private readonly Label _body = new();
    private readonly Button _toggle = new();
    private readonly Panel _content;
    private bool _expanded = true;

    public event EventHandler? ExpandChanged;

    public DescriptionPanel()
    {
        Dock = DockStyle.Bottom;
        Height = 92;
        BackColor = Theme.Current.SurfaceAlt;
        Padding = new Padding(16, 8, 16, 8);

        _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Current.SurfaceAlt, Padding = Padding.Empty };

        _heading.AutoSize = false;
        _heading.Dock = DockStyle.Top;
        _heading.Height = 20;
        _heading.TextAlign = ContentAlignment.MiddleLeft;
        _heading.ForeColor = Theme.Current.Text;
        _heading.Font = new Font(Font, FontStyle.Bold);
        // Slack, because AutoSize measures a pixel or two short of what TextRenderer draws
        // and the last glyph gets sliced.
        _heading.Padding = new Padding(0, 0, 4, 0);

        _body.AutoSize = false;
        _body.Dock = DockStyle.Fill;
        _body.ForeColor = Theme.Current.TextDim;
        _body.Padding = new Padding(0, 2, 8, 0);

        _content.Controls.Add(_body);
        _content.Controls.Add(_heading);

        _toggle.Dock = DockStyle.Right;
        _toggle.Width = 34;
        _toggle.Height = 26;
        _toggle.Text = "▾";
        _toggle.AccessibleName = "Collapse description panel";
        _toggle.Click += (_, _) => Toggle();

        Controls.Add(_content);
        Controls.Add(_toggle);

        Theme.StyleButton(_toggle, Theme.Current);
        _toggle.FlatAppearance.BorderSize = 0;
    }

    public void SetText(string heading, string body)
    {
        _heading.Text = heading;
        _body.Text = body;
    }

    /// <summary>Highlights the body text, used to warn before a destructive action.</summary>
    public void SetWarning(string heading, string body)
    {
        _heading.Text = heading;
        _heading.ForeColor = Theme.Current.Warning;
        _body.Text = body;
        _body.ForeColor = Theme.Current.Warning;
    }

    public void ClearWarning()
    {
        _heading.ForeColor = Theme.Current.Text;
        _body.ForeColor = Theme.Current.TextDim;
    }

    private void Toggle()
    {
        _expanded = !_expanded;
        Height = _expanded ? 92 : 30;
        _content.Visible = _expanded;
        _toggle.Text = _expanded ? "▾" : "▴";
        _toggle.AccessibleName = _expanded ? "Collapse description panel" : "Expand description panel";
        ExpandChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Restyle()
    {
        BackColor = Theme.Current.SurfaceAlt;
        _content.BackColor = Theme.Current.SurfaceAlt;
        _heading.ForeColor = Theme.Current.Text;
        _body.ForeColor = Theme.Current.TextDim;
        Theme.StyleButton(_toggle, Theme.Current);
    }
}
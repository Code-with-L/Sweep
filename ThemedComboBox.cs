using System.Drawing.Drawing2D;

namespace Sweep;

/// <summary>
/// A ComboBox that paints itself entirely from the Sweep palette.
///
/// The stock control ignores BackColor and ForeColor: on Windows 11 its drop-down button is
/// drawn by the theme engine regardless, which left the filter dropdowns as white blocks with
/// black text against a dark window. Setting DrawMode alone fixes the field but not the button,
/// so the control is switched to UserPaint and the whole surface - field, border, text and the
/// chevron - is rendered here.
///
/// Only the open list is left to the base ComboBox; those items are painted by the DrawItem
/// handler in <see cref="Theme.StyleComboBox"/>.
/// </summary>
internal sealed class ThemedComboBox : ComboBox
{
    private const int ButtonWidth = 22;

    public ThemedComboBox()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque,
            true);

        DrawMode = DrawMode.OwnerDrawFixed;
    }

    /// <summary>Forces a repaint so a theme switch is reflected immediately.</summary>
    public void RefreshTheme() => Invalidate(true);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.Current;

        using (var fill = new SolidBrush(p.SurfaceAlt))
        {
            g.FillRectangle(fill, ClientRectangle);
        }

        using (var border = new Pen(p.Border))
        {
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        // The native control still owns mouse handling and the drop-down popup, so clicking
        // the button still opens the list; only the pixels are ours.
        string text = SelectedItem?.ToString() ?? Text;

        var textArea = new Rectangle(8, 0, Math.Max(1, Width - ButtonWidth - 14), Height);
        TextRenderer.DrawText(
            g,
            text,
            Font,
            textArea,
            p.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

        DrawChevron(g, p);
    }

private void DrawChevron(Graphics g, Theme.Palette p)
    {
        int cy = Height / 2;
        const int Wing = 4;

        using var pen = new Pen(p.TextDim, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        // Place the chevron inside the button strip the native control reserves.
        var origin = new PointF(Width - ButtonWidth + (ButtonWidth / 2f) - 1, cy);

        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawLines(pen,
        [
            new PointF(origin.X - Wing, origin.Y - 2),
            new PointF(origin.X, origin.Y + 2),
            new PointF(origin.X + Wing, origin.Y - 2),
        ]);
        g.SmoothingMode = previous;
    }
}
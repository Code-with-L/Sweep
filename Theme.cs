using System.Drawing.Drawing2D;

namespace Sweep;

internal static class Theme
{
    public sealed record Palette(
        Color Background,
        Color Surface,
        Color SurfaceAlt,
        Color Text,
        Color TextDim,
        Color Accent,
        Color AccentText,
        Color Border,
        Color Danger,
        Color Warning,
        Color Success,
        Color Selection);

    public static Palette Dark { get; } = new(
        Background: Color.FromArgb(0x12, 0x14, 0x17),
        Surface: Color.FromArgb(0x1A, 0x1D, 0x22),
        SurfaceAlt: Color.FromArgb(0x22, 0x26, 0x2C),
        Text: Color.FromArgb(0xE9, 0xEC, 0xEF),
        TextDim: Color.FromArgb(0x93, 0x9B, 0xA4),
        Accent: Color.FromArgb(0x4C, 0x9A, 0xFF),
        AccentText: Color.FromArgb(0x0B, 0x14, 0x20),
        Border: Color.FromArgb(0x2E, 0x34, 0x3C),
        Danger: Color.FromArgb(0xFF, 0x6B, 0x6B),
        Warning: Color.FromArgb(0xFF, 0xC4, 0x6B),
        Success: Color.FromArgb(0x5B, 0xD9, 0x8A),
        Selection: Color.FromArgb(0x1E, 0x30, 0x48));

    public static Palette Light { get; } = new(
        Background: Color.FromArgb(0xF3, 0xF5, 0xF7),
        Surface: Color.White,
        SurfaceAlt: Color.FromArgb(0xEC, 0xEF, 0xF2),
        Text: Color.FromArgb(0x18, 0x1C, 0x21),
        TextDim: Color.FromArgb(0x5C, 0x64, 0x6D),
        Accent: Color.FromArgb(0x1F, 0x6F, 0xEB),
        AccentText: Color.White,
        Border: Color.FromArgb(0xD3, 0xD8, 0xDE),
        Danger: Color.FromArgb(0xC0, 0x28, 0x28),
        Warning: Color.FromArgb(0xA8, 0x66, 0x00),
        Success: Color.FromArgb(0x1B, 0x7F, 0x45),
        Selection: Color.FromArgb(0xDD, 0xE9, 0xFB));

    public static ThemeMode Mode { get; private set; } = ThemeMode.Dark;

    public static Palette Current => Mode == ThemeMode.Dark ? Dark : Light;

    public static void ApplyMode(ThemeMode mode) => Mode = mode;

    public static Color StatusColorFor(LogLevel level) => level switch
    {
        LogLevel.Success => Current.Success,
        LogLevel.Warning => Current.Warning,
        LogLevel.Error => Current.Danger,
        _ => Current.TextDim,
    };

    public static void ApplyToGrid(DataGridView grid, Palette p)
    {
        grid.BackgroundColor = p.Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = p.Border;
        grid.ForeColor = p.Text;
        grid.DefaultCellStyle.BackColor = p.Surface;
        grid.DefaultCellStyle.ForeColor = p.Text;
        grid.DefaultCellStyle.SelectionBackColor = p.Selection;
        grid.DefaultCellStyle.SelectionForeColor = p.Text;
        grid.DefaultCellStyle.Padding = new Padding(4, 3, 4, 3);
        grid.ColumnHeadersDefaultCellStyle.BackColor = p.SurfaceAlt;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = p.TextDim;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.SurfaceAlt;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.TextDim;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 6, 4, 6);
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.RowTemplate.Height = 38;
    }

    public static void ApplyToList(ListView list, Palette p)
    {
        list.BackColor = p.Surface;
        list.ForeColor = p.Text;
        list.BorderStyle = BorderStyle.None;
    }

    public static void ApplyToTree(TreeView tree, Palette p)
    {
        tree.BackColor = p.Surface;
        tree.ForeColor = p.Text;
        tree.BorderStyle = BorderStyle.None;
        tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
        tree.DrawNode += (_, e) =>
        {
            if (e.Node is null || e.Graphics is null)
        {
            return;
        }

        bool selected = e.Node.IsSelected;
            using var back = new SolidBrush(selected ? p.Selection : p.Surface);
            using var fore = new SolidBrush(p.Text);
            e.Graphics.FillRectangle(back, e.Bounds);
            TextRenderer.DrawText(
                e.Graphics,
                e.Node.Text,
                SystemFonts.DefaultFont,
                e.Bounds,
                fore.Color,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
    }

    public static void StyleButton(Button button, Palette p, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? p.Accent : p.Border;
        button.BackColor = primary ? p.Accent : p.SurfaceAlt;
        button.ForeColor = primary ? p.AccentText : p.Text;
        button.FlatAppearance.MouseOverBackColor = primary ? p.Accent : p.Selection;
        button.Cursor = Cursors.Hand;
    }

    public static void StyleTextBox(TextBoxBase box, Palette p)
    {
        box.BackColor = p.SurfaceAlt;
        box.ForeColor = p.Text;
        box.BorderStyle = BorderStyle.FixedSingle;
    }

    /// <summary>Dark-aware renderer for menus and tool strips.</summary>
    public sealed class Renderer : ToolStripProfessionalRenderer
    {
        private readonly Palette _p;

        public Renderer(Palette p)
            : base(new SweepColorTable(p))
        {
            _p = p;
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(_p.Border);
            e.Graphics.DrawLine(pen, e.AffectedBounds.Left, e.AffectedBounds.Bottom - 1,
                e.AffectedBounds.Right, e.AffectedBounds.Bottom - 1);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? _p.Text : _p.Text;
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not ToolStripButton button)
            {
                base.OnRenderButtonBackground(e);
                return;
            }

            using var brush = new SolidBrush(button.Selected || button.Pressed ? _p.Selection : _p.Surface);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }

        private sealed class SweepColorTable : ProfessionalColorTable
        {
            private readonly Palette _p;

            public SweepColorTable(Palette p) => _p = p;

            public override Color ToolStripDropDownBackground => _p.SurfaceAlt;
            public override Color MenuItemSelected => _p.Selection;
            public override Color MenuItemSelectedGradientBegin => _p.Selection;
            public override Color MenuItemSelectedGradientEnd => _p.Selection;
            public override Color MenuItemBorder => _p.Selection;
            public override Color MenuBorder => _p.Border;
            public override Color ButtonSelectedHighlight => _p.Selection;
            public override Color ButtonCheckedHighlight => _p.Selection;
            public override Color SeparatorDark => _p.Border;
            public override Color SeparatorLight => _p.Border;
            public override Color ImageMarginGradientBegin => _p.Surface;
            public override Color ImageMarginGradientMiddle => _p.Surface;
            public override Color ImageMarginGradientEnd => _p.Surface;
            public override Color ToolStripGradientBegin => _p.Surface;
            public override Color ToolStripGradientMiddle => _p.Surface;
            public override Color ToolStripGradientEnd => _p.Surface;
        }
    }

    public static void StyleMenu(MenuStrip menu, Palette p)
    {
        menu.BackColor = p.Surface;
        menu.ForeColor = p.Text;
        menu.Renderer = new Renderer(p);
        menu.RenderMode = ToolStripRenderMode.Professional;

        foreach (ToolStripItem item in menu.Items)
        {
            StyleMenuItem(item, p);
        }
    }

    private static void StyleMenuItem(ToolStripItem item, Palette p)
    {
        item.BackColor = p.Surface;
        item.ForeColor = p.Text;

        if (item is ToolStripDropDownItem drop)
        {
            foreach (ToolStripItem child in drop.DropDownItems)
            {
                StyleMenuItem(child, p);
            }

            drop.DropDown.BackColor = p.SurfaceAlt;
            drop.DropDown.ForeColor = p.Text;
            drop.DropDown.Renderer = new Renderer(p);
            drop.DropDown.RenderMode = ToolStripRenderMode.Professional;
        }
    }

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || bounds.Width <= radius * 2 || bounds.Height <= radius * 2)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
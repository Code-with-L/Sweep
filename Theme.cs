using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms.VisualStyles;

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
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

        // MultiSelect is deliberately NOT set here. The theme is applied after the grid is
        // built and again on every theme switch, so forcing it here silently undid the
        // host's own setting and left the list stuck on single selection.
        grid.RowTemplate.Height = 38;
    }

/// <summary>
    /// Applies the palette to a ListView, including its column header.
    ///
    /// The header is the part that will not follow BackColor on its own: Windows draws it, so
    /// it came out as a white bar against a dark window. Owner-drawing is what makes the whole
    /// control match.
    /// </summary>
    public static void ApplyToList(ListView list, Palette p)
    {
        list.BackColor = p.Surface;
        list.ForeColor = p.Text;

        list.DrawItem -= OnListDrawItem;
        list.DrawSubItem -= OnListDrawSubItem;
        list.DrawColumnHeader -= OnListDrawHeader;
        list.DrawItem += OnListDrawItem;
        list.DrawSubItem += OnListDrawSubItem;
        list.DrawColumnHeader += OnListDrawHeader;
        list.OwnerDraw = true;

        // Native scrollbars stay light grey otherwise, including the corner block above the
        // vertical bar, which reads as a hole in a dark list.
        list.HandleCreated -= OnListHandleCreated;
        list.HandleCreated += OnListHandleCreated;
        if (list.IsHandleCreated)
        {
            ApplyScrollbarTheme(list);
        }
    }

    private static void OnListHandleCreated(object? sender, EventArgs e)
    {
        if (sender is ListView list)
        {
            ApplyScrollbarTheme(list);
        }
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    private static void ApplyScrollbarTheme(ListView list)
    {
        try
        {
            bool dark = Mode == ThemeMode.Dark;
            SetWindowTheme(list.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);

            // The header is a separate child window. Its unused right edge is painted with the
            // default light background, which is the white block that appeared beside the last
            // column; theming it dark removes it without adding a horizontal scrollbar.
            EnumChildWindows(list.Handle, (child, _) =>
            {
                var name = new StringBuilder(256);
                GetClassName(child, name, 256);
                if (name.ToString().Contains("SysHeader32", StringComparison.OrdinalIgnoreCase))
                {
                    SetWindowTheme(child, dark ? "DarkMode_ItemsView" : "Explorer", null);
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Older builds without the dark themes just keep the default colours.
        }
    }

    private static void OnListDrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        if (sender is not ListView list)
        {
            return;
        }

        var p = Current;

        // The strip past the final column never raises a header event, so the last column's
        // fill is extended across it. Left alone that gap stayed white against a dark window.
        Rectangle bounds = e.Bounds;
        if (e.ColumnIndex >= 0 && e.ColumnIndex >= list.Columns.Count - 1)
        {
            bounds = new Rectangle(
                bounds.Left,
                bounds.Top,
                Math.Max(bounds.Width, list.ClientSize.Width - bounds.Left),
                bounds.Height);
        }

        using var back = new SolidBrush(p.SurfaceAlt);
        using var border = new Pen(p.Border);
        using var text = new SolidBrush(p.TextDim);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.LineLimit,
        };

        e.Graphics.FillRectangle(back, bounds);
        e.Graphics.DrawLine(border, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom);

        var area = new RectangleF(bounds.X + 6, bounds.Y, Math.Max(1, bounds.Width - 10), bounds.Height);
        e.Graphics.DrawString(e.Header?.Text ?? string.Empty, list.Font, text, area, format);
    }

    private static void OnListDrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        var p = Current;

        using var back = new SolidBrush(e.Item?.Selected == true ? p.Selection : p.Surface);
        e.Graphics.FillRectangle(back, e.Bounds);
    }

private static void OnListDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (sender is not ListView list || e.Item is null)
        {
            return;
        }

        var p = Current;
        bool selected = e.Item.Selected;

        using var text = new SolidBrush(p.Text);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.LineLimit,
        };

        var area = new RectangleF(e.Bounds.X + 4, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 8), e.Bounds.Height);

        // Owner-draw replaces the native painting completely, including the check box. Without
        // this the checkable lists (leftovers, junk, history, traces) showed no tick at all -
        // the state was there and clickable, but invisible.
        if (e.ColumnIndex == 0 && list.CheckBoxes && e.Item is not null)
        {
            CheckBoxState state = e.Item.Checked
                ? CheckBoxState.CheckedNormal
                : CheckBoxState.UncheckedNormal;

            Size glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
            int top = e.Bounds.Y + Math.Max(0, (e.Bounds.Height - glyph.Height) / 2);
            var point = new Point(e.Bounds.X + 4, top);
            CheckBoxRenderer.DrawCheckBox(e.Graphics, point, state);

            int textLeft = point.X + glyph.Width + 6;
            area = new RectangleF(
                textLeft,
                e.Bounds.Y,
                Math.Max(1, e.Bounds.Right - textLeft - 4),
                e.Bounds.Height);
        }

        e.Graphics.DrawString(e.SubItem?.Text ?? string.Empty, list.Font, text, area, format);
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
        SizeToText(button);
    }

    /// <summary>
    /// Widens a button to fit its own caption.
    ///
    /// Fixed widths clipped captions as soon as the system font or DPI changed - "Enable
    /// auto start" became "Enable auto". Measuring instead of guessing means a caption
    /// cannot be cut off, and it has to be called again if the text changes at runtime.
    /// </summary>
    public static void SizeToText(Button button, int minimum = 0)
    {
        // The slack covers the button's own internal margins and its border. Measuring the
        // caption alone left a few pixels short, which showed up as a clipped last letter.
        int text = TextRenderer.MeasureText(button.Text, button.Font).Width;
        button.Width = Math.Max(minimum, text + 44);
    }

    public static void StyleTextBox(TextBoxBase box, Palette p)
    {
        box.BackColor = p.SurfaceAlt;
        box.ForeColor = p.Text;
        box.BorderStyle = BorderStyle.FixedSingle;
    }

    /// <summary>
    /// ComboBox is one of the few controls that ignores BackColor and ForeColor unless it
    /// draws itself, so the filter dropdowns came up as white boxes with black text against
    /// a dark window. Owner-drawing is the supported way to make them follow the palette.
    /// </summary>
    public static void StyleComboBox(ComboBox box, Palette p)
    {
        // DrawMode is sticky, and the handlers capture this palette, so re-theming has to
        // drop the old subscription rather than stack another one on top of it.
        box.DrawItem -= OnComboDrawItem;
        box.DrawItem += OnComboDrawItem;

        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.BackColor = p.SurfaceAlt;
        box.ForeColor = p.Text;
        box.FlatStyle = FlatStyle.Flat;

        if (box is ThemedComboBox themed)
        {
            themed.RefreshTheme();
        }
    }

    private static void OnComboDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox box)
        {
            return;
        }

        var p = Theme.Current;
        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        bool hovered = (e.State & DrawItemState.ComboBoxEdit) == DrawItemState.ComboBoxEdit;

        using var background = new SolidBrush(selected || hovered ? p.Selection : p.SurfaceAlt);

        // The control relies entirely on this handler for its background, so an empty or
        // out-of-range index still has to be painted over rather than skipped.
        e.Graphics.FillRectangle(background, e.Bounds);

        if (e.Index < 0 || e.Index >= box.Items.Count)
        {
            return;
        }

        using var foreground = new SolidBrush(selected || hovered ? p.Text : p.TextDim);
        using var format = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        string text = box.Items[e.Index]?.ToString() ?? string.Empty;
        var textArea = new RectangleF(
            e.Bounds.X + 6,
            e.Bounds.Y,
            Math.Max(1, e.Bounds.Width - 6 - SystemInformation.VerticalScrollBarWidth - 4),
            e.Bounds.Height);

        e.Graphics.DrawString(text, box.Font, foreground, textArea, format);
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
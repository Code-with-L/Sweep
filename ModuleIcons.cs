using System.Drawing.Drawing2D;

namespace Sweep;

/// <summary>
/// Icons for the navigation rail and the toolbar.
///
/// Drawn rather than shipped as assets: there is no image pipeline here, and a handful of
/// simple shapes rendered at the right size stays sharp at any DPI, follows the theme colour,
/// and costs nothing to maintain. Everything is stroked with round caps so the set reads as
/// one family rather than a pile of clip art.
/// </summary>
internal static class ModuleIcons
{
    private const int Size = 20;

    public enum Glyph
    {
        Programs,
        Traced,
        Store,
        Extension,
        Hunter,
        Autorun,
        Junk,
        Tools,
        History,
        Backup,
        Settings,
        Refresh,
        Log,
    }

    /// <summary>Renders a glyph, tinted to the given colour.</summary>
    public static Image Render(Glyph glyph, Color color, int size = Size)
    {
        var bitmap = new Bitmap(size, size);
        bitmap.SetResolution(96f, 96f);

        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float scale = size / (float)Size;
        g.ScaleTransform(scale, scale);

        using var pen = new Pen(color, 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var fill = new SolidBrush(Color.FromArgb(38, color));

        switch (glyph)
        {
            case Glyph.Programs:
                DrawFolder(g, pen, fill);
                break;
            case Glyph.Traced:
                DrawClock(g, pen);
                break;
            case Glyph.Store:
                DrawStoreBag(g, pen, fill);
                break;
            case Glyph.Extension:
                DrawPuzzle(g, pen, fill);
                break;
            case Glyph.Hunter:
                DrawCrosshair(g, pen);
                break;
            case Glyph.Autorun:
                DrawSliders(g, pen);
                break;
            case Glyph.Junk:
                DrawBroom(g, pen, fill);
                break;
            case Glyph.Tools:
                DrawWrench(g, pen);
                break;
            case Glyph.History:
                DrawHistory(g, pen);
                break;
            case Glyph.Backup:
                DrawArchive(g, pen, fill);
                break;
            case Glyph.Settings:
                DrawGear(g, pen, fill);
                break;
            case Glyph.Refresh:
                DrawRefresh(g, pen);
                break;
            case Glyph.Log:
                DrawList(g, pen, fill);
                break;
        }

        return bitmap;
    }

    private static void DrawFolder(Graphics g, Pen pen, Brush fill)
    {
        g.FillPath(fill, RoundedPath(new[]
        {
            new PointF(3, 6), new PointF(8.5f, 6), new PointF(10, 8),
            new PointF(17, 8), new PointF(17, 16), new PointF(3, 16),
        }, 1.6f));
        g.DrawPath(pen, RoundedPath(new[]
        {
            new PointF(3, 16), new PointF(3, 6), new PointF(8.5f, 6), new PointF(10, 8),
            new PointF(17, 8), new PointF(17, 16),
        }, 1.6f));
    }

    private static void DrawClock(Graphics g, Pen pen)
    {
        g.DrawEllipse(pen, 3f, 3f, 14f, 14f);
        g.DrawLine(pen, 10f, 6.5f, 10f, 10.5f);
        g.DrawLine(pen, 10f, 10.5f, 13.2f, 12f);
    }

    private static void DrawStoreBag(Graphics g, Pen pen, Brush fill)
    {
        g.FillPath(fill, RoundedPath(new[]
        {
            new PointF(4, 7), new PointF(16, 7), new PointF(17, 17), new PointF(3, 17),
        }, 1.8f));
        g.DrawPath(pen, RoundedPath(new[]
        {
            new PointF(3, 17), new PointF(4, 7), new PointF(16, 7), new PointF(17, 17),
        }, 1.8f));
        g.DrawArc(pen, 7f, 3.5f, 6f, 6f, 180, 180);
    }

    private static void DrawPuzzle(Graphics g, Pen pen, Brush fill)
    {
        g.FillPath(fill, RoundedPath(new[]
        {
            new PointF(4, 4), new PointF(9, 4), new PointF(9, 7),
            new PointF(12, 7), new PointF(12, 4), new PointF(16, 4),
            new PointF(16, 9), new PointF(13, 9), new PointF(13, 12),
            new PointF(16, 12), new PointF(16, 16), new PointF(11, 16),
            new PointF(11, 13), new PointF(8, 13), new PointF(8, 16),
            new PointF(4, 16),
        }, 1.4f));
        g.DrawPath(pen, RoundedPath(new[]
        {
            new PointF(4, 4), new PointF(9, 4), new PointF(9, 7),
            new PointF(12, 7), new PointF(12, 4), new PointF(16, 4),
            new PointF(16, 9), new PointF(13, 9), new PointF(13, 12),
            new PointF(16, 12), new PointF(16, 16), new PointF(11, 16),
            new PointF(11, 13), new PointF(8, 13), new PointF(8, 16),
            new PointF(4, 16), new PointF(4, 4),
        }, 1.4f));
    }

    private static void DrawCrosshair(Graphics g, Pen pen)
    {
        g.DrawEllipse(pen, 5f, 5f, 10f, 10f);
        g.DrawLine(pen, 10f, 1.5f, 10f, 5f);
        g.DrawLine(pen, 10f, 15f, 10f, 18.5f);
        g.DrawLine(pen, 1.5f, 10f, 5f, 10f);
        g.DrawLine(pen, 15f, 10f, 18.5f, 10f);
    }

    private static void DrawSliders(Graphics g, Pen pen)
    {
        g.DrawLine(pen, 3f, 6f, 17f, 6f);
        g.DrawLine(pen, 3f, 14f, 17f, 14f);
        g.DrawEllipse(pen, 6.5f, 4f, 4f, 4f);
        g.DrawEllipse(pen, 11.5f, 12f, 4f, 4f);
    }

    private static void DrawBroom(Graphics g, Pen pen, Brush fill)
    {
        g.DrawLine(pen, 12.5f, 3.5f, 7.5f, 9.5f);
        g.FillPath(fill, RoundedPath(new[]
        {
            new PointF(7.6f, 8.6f), new PointF(11.4f, 12.4f),
            new PointF(6, 16), new PointF(4, 14),
        }, 1f));
        g.DrawPath(pen, RoundedPath(new[]
        {
            new PointF(7.6f, 8.6f), new PointF(11.4f, 12.4f),
            new PointF(6, 16), new PointF(4, 14), new PointF(7.6f, 8.6f),
        }, 1f));
    }

    private static void DrawWrench(Graphics g, Pen pen)
    {
        g.DrawLine(pen, 4.5f, 15.5f, 12f, 8f);
        g.DrawArc(pen, 8.5f, 2.5f, 9f, 9f, 40, 250);
        g.DrawEllipse(pen, 2.2f, 13.2f, 4.6f, 4.6f);
    }

    private static void DrawHistory(Graphics g, Pen pen)
    {
        g.DrawArc(pen, 3f, 3f, 14f, 14f, 250, 280);
        g.DrawLine(pen, 10f, 5.6f, 10f, 10.4f);
        g.DrawLine(pen, 10f, 10.4f, 13.2f, 12f);
    }

    private static void DrawArchive(Graphics g, Pen pen, Brush fill)
    {
        g.FillPath(fill, RoundedPath(new[]
        {
            new PointF(3, 4), new PointF(17, 4), new PointF(17, 8), new PointF(3, 8),
        }, 1f));
        g.DrawPath(pen, RoundedPath(new[]
        {
            new PointF(3, 8), new PointF(4.6f, 17), new PointF(15.4f, 17), new PointF(17, 8), new PointF(3, 8),
        }, 1.4f));
        g.DrawPath(pen, RoundedPath(new[]
        {
            new PointF(3, 4), new PointF(17, 4), new PointF(17, 8), new PointF(3, 8), new PointF(3, 4),
        }, 1.2f));
        g.DrawLine(pen, 8.2f, 12.5f, 11.8f, 12.5f);
    }

    private static void DrawGear(Graphics g, Pen pen, Brush fill)
    {
        var path = new GraphicsPath();
        const int teeth = 8;
        float outer = 8.6f;
        float inner = 6.4f;
        PointF previous = PointF.Empty;

        for (int i = 0; i < teeth * 2; i++)
        {
            double angle = Math.PI * i / teeth - Math.PI / 2;
            float radius = i % 2 == 0 ? outer : inner;
            float x = 10f + (float)(Math.Cos(angle) * radius);
            float y = 10f + (float)(Math.Sin(angle) * radius);
            var point = new PointF(x, y);

            if (i == 0)
            {
                path.StartFigure();
                path.AddLine(point, point);
            }
            else
            {
                path.AddLine(previous, point);
            }

            previous = point;
        }

        path.CloseFigure();
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
        g.DrawEllipse(pen, 7.2f, 7.2f, 5.6f, 5.6f);
    }

    private static void DrawRefresh(Graphics g, Pen pen)
    {
        g.DrawArc(pen, 3.2f, 3.2f, 13.6f, 13.6f, 60, 260);
        g.DrawLines(pen,
        [
            new PointF(14.6f, 3.0f),
            new PointF(15.4f, 7.6f),
            new PointF(10.8f, 7.0f),
        ]);
    }

    private static void DrawList(Graphics g, Pen pen, Brush fill)
    {
        g.DrawRectangle(pen, 4f, 3f, 12f, 14f);
        g.FillRectangle(fill, 6f, 6f, 8f, 1.6f);
        g.FillRectangle(fill, 6f, 10f, 8f, 1.6f);
        g.FillRectangle(fill, 6f, 14f, 5f, 1.6f);
    }

    /// <summary>Builds a closed path through the points with each corner rounded.</summary>
    private static GraphicsPath RoundedPath(PointF[] points, float radius)
    {
        var path = new GraphicsPath();
        if (points.Length < 3)
        {
            return path;
        }

        int count = points.Length;
        for (int i = 0; i < count; i++)
        {
            PointF current = points[i];
            PointF next = points[(i + 1) % count];
            PointF previous = points[(i - 1 + count) % count];

            PointF entry = MoveTowards(current, previous, radius);
            PointF exit = MoveTowards(current, next, radius);

            if (i == 0)
            {
                path.StartFigure();
            }
            else
            {
                path.AddLine(entry, entry);
            }

            path.AddArc(
                current.X - radius,
                current.Y - radius,
                radius * 2,
                radius * 2,
                (float)(Math.Atan2(entry.Y - current.Y, entry.X - current.X) * 180 / Math.PI),
                90f);
            path.AddLine(exit, exit);
        }

        path.CloseFigure();
        return path;
    }

    private static PointF MoveTowards(PointF from, PointF to, float distance)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float length = (float)Math.Sqrt(dx * dx + dy * dy);

        return length <= 0.01f
            ? from
            : new PointF(from.X + dx / length * distance, from.Y + dy / length * distance);
    }
}
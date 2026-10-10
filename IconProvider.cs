using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Sweep;

/// <summary>An icon source: a file plus an optional icon index or resource id.</summary>
public readonly record struct IconSource(string Path, int Index);

/// <summary>
/// Extracts program icons without ever blocking the UI thread and without ever showing a
/// blank cell.
///
/// Three problems this solves:
///  1. DisplayIcon is frequently "some.dll,-1234" - a resource id, not an index 0. Reading
///     index 0 of a shared system DLL gives every app the same icon.
///  2. There is no fallback chain, so anything the first probe fails on shows nothing.
///  3. Extraction ran inside CellValueNeeded, on the UI thread, on every repaint.
/// </summary>
public static class IconProvider
{
    private const int IconSize = 32;

    private static readonly ConcurrentDictionary<string, Image?> Cache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> InFlight = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiUseFileAttributes = 0x000000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    /// <summary>
    /// Cache-only lookup. Never touches the filesystem, so it is safe to call from
    /// CellValueNeeded on the UI thread.
    /// </summary>
    public static Image? Get(InstalledApp app) =>
        Cache.TryGetValue(app.DisplayName, out Image? cached) ? cached : null;

    public static bool IsLoaded(InstalledApp app) => Cache.ContainsKey(app.DisplayName);

    /// <summary>
    /// Loads one app's icon. Call from a background thread.
    /// </summary>
    public static void Load(InstalledApp app)
    {
        lock (Gate)
        {
            if (Cache.ContainsKey(app.DisplayName))
            {
                return;
            }

            if (!InFlight.Add(app.DisplayName))
            {
                return;
            }
        }

        Image? image = null;

        try
        {
            foreach (IconSource source in CandidateSources(app))
            {
                image = Extract(source);
                if (image is not null)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogStore.Warn($"Icon extraction failed for \"{app.DisplayName}\": {ex.Message}");
        }

        // A generated badge always renders, so no row is ever blank.
        image ??= CreateBadge(app.DisplayName);

        Cache[app.DisplayName] = image;

        lock (Gate)
        {
            InFlight.Remove(app.DisplayName);
        }
    }

    /// <summary>
    /// Warms the cache for a list of apps without blocking the caller. Safe to cancel.
    /// </summary>
    public static void Prefetch(IEnumerable<InstalledApp> apps, CancellationToken cancellationToken)
    {
        foreach (var app in apps)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Load(app);
        }
    }

    public static void Clear()
    {
        foreach (var image in Cache.Values)
        {
            image?.Dispose();
        }

        Cache.Clear();

        lock (Gate)
        {
            InFlight.Clear();
        }
    }

    /// <summary>Ordered fallbacks. The first source that yields an icon wins.</summary>
    public static IEnumerable<IconSource> CandidateSources(InstalledApp app)
    {
        var fromDisplayIcon = ParseDisplayIcon(app.DisplayIcon);
        if (fromDisplayIcon is not null)
        {
            yield return fromDisplayIcon.Value;
        }

        string? uninstallExe = AppIconTargets.ExecutableFromUninstallString(app.UninstallString);
        if (!string.IsNullOrWhiteSpace(uninstallExe))
        {
            yield return new IconSource(uninstallExe, 0);
        }

        foreach (string candidate in AppIconTargets.LikelyExecutables(app))
        {
            yield return new IconSource(candidate, 0);
        }
    }

    /// <summary>
    /// DisplayIcon is "path" or "path,index". A negative index is a resource identifier,
    /// not an ordinal, and must be passed through unchanged.
    /// </summary>
    internal static IconSource? ParseDisplayIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        string value = displayIcon.Trim();
        int index = 0;

        int comma = value.LastIndexOf(',');
        if (comma > 1 && int.TryParse(value[(comma + 1)..].Trim(), out int parsed))
        {
            index = parsed;
            value = value[..comma].Trim();
        }

        value = Environment.ExpandEnvironmentVariables(value.Trim('"'));
        return value.Length == 0 ? null : new IconSource(value, index);
    }

    private static Image? Extract(IconSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Path))
        {
            return null;
        }

        string extension = Path.GetExtension(source.Path).ToLowerInvariant();
        bool isImageFile = extension is ".ico" or ".bmp" or ".png" or ".jpg" or ".jpeg" or ".gif";

        bool exists;
        try
        {
            exists = File.Exists(source.Path);
        }
        catch
        {
            exists = false;
        }

        if (!exists)
        {
            // A stale icon path. SHGFI_USEFILEATTRIBUTES would happily return a generic
            // page glyph here; a labelled badge is more informative, so let the caller
            // fall through to the next candidate instead.
            return null;
        }

        if (isImageFile)
        {
            return ExtractViaShell(source.Path, exists: true);
        }

        // A PE file with no RT_GROUP_ICON yields 0 from ExtractIconEx. That means "this
        // binary has no icon", not "use a generic icon", so returning null here lets the
        // badge take over rather than showing an anonymous document glyph.
        return ExtractFromBinary(source.Path, source.Index);
    }

    private static Image? ExtractFromBinary(string path, int index = 0)
    {
        var large = new IntPtr[1];
        try
        {
            uint count = Native.ExtractIconExW(path, index, large, null, 1);
            if (count == 0 || large[0] == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                using var handleIcon = Icon.FromHandle(large[0]);
                using var sized = new Icon(handleIcon, IconSize, IconSize);
                using var bitmap = sized.ToBitmap();
                return new Bitmap(bitmap);
            }
            finally
            {
                Native.DestroyIcon(large[0]);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Shell extraction covers .ico, .bmp, .png and executables whose resource tree the
    /// icon API refuses, and SHGFI_USEFILEATTRIBUTES still yields a type-appropriate icon
    /// for a file that no longer exists.
    /// </summary>
    private static Image? ExtractViaShell(string path, bool exists)
    {
        uint flags = ShgfiIcon | ShgfiLargeIcon;
        if (!exists)
        {
            flags |= ShgfiUseFileAttributes;
        }

        try
        {
            if (SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero)
            {
                return null;
            }

            if (info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                using var handleIcon = Icon.FromHandle(info.hIcon);
                using var sized = new Icon(handleIcon, IconSize, IconSize);
                using var bitmap = sized.ToBitmap();
                return new Bitmap(bitmap);
            }
            finally
            {
                Native.DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Last-resort icon: a rounded badge with the product's initial. This is deliberately
    /// better than a generic page glyph, because it stays recognisable and never leaves an
    /// empty cell.
    /// </summary>
    private static Image CreateBadge(string name)
    {
        using var bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        byte r = Hash(name);
        Color background = Color.FromArgb(255, 60 + r % 120, 90 + (r / 7) % 110, 140 + (r / 13) % 100);

        using (var brush = new SolidBrush(background))
        using (GraphicsPath path = Theme.RoundedRect(new Rectangle(1, 1, IconSize - 2, IconSize - 2), 6))
        {
            g.FillPath(brush, path);
        }

        string initial = FirstInitial(name);
        using var font = new Font(SystemFonts.DefaultFont.FontFamily, 14f, FontStyle.Bold);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        using var textBrush = new SolidBrush(Color.White);
        g.DrawString(initial, font, textBrush, new RectangleF(0, 1, IconSize, IconSize - 1), format);

        return new Bitmap(bitmap);
    }

    private static string FirstInitial(string name)
    {
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                return char.ToUpperInvariant(c).ToString();
            }
        }

        return "?";
    }

    private static byte Hash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in value)
            {
                hash = (hash ^ c) * 16777619;
            }

            return (byte)(hash & 0xFF);
        }
    }
}

/// <summary>Icon source discovery, split out so both the provider and tests can use it.</summary>
public static class AppIconTargets
{
    public static string? ExecutableFromUninstallString(string? command)
    {
        var parts = Sweep.CommandLine.Split(command);
        string exe = CommandLine.ResolveExecutable(parts.FileName);
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// Candidate executables in the install folder, best first. Uninstallers such as
    /// unins000.exe and vcredist_x64.exe are explicitly deprioritised because they are
    /// almost never the icon a user recognises.
    /// </summary>
    public static IEnumerable<string> LikelyExecutables(InstalledApp app)
    {
        if (string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            yield break;
        }

        string dir;
        try
        {
            dir = Environment.ExpandEnvironmentVariables(app.InstallLocation.Trim().Trim('"'));
        }
        catch
        {
            yield break;
        }

        if (!Directory.Exists(dir))
        {
            yield break;
        }

        string compact = NameTokens.Compact(app.DisplayName);

        List<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateDirectories(dir)
                    .Where(d => !IsExcludedFolder(d))
                    .Take(8)
                    .SelectMany(d => SafeEnumerate(d)))
                .Take(400)
                .ToList();
        }
        catch
        {
            yield break;
        }

        foreach (string path in candidates
                     .Select(c => (Path: c, Score: Score(c, compact)))
                     .Where(x => x.Score >= 0)
                     .OrderByDescending(x => x.Score)
                     .ThenByDescending(x => new FileInfo(x.Path).Length)
                     .Select(x => x.Path))
        {
            yield return path;
        }
    }

    private static bool IsExcludedFolder(string dir)
    {
        string name = Path.GetFileName(dir);
        return name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("locales", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("resources", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return [];
        }
    }

    private static int Score(string path, string compact)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string lower = name.ToLowerInvariant();

        // Uninstallers and redistributables are never the product's real icon.
        if (lower.StartsWith("unins", StringComparison.Ordinal) ||
            lower.Contains("uninstall", StringComparison.Ordinal) ||
            lower.Contains("setup", StringComparison.Ordinal) ||
            lower.Contains("install", StringComparison.Ordinal) ||
            lower.Contains("redist", StringComparison.Ordinal) ||
            lower.Contains("vcredist", StringComparison.Ordinal) ||
            lower.Contains("crashreport", StringComparison.Ordinal) ||
            lower.Contains("update", StringComparison.Ordinal) ||
            lower.Contains("helper", StringComparison.Ordinal) ||
            lower.Contains("vcreg", StringComparison.Ordinal) ||
            lower.Contains("launcher", StringComparison.Ordinal))
        {
            return -1;
        }

        string nameCompact = NameTokens.Compact(name);
        if (compact.Length > 0 && string.Equals(nameCompact, compact, StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        if (compact.Length > 0 && nameCompact.Contains(compact, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return nameCompact.Length >= 4 ? 1 : 0;
    }
}
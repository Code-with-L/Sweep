using System.Runtime.InteropServices;

namespace Sweep;

/// <summary>
/// Windows 11 window integration: Snap Layouts, rounded corners, and a title bar that
/// follows the app theme instead of staying stubbornly white.
///
/// Snap Layouts are opt-in. Windows only shows the layout flyout when the app returns
/// HTMAXBUTTON from WM_NCHITTEST while the cursor is over the maximise button. Without that
/// the shell does not consider the window snap-capable and no layout menu ever appears.
/// </summary>
internal static class Win11
{
    internal const int WmNcHitTest = 0x0084;
    internal const int HtClient = 1;
    internal const int HtMaxButton = 2;

    private const int DwmwaUseImmersiveDarkMode = 20;

    // Windows 10 builds before 20H1 only understand the attribute under its old number.
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwaSystemBackdropType = 38;

    private const int CornerPreferenceRound = 2;
    private const int DwmSystemBackdropNone = 1;
    private const int DwmSystemBackdropMainFrame = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);


    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>
    /// Rounded corners, theme-matched title bar, and optional Mica backdrop.
    /// Silently does nothing on Windows 10, which simply rejects the attributes.
    /// </summary>
    public static void ApplyChrome(Form form, bool dark, Color background, Color foreground, bool mica = false)
    {
        if (!Supported || !form.IsHandleCreated)
        {
            return;
        }

        int dark0 = dark ? 1 : 0;
        int corners = CornerPreferenceRound;
        int backdrop = mica ? DwmSystemBackdropMainFrame : DwmSystemBackdropNone;
        int caption = ToBgr(background);
        int text = ToBgr(foreground);

        // The backdrop must be settled BEFORE the caption colour: left as "Auto", the
        // system re-applies its own palette afterwards and an explicitly light title bar
        // comes out dark.
        //
        // Note: on some Windows 11 builds DWMWA_CAPTION_COLOR returns S_OK but is then
        // ignored - DwmGetWindowAttribute reports DWMWA_COLOR_DEFAULT. Rounded corners and
        // the dark title bar do apply, so a mismatched title bar on the light theme is a
        // known cosmetic limitation rather than a bug to keep chasing.
        TrySetDwm(form.Handle, DwmwaSystemBackdropType, backdrop);

        // 20 is ignored (not just rejected) on some builds, which left a white caption over
        // a dark window. Asking under the legacy number as well is harmless on builds that
        // already honoured the first call.
        int darkHr = TrySetDwm(form.Handle, DwmwaUseImmersiveDarkMode, dark0);
        if (darkHr != 0)
        {
            TrySetDwm(form.Handle, DwmwaUseImmersiveDarkModeLegacy, dark0);
        }

        TrySetDwm(form.Handle, DwmwaWindowCornerPreference, corners);
        TrySetDwm(form.Handle, DwmwaCaptionColor, caption);
        TrySetDwm(form.Handle, DwmwaTextColor, text);
    }

    private static int ToBgr(Color color) => (color.B << 16) | (color.G << 8) | color.R;

    /// <summary>Returns the HRESULT so a rejected attribute is visible if it ever regresses.</summary>
    private static int TrySetDwm(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            int copy = value;
            return DwmSetWindowAttribute(hwnd, attribute, ref copy, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            return unchecked((int)0x80070057);
        }
        catch (EntryPointNotFoundException)
        {
            return unchecked((int)0x80070057);
        }
    }

    /// <summary>
    /// Screen-space bounds of the maximise button.
    ///
    /// WinForms does not expose the real caption buttons, and the standard trick of
    /// returning HTMAXBUTTON for the whole caption area hijacks dragging and
    /// double-click-to-maximise. The buttons are laid out right-to-left at a fixed
    /// caption height, which is stable enough to target precisely.
    /// </summary>
    public static Rectangle MaximizeButtonBounds(Form form)
    {
        try
        {
            float scale = form.DeviceDpi / 96f;
            int buttonWidth = (int)(46 * scale);
            int captionHeight = (int)(32 * scale);

            Rectangle window = form.Bounds;
            return new Rectangle(
                window.Right - (2 * buttonWidth),
                window.Top,
                buttonWidth,
                captionHeight);
        }
        catch
        {
            return Rectangle.Empty;
        }
    }

    /// <summary>Decides the WM_NCHITTEST result, enabling Snap Layouts over maximise.</summary>
    public static int HitTest(Form form, long lParam)
    {
        if (!Supported)
        {
            return 0;
        }

        try
        {
            // WM_NCHITTEST packs the screen coordinates into lParam as signed 16-bit values.
            int x = (short)((long)lParam & 0xFFFF);
            int y = (short)(((long)lParam >> 16) & 0xFFFF);

            if (MaximizeButtonBounds(form).Contains(x, y))
            {
                return HtMaxButton;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// Base form that applies Windows 11 chrome and the Snap Layout hit test. Every dialog in
/// the app derives from this so they all behave consistently.
/// </summary>
internal class ThemedForm : Form
{
    private bool _useMicaBackdrop;

    public ThemedForm()
    {
        // Snap Layouts are only meaningful once the handle exists.
        HandleCreated += (_, _) => ApplyWindows11Chrome();

        // WinForms re-asserts its own window attributes while the frame is created and
        // shown, which silently discards anything DWM was told at HandleCreated. Applying
        // again once the window is visible is what actually sticks.
        Shown += (_, _) => ApplyWindows11Chrome();
    }

    /// <summary>
    /// Mica renders behind the client area, so it only shows through where the form does
    /// not paint an opaque background. Off by default for that reason.
    /// </summary>
    protected void EnableMicaBackdrop()
    {
        _useMicaBackdrop = true;
        if (IsHandleCreated)
        {
            ApplyWindows11Chrome();
        }
    }

    protected virtual void ApplyWindows11Chrome()
    {
        var palette = Theme.Current;
        Win11.ApplyChrome(
            this,
            dark: Theme.Mode == ThemeMode.Dark,
            background: palette.Background,
            foreground: palette.Text,
            mica: _useMicaBackdrop);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win11.WmNcHitTest)
        {
            base.WndProc(ref m);

            if ((int)m.Result == Win11.HtClient)
            {
                int result = Win11.HitTest(this, m.LParam.ToInt64());
                if (result != 0)
                {
                    m.Result = (IntPtr)result;
                    return;
                }
            }

            return;
        }

        base.WndProc(ref m);
    }
}
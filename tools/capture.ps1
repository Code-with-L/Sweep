param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$WaitSeconds = 10,
    [string[]]$ExtraArgs = @(),
    [string]$TitleMatch = '',
    # DataGridView does not honour WM_PRINT, so PrintWindow reliably captures an empty grid.
    # Screen capture is the only way to get a truthful screenshot of a list view.
    [switch]$ForceScreen
)

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class Win {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    public static List<IntPtr> VisibleWindowsFor(uint target) {
        var found = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string TitleOf(IntPtr h) {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
"@

# The app is PerMonitorV2; an unaware host gets virtualised coordinates and would size the
# bitmap smaller than the window, silently cropping the bottom bar and status strip.
[void][Win]::SetProcessDpiAwarenessContext([IntPtr](-4))

$all = @('--no-elevate') + $ExtraArgs
$proc = Start-Process -FilePath $Exe -ArgumentList $all -PassThru
Start-Sleep -Seconds $WaitSeconds

if ($proc.HasExited) {
    Write-Output "PROCESS EXITED EARLY code=$($proc.ExitCode)"
    exit 1
}

$target = [IntPtr]::Zero
if ($TitleMatch -ne '') {
    foreach ($h in [Win]::VisibleWindowsFor([uint32]$proc.Id)) {
        $t = [Win]::TitleOf($h)
        if ($t -match $TitleMatch) { $target = $h; break }
    }
}

if ($target -eq [IntPtr]::Zero) {
    $proc.Refresh()
    $target = $proc.MainWindowHandle
}

if ($target -eq [IntPtr]::Zero) {
    Write-Output "NO WINDOW FOUND"
    $proc.Kill()
    exit 1
}

[void][Win]::ShowWindow($target, 9)
[void][Win]::SetForegroundWindow($target)
Start-Sleep -Milliseconds 2500

$rect = New-Object Win+RECT
[void][Win]::GetWindowRect($target, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
Write-Output "window=${w}x${h} title='$([Win]::TitleOf($target))'"

if ($ForceScreen) {
    # A background process cannot steal focus (Windows blocks it), so SetForegroundWindow
    # is a no-op here and a screen grab would capture whatever else is on top. Pinning the
    # window topmost sidesteps the focus lock entirely.
    [void][Win]::SetWindowPos($target, [Win]::HWND_TOPMOST, 0, 0, 0, 0,
        [Win]::SWP_NOMOVE -bor [Win]::SWP_NOSIZE -bor [Win]::SWP_SHOWWINDOW)
    Start-Sleep -Milliseconds 1200

    [void][Win]::GetWindowRect($target, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()

    [void][Win]::SetWindowPos($target, [Win]::HWND_NOTOPMOST, 0, 0, 0, 0,
        [Win]::SWP_NOMOVE -bor [Win]::SWP_NOSIZE)

    $proc.Kill()
    $proc.WaitForExit(5000) | Out-Null
    Write-Output "saved=$Out"
    exit 0
}

$renderJob = Start-Job -ArgumentList ([int64]$target.ToInt64()), $Out -ScriptBlock {
    param([int64]$hwnd, [string]$out)

    Add-Type -AssemblyName System.Drawing
    $h = [IntPtr]$hwnd
    $rect = New-Object PSObject
    $r = New-Object System.Drawing.Rectangle 0, 0, 0, 0

    # Re-declare the pieces this job needs locally.
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Cap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
}
"@

    [void][Cap]::SetProcessDpiAwarenessContext([IntPtr](-4))
    $rr = New-Object Cap+RECT
    [void][Cap]::GetWindowRect($h, [ref]$rr)
    $ww = $rr.Right - $rr.Left
    $hh = $rr.Bottom - $rr.Top

    $b = New-Object System.Drawing.Bitmap($ww, $hh)
    $gg = [System.Drawing.Graphics]::FromImage($b)
    $d = $gg.GetHdc()
    [void][Cap]::PrintWindow($h, $d, 2)
    $gg.ReleaseHdc($d)
    $gg.Dispose()
    $b.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
}

$renderUsedFallback = $false
if (Wait-Job $renderJob -Timeout 25) {
    Receive-Job $renderJob | Out-Null
    Write-Output "render=printwindow"
}
else {
    Write-Output "render=fallback (PrintWindow timed out)"
    Stop-Job $renderJob
    $renderUsedFallback = $true
}

Remove-Job $renderJob -Force -ErrorAction SilentlyContinue

if ($ForceScreen -or $renderUsedFallback) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
}

$proc.Kill()
$proc.WaitForExit(5000) | Out-Null
Write-Output "saved=$Out"
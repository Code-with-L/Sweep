param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$WaitSeconds = 10,
    [string[]]$ExtraArgs = @(),
    [string]$TitleMatch = ''
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

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[void][Win]::PrintWindow($target, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$proc.Kill()
$proc.WaitForExit(5000) | Out-Null
Write-Output "saved=$Out"
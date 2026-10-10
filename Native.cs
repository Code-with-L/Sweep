using System.Runtime.InteropServices;

namespace Sweep;

/// <summary>Centralised P/Invoke declarations. Everything here is documented Win32 surface.</summary>
internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint ExtractIconExW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpszFile,
        int nIconIndex,
        IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall,
        uint nIcons);

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
    private const int MOVEFILE_REPLACE_EXISTING = 0x1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveFileExW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpExistingFileName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpNewFileName,
        int dwFlags);

    public static bool ScheduleDeleteOnReboot(string path)
    {
        // lpNewFileName must be null for MOVEFILE_DELAY_UNTIL_REBOOT.
        return MoveFileExW(path, null!, MOVEFILE_DELAY_UNTIL_REBOOT);
    }

    public static bool RenameOnReboot(string from, string to)
    {
        return MoveFileExW(from, to, MOVEFILE_DELAY_UNTIL_REBOOT | MOVEFILE_REPLACE_EXISTING);
    }

    // ---- System Restore -------------------------------------------------

    public const int EVENTTYPE_BEGIN_SYSTEM_CHANGE = 12;
    public const int RESTORE_POINT_TYPE_APPLICATION_INSTALL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RESTOREPOINTINFOW
    {
        public int dwEventType;
        public int dwRestorePointType;
        public IntPtr szEventTitle;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SRSetRestorePointW(ref RESTOREPOINTINFOW pRestorePointInfo, out int pEventType);

    /// <summary>Creates a system restore point. Returns 0 on success; the value is an event id.</summary>
    public static bool TryCreateRestorePoint(string title, out int eventId, out string error)
    {
        eventId = 0;
        error = "";
        var info = new RESTOREPOINTINFOW
        {
            dwEventType = EVENTTYPE_BEGIN_SYSTEM_CHANGE,
            dwRestorePointType = RESTORE_POINT_TYPE_APPLICATION_INSTALL,
            szEventTitle = IntPtr.Zero,
        };

        IntPtr buf = IntPtr.Zero;
        try
        {
            buf = Marshal.StringToHGlobalUni(title);
            info.szEventTitle = buf;
            if (SRSetRestorePointW(ref info, out int result) != 0)
            {
                eventId = result;
                return true;
            }

            error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (buf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    // ---- Known folders (respects per-user redirection + ARM64 WOW64 rules) ----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    private static readonly Guid FOLDERID_UserProgramFiles = new("5cd7ae22-9e39-4347-96a2-5fcaecafb5f3");
    private static readonly Guid FOLDERID_ProgramFilesX86 = new("7c5a40ef-a0fb-4bfc-874a-c0f2e0b9fa8e");

    public static string? GetKnownFolder(Guid folderId)
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out ptr) != 0 || ptr == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(ptr);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
    }

    /// <summary>x86 program files, which on ARM64 may be redirected/emulated differently.</summary>
    public static string? ProgramFilesX86 => GetKnownFolder(FOLDERID_ProgramFilesX86);

    /// <summary>The 32-bit "Program Files" that x86 apps install into.</summary>
    public static string? UserProgramFiles => GetKnownFolder(FOLDERID_UserProgramFiles);

    // ---- Console (for headless self-test from a terminal) ----

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>
    /// A WinExe has no console of its own, so CLI-style invocations would print nothing.
    /// Borrowing the parent terminal makes the self-test usable from PowerShell.
    /// </summary>
    public static void TryAttachParentConsole()
    {
        try
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
        catch
        {
            // Running without a parent console is fine.
        }
    }

    // ---- Text box cue banner (grey placeholder text) ----

    private const int EM_SETCUEBANNER = 0x1501;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    public static void SetCueBanner(TextBoxBase box, string text)
    {
        try
        {
            SendMessage(box.Handle, EM_SETCUEBANNER, new IntPtr(1), text);
        }
        catch
        {
            // Cosmetic only.
        }
    }

    /// <summary>DataGridView is not double-buffered by default and flickers badly when repainted.</summary>
    public static void EnableDoubleBuffer(Control control)
    {
        try
        {
            typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(control, true);
        }
        catch
        {
            // Cosmetic only.
        }
    }
}
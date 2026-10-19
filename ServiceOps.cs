using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sweep;

/// <summary>
/// Service enable/disable via the Service Control Manager.
///
/// Renaming or deleting registry values is not a substitute: SCM caches the start type,
/// so a registry edit appears to work and then silently reverts. This uses the real API,
/// which makes the change both effective and genuinely reversible.
/// </summary>
internal static class ServiceOps
{
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceDisabled = 1;
    private const uint ServiceDemandStart = 3;
    private const uint ServiceAutoStart = 2;

    private const int ScManagerAllAccess = 0x000F003F;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, int desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(
        IntPtr service, uint serviceType, uint startType, uint errorControl,
        string? binaryPathName, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public static bool SetStartMode(string serviceName, bool enabled, out string error)
    {
        error = string.Empty;
        IntPtr manager = IntPtr.Zero;
        IntPtr service = IntPtr.Zero;

        try
        {
            manager = OpenSCManager(null, null, ScManagerAllAccess);
            if (manager == IntPtr.Zero)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            service = OpenService(manager, serviceName, 0x0001 | 0x0002 | 0x0010);
            if (service == IntPtr.Zero)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            uint startType = enabled ? ServiceAutoStart : ServiceDisabled;
            if (!ChangeServiceConfigW(
                    service, ServiceNoChange, startType, ServiceNoChange,
                    null, null, IntPtr.Zero, null, null, null, null))
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }

            if (manager != IntPtr.Zero)
            {
                CloseServiceHandle(manager);
            }
        }
    }

    public static bool IsRunning(string serviceName)
    {
        IntPtr manager = IntPtr.Zero;
        IntPtr service = IntPtr.Zero;

        try
        {
            manager = OpenSCManager(null, null, 0x0001); // SC_MANAGER_CONNECT
            if (manager == IntPtr.Zero)
            {
                return false;
            }

            service = OpenService(manager, serviceName, 0x0100); // SERVICE_QUERY_STATUS
            if (service == IntPtr.Zero)
            {
                return false;
            }

            // SC_STATUS_PROCESS_INFO requires SERVICE_STATUS_PROCESS (36 bytes), not the
            // 28-byte SERVICE_STATUS: the smaller buffer fails with ERROR_INSUFFICIENT_BUFFER
            // and made this method report "not running" for every service.
            var status = new SERVICE_STATUS_PROCESS();
            if (!QueryServiceStatusEx(
                    service,
                    0,
                    ref status,
                    Marshal.SizeOf<SERVICE_STATUS_PROCESS>(),
                    out _))
            {
                return false;
            }

            return status.dwCurrentState == ServiceRunning;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }

            if (manager != IntPtr.Zero)
            {
                CloseServiceHandle(manager);
            }
        }
    }

    private const uint ServiceRunning = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        ref SERVICE_STATUS_PROCESS buffer,
        int bufferSize,
        out int bytesNeeded);
}
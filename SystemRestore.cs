namespace Sweep;

internal static class SystemRestore
{
    public static bool TryCreate(string appName, out string message)
    {
        string title = $"Before removing {appName} - {DateTime.Now:yyyy-MM-dd HH:mm}";
        if (Native.TryCreateRestorePoint(title, out int eventId, out string error))
        {
            message = $"Restore point created (event {eventId}).";
            LogStore.Success(message);
            return true;
        }

        message = $"Could not create a restore point: {error}. " +
                  "System Protection may be turned off on this device.";
        LogStore.Warn(message);
        return false;
    }
}
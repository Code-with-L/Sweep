using System.Collections.Concurrent;
using System.Text;

namespace Sweep;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message)
{
    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

    public string LevelDisplay => Level switch
    {
        LogLevel.Success => "OK",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO",
    };
}

/// <summary>
/// Append-only activity log. Every destructive action is recorded here so the user has a
/// record of exactly what was changed.
/// </summary>
public static class LogStore
{
    private static readonly ConcurrentQueue<LogEntry> Recent = new();
    private static readonly object FileGate = new();

    public static event EventHandler<LogEntry>? EntryAdded;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sweep",
        "logs");

    public static string LogFilePath => Path.Combine(
        LogDirectory,
        $"sweep-{DateTime.Now:yyyyMMdd}.log");

    public static IReadOnlyList<LogEntry> Snapshot() => Recent.Reverse().ToList();

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Success(string message) => Write(LogLevel.Success, message);

    public static void Warn(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message, Exception? ex = null) =>
        Write(LogLevel.Error, ex is null ? message : $"{message}: {ex.Message}");

    public static void ClearRecent() => Recent.Clear();

    private static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);

        // Bound memory: keep the most recent 500 entries.
        Recent.Enqueue(entry);
        while (Recent.Count > 500 && Recent.TryDequeue(out _))
        {
        }

        AppendToFile(entry);

        try
        {
            EntryAdded?.Invoke(null, entry);
        }
        catch
        {
            // UI listeners must never break logging.
        }
    }

    private static void AppendToFile(LogEntry entry)
    {
        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(LogDirectory);
                var sb = new StringBuilder()
                    .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(entry.LevelDisplay.PadRight(5)).Append("] ")
                    .AppendLine(entry.Message);

                File.AppendAllText(LogFilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never be fatal.
        }
    }

    /// <summary>Writes a plain-text export of the current session for support/reporting.</summary>
    public static void ExportSession(string destination)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sweep activity log");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Machine: {Environment.MachineName}   OS: {Environment.OSVersion}");
        sb.AppendLine(new string('-', 60));

        foreach (var entry in Snapshot())
        {
            sb.AppendLine($"{entry.Timestamp:HH:mm:ss} [{entry.LevelDisplay}] {entry.Message}");
        }

        File.WriteAllText(destination, sb.ToString(), Encoding.UTF8);
    }
}
using System.Text.Json;

namespace Sweep;

/// <summary>One recorded removal.</summary>
internal sealed record UninstallRecord(
    string Name,
    string Operation,
    DateTime WhenUtc,
    int ExitCode,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Failed)
{
    public DateTime When => WhenUtc.ToLocalTime();

    public string Summary =>
        Failed.Count == 0
            ? $"{Removed.Count} item(s) removed"
            : $"{Removed.Count} removed, {Failed.Count} could not be removed";

    public string AgeDescription
    {
        get
        {
            TimeSpan age = DateTime.Now - When;

            if (age.TotalMinutes < 1)
            {
                return "just now";
            }

            if (age.TotalHours < 1)
            {
                return $"{(int)age.TotalMinutes} minutes ago";
            }

            if (age.TotalDays < 1)
            {
                return $"{(int)age.TotalHours} hours ago";
            }

            return age.TotalDays < 2 ? "yesterday" : $"{(int)age.TotalDays} days ago";
        }
    }

    /// <summary>Export text for this record, including the items that did not come out.</summary>
    public string ToText()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"{Name}");
        text.AppendLine($"  Operation : {Operation}");
        text.AppendLine($"  When      : {When:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"  Exit code : {ExitCode}");
        text.AppendLine($"  Result    : {Summary}");
        text.AppendLine();

        text.AppendLine($"Removed ({Removed.Count}):");
        foreach (string item in Removed)
        {
            text.AppendLine($"  + {item}");
        }

        if (Failed.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"Could not remove ({Failed.Count}):");
            foreach (string item in Failed)
            {
                text.AppendLine($"  ! {item}");
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// Uninstall History: a durable record of what each removal actually did.
///
/// This replaces the ad-hoc activity log as the answer to "what happened when I uninstalled
/// that?" It keeps both what was removed and what could not be, because a partial removal is
/// exactly the case worth being able to look back on.
/// </summary>
internal static class UninstallHistory
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private const int MaxRecords = 500;

    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sweep",
        "uninstall-history.json");

    public static void Record(UninstallRecord record)
    {
        lock (Gate)
        {
            try
            {
                List<UninstallRecord> records = Load()?.ToList() ?? new List<UninstallRecord>();
                records.Insert(0, record);

                // Bounded: a history that grows forever is its own kind of junk, and the
                // Backup Manager exists precisely so old entries can be exported first.
                if (records.Count > MaxRecords)
                {
                    records.RemoveRange(MaxRecords, records.Count - MaxRecords);
                }

                string? dir = System.IO.Path.GetDirectoryName(Path_);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(Path_, JsonSerializer.Serialize(records, Options));
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Could not record uninstall history: {ex.Message}");
            }
        }
    }

    /// <summary>Newest first.</summary>
    public static List<UninstallRecord> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(Path_))
                {
                    return new List<UninstallRecord>();
                }

                return JsonSerializer.Deserialize<List<UninstallRecord>>(File.ReadAllText(Path_), Options)
                    ?? new List<UninstallRecord>();
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Could not read uninstall history: {ex.Message}");
                return new List<UninstallRecord>();
            }
        }
    }

    public static string ExportAll()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("Sweep - Uninstall History");
        text.AppendLine($"Exported {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine();

        foreach (UninstallRecord record in Load())
        {
            text.AppendLine(record.ToText());
            text.AppendLine(new string('-', 60));
        }

        return text.ToString();
    }
}
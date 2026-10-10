using System.Diagnostics;
using System.Text.Json;

namespace Sweep;

public sealed record WindowsApp(
    string Name,
    string PackageFullName,
    string Publisher,
    string Version,
    string InstallLocation,
    bool IsFramework)
{
    public string PublisherDisplay
    {
        get
        {
            string value = Publisher;
            if (value.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                value = value[3..];
            }

            // Some packages embed a quoted CN, which arrives with the quotes intact.
            int comma = value.IndexOf(',');
            if (comma > 0)
            {
                value = value[..comma];
            }

            return value.Trim().Trim('"');
        }
    }
}

/// <summary>
/// Store/MSIX app support. These never appear in the uninstall registry keys, so the
/// classic program list cannot show them at all.
///
/// The Package Manager API would need a WinRT projection which changes the target
/// framework, so this shells out to the PowerShell Appx cmdlets instead: no new
/// dependencies, and it keeps the ARM64/x64/x86 publish matrix intact.
/// </summary>
public static class WindowsApps
{
    private const string QueryScript =
        "Get-AppxPackage | Select-Object Name,PackageFullName,Publisher,Version,InstallLocation,IsFramework " +
        "| ConvertTo-Json -Compress -Depth 2";

    public static List<WindowsApp> Scan(CancellationToken cancellationToken = default)
    {
        var results = new List<WindowsApp>();

        string json;
        try
        {
            json = RunPowerShell(QueryScript, timeoutMs: 90_000, cancellationToken);
        }
        catch (Exception ex)
        {
            LogStore.Warn($"Could not enumerate Store apps: {ex.Message}");
            return results;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return results;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // A single package serialises as an object rather than an array.
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var app = Parse(element);
                    if (app is not null)
                    {
                        results.Add(app);
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                var app = Parse(root);
                if (app is not null)
                {
                    results.Add(app);
                }
            }
        }
        catch (JsonException ex)
        {
            LogStore.Warn($"Could not parse the Store app list: {ex.Message}");
        }

        return results
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static WindowsApp? Parse(JsonElement element)
    {
        string name = GetString(element, "Name");
        string package = GetString(element, "PackageFullName");
        if (name.Length == 0 || package.Length == 0)
        {
            return null;
        }

        return new WindowsApp(
            name,
            package,
            GetString(element, "Publisher"),
            GetString(element, "Version"),
            GetString(element, "InstallLocation"),
            element.TryGetProperty("IsFramework", out var fw) &&
            (fw.ValueKind == JsonValueKind.True ||
             (fw.ValueKind == JsonValueKind.String && fw.GetString() == "True")));
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : element.TryGetProperty(property, out var other)
                ? other.ToString()
                : string.Empty;

    public static DeletionResult Remove(WindowsApp app)
    {
        string script = $"Remove-AppxPackage -Package '{app.PackageFullName}' -ErrorAction Stop";

        try
        {
            RunPowerShell(script, timeoutMs: 180_000, CancellationToken.None);
            LogStore.Success($"Removed Store app \"{app.Name}\".");
            return new DeletionResult(true, app.PackageFullName, false, null);
        }
        catch (Exception ex)
        {
            LogStore.Error($"Could not remove \"{app.Name}\"", ex);
            return new DeletionResult(false, app.PackageFullName, false, ex.Message);
        }
    }

    private static string RunPowerShell(string script, int timeoutMs, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };

        // -NoProfile and a non-interactive host keep this fast and side-effect free.
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }

            throw new TimeoutException("The PowerShell query timed out.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        string error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0 && error.Length > 0)
        {
            throw new InvalidOperationException(error.Trim());
        }

        return stdout.GetAwaiter().GetResult();
    }
}
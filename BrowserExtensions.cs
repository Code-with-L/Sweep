using System.Diagnostics;
using System.Text.Json;

namespace Sweep;

/// <summary>One extension installed in a browser profile.</summary>
public sealed record BrowserExtension(
    string Browser,
    string Profile,
    string Name,
    string Version,
    string Id,
    string Path,
    bool Removable,
    string State);

/// <summary>
/// Enumerates extensions from the Chromium profile layout (Chrome, Edge, Brave, Vivaldi) and
/// from Firefox's extensions.json.
///
/// Read-only for Firefox: its extension state lives in a profile database, so moving a file
/// is not an uninstall. Chromium stores each extension in a folder of its own, which is why
/// removal is offered there and not there.
/// </summary>
public static class BrowserExtensions
{
    public static IReadOnlyList<BrowserExtension> Scan(CancellationToken cancellationToken = default)
    {
        var results = new List<BrowserExtension>();

        foreach ((string browser, string profileName, string profilePath) in ChromiumProfiles())
        {
            try
            {
                ScanChromium(browser, profileName, profilePath, results, cancellationToken);
            }
            catch
            {
                // A locked or malformed profile should not hide the other browsers.
            }
        }

        try
        {
            ScanFirefox(results, cancellationToken);
        }
        catch
        {
            // Same.
        }

        return results
            .OrderBy(extension => extension.Browser, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(extension => extension.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<(string Browser, string UserData)> ChromiumBrowsers()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return ("Chrome", Path.Combine(local, "Google", "Chrome", "User Data"));
        yield return ("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data"));
        yield return ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"));
        yield return ("Vivaldi", Path.Combine(local, "Vivaldi", "User Data"));
    }

    /// <summary>
    /// Every Chromium profile Sweep can read. Shared with the history cleaner so both modules
    /// agree on what a profile is.
    /// </summary>
    public static IReadOnlyList<(string Browser, string Profile, string Path)> ChromiumProfiles()
    {
        var profiles = new List<(string Browser, string Profile, string Path)>();

        foreach ((string browser, string userData) in ChromiumBrowsers())
        {
            try
            {
                if (!Directory.Exists(userData))
                {
                    continue;
                }

                foreach (string profilePath in Directory.EnumerateDirectories(userData))
                {
                    string name = Path.GetFileName(profilePath);
                    if (name == "Default" ||
                        name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                    {
                        profiles.Add((browser, name, profilePath));
                    }
                }
            }
            catch
            {
                // Unreadable profile root - keep the other browsers.
            }
        }

        return profiles;
    }

    private static void ScanChromium(
        string browser,
        string profileName,
        string profilePath,
        List<BrowserExtension> results,
        CancellationToken cancellationToken)
    {
        string extensionsRoot = Path.Combine(profilePath, "Extensions");
        if (!Directory.Exists(extensionsRoot))
        {
            return;
        }

        foreach (string idFolder in Directory.EnumerateDirectories(extensionsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string id = Path.GetFileName(idFolder);
            if (id.Length != 32)
            {
                continue;
            }

            string? manifestPath = Directory
                .EnumerateFiles(idFolder, "manifest.json", SearchOption.AllDirectories)
                .OrderByDescending(path => path)
                .FirstOrDefault();

            if (manifestPath is null)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                JsonElement root = document.RootElement;

                string rawName = GetString(root, "name");
                string name = Localize(root, Path.GetDirectoryName(manifestPath)!, rawName);
                string version = GetString(root, "version");

                results.Add(new BrowserExtension(
                    browser,
                    profileName,
                    string.IsNullOrWhiteSpace(name) ? id : name,
                    version.Length > 0 ? version : Path.GetFileName(Path.GetDirectoryName(manifestPath)!),
                    id,
                    idFolder,
                    Removable: true,
                    State: "Installed"));
            }
            catch
            {
                // A half-written manifest is not worth failing the scan over.
            }
        }
    }

    private static void ScanFirefox(List<BrowserExtension> results, CancellationToken cancellationToken)
    {
        string profilesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mozilla", "Firefox", "Profiles");

        if (!Directory.Exists(profilesRoot))
        {
            return;
        }

        foreach (string profile in Directory.EnumerateDirectories(profilesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string addonsFile = Path.Combine(profile, "extensions.json");
            if (!File.Exists(addonsFile))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(addonsFile));
                if (!document.RootElement.TryGetProperty("addons", out JsonElement addons) ||
                    addons.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement addon in addons.EnumerateArray())
                {
                    if (!string.Equals(GetString(addon, "type"), "extension", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string name = addon.TryGetProperty("defaultLocale", out JsonElement locale)
                        ? GetString(locale, "name")
                        : string.Empty;

                    bool active = !addon.TryGetProperty("active", out JsonElement activeElement) ||
                                  activeElement.ValueKind != JsonValueKind.False;

                    bool userDisabled = addon.TryGetProperty("userDisabled", out JsonElement disabled) &&
                                        disabled.ValueKind == JsonValueKind.True;

                    string path = GetString(addon, "path");
                    if (!Path.IsPathRooted(path))
                    {
                        path = Path.Combine(profile, "extensions", path);
                    }

                    results.Add(new BrowserExtension(
                        "Firefox",
                        Path.GetFileName(profile),
                        name.Length > 0 ? name : GetString(addon, "id"),
                        GetString(addon, "version"),
                        GetString(addon, "id"),
                        path,
                        Removable: false,
                        State: userDisabled ? "Disabled" : active ? "Enabled" : "Inactive"));
                }
            }
            catch
            {
                // Unreadable profile.
            }
        }
    }

    /// <summary>Resolves "__MSG_name__" through the profile's _locales messages.</summary>
    private static string Localize(JsonElement manifest, string manifestDirectory, string value)
    {
        if (!value.StartsWith("__MSG_", StringComparison.Ordinal) ||
            !value.EndsWith("__", StringComparison.Ordinal) ||
            value.Length <= 8)
        {
            return value;
        }

        string key = value[6..^2];
        string locale = GetString(manifest, "default_locale");
        if (locale.Length == 0)
        {
            locale = "en";
        }

        string messagesPath = Path.Combine(manifestDirectory, "_locales", locale, "messages.json");

        try
        {
            if (!File.Exists(messagesPath))
            {
                return value;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(messagesPath));
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Object &&
                    property.Value.TryGetProperty("message", out JsonElement message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? value;
                }
            }
        }
        catch
        {
            // Fall through to the raw token.
        }

        return value;
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>The process names that keep an extension folder locked while a browser runs.</summary>
    public static IReadOnlyList<string> ProcessesFor(string browser) => browser switch
    {
        "Chrome" => ["chrome"],
        "Edge" => ["msedge"],
        "Brave" => ["brave"],
        "Vivaldi" => ["vivaldi"],
        _ => [],
    };

    public static string? RunningProcess(string browser)
    {
        foreach (string name in ProcessesFor(browser))
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0)
                {
                    return name + ".exe";
                }
            }
            catch
            {
                // Enumeration can fail for a protected process; assume not running.
            }
        }

        return null;
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace Sweep;

/// <summary>
/// Turns a product name into conservative search tokens.
/// The whole point is precision: a residue matcher that produces false positives is
/// worse than useless, because the user stops trusting the results.
/// </summary>
public static class NameTokens
{
    /// <summary>Splits on punctuation and camelCase so "AntigravityIDE" yields two words.</summary>
    private static readonly Regex WordPattern = new(
        @"[A-Z]{2,}(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|\d+",
        RegexOptions.Compiled);

    public static IReadOnlyList<string> SplitWords(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var words = new List<string>();
        foreach (Match match in WordPattern.Matches(value))
        {
            words.Add(match.Value.ToLowerInvariant());
        }

        return words;
    }

    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "of", "inc", "inc.", "ltd", "ltd.", "llc", "llp",
        "corp", "corp.", "corporation", "company", "co", "gmbh", "bv", "sa", "ag",
        "plc", "pty", "software", "softwares", "solutions", "system", "systems",
        "app", "apps", "application", "applications", "program", "programs",
        "tool", "tools", "version", "setup", "installer", "update", "edition",
        "win", "windows", "microsoft", "desktop", "x86", "x64", "amd64",
        "arm64", "bit", "bits", "trial", "freeware", "shareware", "demo",
        "sdk", "api", "framework", "runtime", "library", "service", "services",
        "client", "server", "studio", "labs", "technologies", "technology",

        // Generic nouns that appear as whole words in thousands of unrelated folder names.
        "user", "users", "data", "files", "cache", "caches", "backup", "backups",
        "default", "defaults", "common", "config", "configuration", "main",
        "new", "old", "current", "general", "personal", "professional",
        "enterprise", "ultimate", "premium", "basic", "standard", "complete",
        "manager", "bundle", "package", "component", "components", "module",
        "player", "editor", "viewer", "converter", "generator", "manager",

        // Left over from stripping: these describe the packaging, not the product.
        "redistributable", "redist", "visual", "proof", "release", "express",
        "additional", "minimum", "maximum", "premium", "ultimate", "professional",
        "community", "enterprise", "evaluation", "retail", "oem", "bundle",

        // Directory conventions that leak in from install paths.
        "bin", "lib", "libs", "source", "sources", "winget", "share", "shared",
        "tmp", "log", "logs", "sample", "samples", "example", "examples",
        "doc", "docs", "help", "extra", "extras", "addon", "addons",
    };

    /// <summary>
    /// Category nouns that describe what a product IS rather than what it is CALLED.
    /// These are stripped from product names: matching on "media" or "player" finds
    /// "Windows Media Player" and "social-media", which are not leftovers.
    /// </summary>
    private static readonly HashSet<string> CategoryNouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "player", "players", "media", "editor", "editors", "viewer", "browser", "browsers",
        "manager", "studio", "suite", "tool", "tools", "utility", "utilities", "app", "apps",
        "application", "applications", "program", "programs", "software", "service", "services",
        "server", "servers", "client", "clients", "agent", "agents", "framework", "library",
        "libraries", "sdk", "ide", "plugin", "plugins", "pack", "bundle", "extension",
        "extensions", "driver", "drivers", "launcher", "converter", "generator", "desktop",
        "mobile", "console", "terminal", "host", "monitor", "analyzer", "profiler",
        "recorder", "downloader", "uploader", "reader", "writer", "checker", "builder",
        "creator", "maker", "engine", "core", "kit", "hub", "center", "centre", "desk",
        "dashboard", "panel", "wizard", "assistant", "companion", "control", "virtual",
        "machine", "emulator", "sandbox", "container", "daemon", "bot", "parser",
        "compiler", "debugger", "tracer", "scraper", "crawler", "calendar", "mail",
        "mailer", "messenger", "chat", "music", "camera", "notes", "maps", "weather",
        "news", "store", "books", "translate", "player", "remote", "sync", "transfer",
        "share", "backup", "restore", "firewall", "antivirus", "optimizer", "cleaner",
        "defrag", "skin", "theme", "themes", "icon", "icons", "font", "fonts", "dictionary",
        "uninstaller", "updater", "wrapper", "patcher", "cleaner",
    };
    /// <summary>Vendors that would match hundreds of unrelated folders.</summary>
    private static readonly HashSet<string> GenericVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "google", "mozilla", "adobe", "oracle", "sun",
        "java", "openjdk", "samsung", "sony", "lg", "nvidia", "intel", "amd",
        "qualcomm", "broadcom", "cisco", "ibm", "hp", "lenovo", "dell", "asus",
    };

    private static readonly char[] Separators =
    [
        ' ', '\t', '_', '-', '.', ',', '+', '&', '\'', '"', '(', ')', '[', ']', '{', '}', '/', '\\', ':', '!', '?', ';', '#', '*',

        // Unicode spaces: several installers put a non-breaking space in the display name,
        // which otherwise survives as part of a token ("leon2<NBSP>" != "leon2").
        '\u00A0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005',
        '\u2006', '\u2007', '\u2008', '\u2009', '\u200A', '\u202F', '\u205F', '\u3000',
        '\u200B', '\uFEFF',
    ];

    /// <summary>Lowercase alphanumeric only - good for matching "AdobeAcrobat" style folders.</summary>
    public static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static string StripDecorations(string value)
    {
        // Remove bracketed qualifiers: "Notepad++ (64-bit x64)" -> "Notepad++"
        var sb = new StringBuilder(value.Length);
        int depth = 0;
        foreach (char c in value)
        {
            if (c is '(' or '[' or '{')
            {
                depth++;
                sb.Append(' ');
                continue;
            }

            if (c is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
                sb.Append(' ');
                continue;
            }

            sb.Append(depth == 0 ? c : ' ');
        }

        return sb.ToString();
    }

    private static bool IsNoise(string token)
    {
        if (NoiseWords.Contains(token))
        {
            return true;
        }

        // Pure numbers, version fragments, or single characters.
        if (token.Length < 3)
        {
            return true;
        }

        int digits = token.Count(char.IsDigit);
        if (digits > 0 && digits * 2 >= token.Length)
        {
            return true;
        }

        return false;
    }

    private static List<string> RawTokens(string? value, int minLength, bool filterNoise = true)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        string cleaned = StripDecorations(value)
            .Replace('\u00AE', ' ')   // registered
            .Replace('\u2122', ' ')   // trademark
            .Replace('\u00A9', ' ')   // copyright
            .Replace('\u2120', ' ');  // service mark

        foreach (var part in cleaned.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = part.Trim().ToLowerInvariant();
            if (token.Length < minLength)
            {
                continue;
            }

            if (filterNoise && (IsNoise(token) || IsUserName(token)))
            {
                continue;
            }

            if (!result.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(token);
            }
        }

        return result;
    }

    /// <summary>
    /// A product called "Roblox Player for leon2" would otherwise search for "leon2",
    /// which matches the user's own home-directory paths.
    /// </summary>
    private static bool IsUserName(string token) =>
        RunContext.EffectiveUserName.Length > 0 &&
        token.Equals(RunContext.EffectiveUserName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Search tokens derived from the product name.
    ///
    /// Category nouns are dropped first, because a product's identity lives in the words
    /// that distinguish it, not in the words that describe its type. "VLC media player"
    /// must search for "vlc", never for "media" - otherwise the scan reports
    /// "Windows Media Player" and "social-media" as leftovers.
    ///
    /// Shorter tokens are used only when nothing longer survives, so "7-Zip" and
    /// "Battle.net" still produce something usable.
    /// </summary>
    public static IReadOnlyList<string> ForName(string? name)
    {
        // RawTokens with noise filtering can come back empty for an all-noise name such as
        // "Microsoft Windows Desktop Runtime". The unfiltered pass tells us whether the name
        // was actually multi-word, which decides if squashing it is meaningful.
        var all = RawTokens(name, 3);
        var unfiltered = RawTokens(name, 3, filterNoise: false);

        var distinctive = all.Where(token => !CategoryNouns.Contains(token)).ToList();

        // Everything in the name was a category noun. Squash it only when the name really was
        // multi-word: a lone noise word pulled from an install path ("Release", "bin") must
        // never become a search token.
        if (distinctive.Count == 0)
        {
            return Squashable(unfiltered, name) ? [Compact(name)] : [];
        }

        var result = distinctive.Where(token => token.Length >= 4 && !IsNoise(token)).ToList();
        if (result.Count == 0)
        {
            result = distinctive.Where(token => token.Length >= 3 && !IsNoise(token)).ToList();
        }

        if (result.Count == 0 && Squashable(unfiltered, name))
        {
            result.Add(Compact(name));
        }

        return result;
    }

    private static bool Squashable(List<string> unfiltered, string? name) =>
        unfiltered.Count >= 2 && Compact(name).Length >= 4;

    /// <summary>
    /// Publisher-derived tokens. Deliberately conservative - these produce far more
    /// false positives, so generic vendor names are dropped entirely.
    /// </summary>
    public static IReadOnlyList<string> ForPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var raw in publisher.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim().ToLowerInvariant();
            if (token.Length < 5 || GenericVendors.Contains(token) || IsUserName(token))
            {
                continue;
            }

            if (IsNoise(token))
            {
                continue;
            }

            if (!result.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(token);
            }
        }

        return result;
    }

    /// <summary>The leaf folder of an install path, e.g. "Adobe\Acrobat DC" -> "Acrobat DC".</summary>
    public static string? FromInstallLocation(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return null;
        }

        try
        {
            string trimmed = installLocation.Trim().Trim('"', '\\', '/');
            if (trimmed.Length == 0)
            {
                return null;
            }

            var leaf = new DirectoryInfo(trimmed).Name;
            return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Everything worth searching for, ordered most-specific first.
    /// </summary>
    public static ResidueTokenSet BuildTokenSet(InstalledApp app)
    {
        var strong = new List<string>(NameTokens.ForName(app.DisplayName));

        string? leaf = NameTokens.FromInstallLocation(app.InstallLocation);
        if (leaf is not null)
        {
            foreach (var token in NameTokens.ForName(leaf))
            {
                if (!strong.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    strong.Add(token);
                }
            }
        }

        // Publisher tokens overlap name tokens constantly (a product called "Discord" is
        // published by "Discord Inc."). Overlapping tokens stay in the strong list, where
        // they are more reliable, and are dropped from the weak list instead.
        var weak = NameTokens.ForPublisher(app.Publisher)
            .Where(token => !strong.Contains(token, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return new ResidueTokenSet(strong, weak, NameTokens.Compact(app.DisplayName));
    }
}

/// <summary>
/// A candidate name decomposed once.
///
/// This exists because the scan calls Match() tens of thousands of times per registry
/// walk, and the previous implementation re-ran the camelCase regex and rebuilt the
/// compacted form once per token rather than once per candidate - three to five times the
/// work for every single directory and key name.
/// </summary>
public readonly struct NameMatchContext
{
    public NameMatchContext(string candidate)
    {
        Candidate = candidate;
        Words = NameTokens.SplitWords(candidate);
        Compact = NameTokens.Compact(candidate);
    }

    public string Candidate { get; }
    public IReadOnlyList<string> Words { get; }
    public string Compact { get; }

    public bool Matches(string token, int minCompactLength)
    {
        if (Candidate.Length == 0 || token.Length == 0)
        {
            return false;
        }

        if (Candidate.Equals(token, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string word in Words)
        {
            if (word.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (token.Length < minCompactLength)
        {
            return false;
        }

        return Compact.Length > token.Length &&
               Compact.Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Strong tokens drive matches; weak tokens (publisher) only match in narrow roots.</summary>
public sealed record ResidueTokenSet(
    IReadOnlyList<string> Strong,
    IReadOnlyList<string> Weak,
    string CompactName)
{
    public bool IsEmpty => Strong.Count == 0 && Weak.Count == 0;

    public IEnumerable<string> All => Strong.Concat(Weak);

    public string? Match(string candidate)
    {
        if (Strong.Count == 0 || candidate.Length == 0)
        {
            return null;
        }

        var context = new NameMatchContext(candidate);
        return MatchAny(context, Strong, MinCompactLength);
    }

    public string? MatchWeak(string candidate)
    {
        if (Weak.Count == 0 || candidate.Length == 0)
        {
            return null;
        }

        var context = new NameMatchContext(candidate);
        return MatchAny(context, Weak, MinCompactLength + 1);
    }

    /// <summary>Cheap reject before the expensive decomposition, for callers in hot loops.</summary>
    public bool CouldMatch(string candidate)
    {
        if (candidate.Length == 0)
        {
            return false;
        }

        foreach (var token in Strong)
        {
            if (candidate.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? MatchAny(NameMatchContext context, IReadOnlyList<string> tokens, int minCompactLength)
    {
        foreach (string token in tokens)
        {
            if (context.Matches(token, minCompactLength))
            {
                return token;
            }
        }

        return null;
    }

    /// <summary>
    /// Below this length, only whole-word (and camelCase) matches are trusted.
    /// Substring matching on short tokens is what turns "media" into "immediate".
    /// </summary>
    private const int MinCompactLength = 6;
}
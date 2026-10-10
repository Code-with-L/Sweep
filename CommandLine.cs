namespace Sweep;

public readonly record struct CommandLineParts(string FileName, string Arguments);

/// <summary>A switch to add, plus any existing flags that already achieve the same thing.</summary>
public readonly record struct SwitchSpec(string Switch, string[] Aliases);

/// <summary>
/// Splits a registered uninstall command into executable + arguments.
/// Registry values are frequently malformed (unquoted paths containing spaces),
/// so this tolerates more than a naive split.
/// </summary>
public static class CommandLine
{
    private static readonly string[] ExecutableExtensions =
        [".exe", ".com", ".bat", ".cmd", ".msi", ".msp", ".msu", ".ps1", ".lnk"];

    private static bool HasExecutableExtension(string token)
    {
        // Trailing arguments glued to the token (e.g. "a.exe" from "a.exe /S") are fine.
        return ExecutableExtensions.Any(ext => token.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    public static CommandLineParts Split(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new CommandLineParts(string.Empty, string.Empty);
        }

        string cmd = command.Trim();

        if (cmd[0] == '"')
        {
            int close = cmd.IndexOf('"', 1);
            if (close > 1)
            {
                string exe = cmd[1..close];
                string args = cmd[(close + 1)..].Trim();
                return new CommandLineParts(exe, args);
            }

            // Unterminated quote - a known real-world bug in some installers.
            cmd = cmd[1..];
        }

        int firstSpace = cmd.IndexOf(' ');
        if (firstSpace < 0)
        {
            return new CommandLineParts(cmd.Trim(), string.Empty);
        }

        // Unquoted path containing spaces. "C:\Program Files\Android Studio\uninstall.exe"
        // splits into several tokens, and taking the first one that ends in .exe yields
        // "Studio\uninstall.exe" - a path that cannot run. Prefer the longest prefix that
        // is an existing file, which recovers the real executable.
        int boundary = FindLongestExistingPrefix(cmd);
        if (boundary > 0)
        {
            return new CommandLineParts(cmd[..boundary], cmd[boundary..].Trim());
        }

        string first = cmd[..firstSpace];
        if (HasExecutableExtension(first))
        {
            return new CommandLineParts(first, cmd[(firstSpace + 1)..].Trim());
        }

        int i = 0;
        while (i < cmd.Length)
        {
            int start = i;
            while (i < cmd.Length && cmd[i] != ' ')
            {
                i++;
            }

            string token = cmd[start..i];
            if (HasExecutableExtension(token))
            {
                // i can already equal cmd.Length when the executable is the whole string.
                int afterToken = Math.Min(i + 1, cmd.Length);
                return new CommandLineParts(token, cmd[afterToken..].Trim());
            }

            while (i < cmd.Length && cmd[i] == ' ')
            {
                i++;
            }
        }

        return new CommandLineParts(first, cmd[(firstSpace + 1)..].Trim());
    }

    /// <summary>
    /// Returns the length of the longest prefix of <paramref name="command"/> (ending at a
    /// space) that names an existing file, or -1 when there is no such prefix.
    /// </summary>
    private static int FindLongestExistingPrefix(string command)
    {
        int best = -1;

        // The whole string matters too: "C:\Program Files\HandBrake\uninst.exe" has no
        // trailing space, so a space-only scan would never test it.
        string whole = command.TrimEnd();
        if (whole.Length > 0 && File.Exists(whole))
        {
            best = whole.Length;
        }

        for (int i = 0; i < command.Length; i++)
        {
            if (command[i] != ' ')
            {
                continue;
            }

            string candidate = command[..i].TrimEnd();
            if (candidate.Length == 0)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Combines existing arguments with extra switches, skipping any switch whose effect is
    /// already covered. Aliases matter: an uninstaller already invoked with /SILENT must not
    /// also be handed /VERYSILENT, which produces confusing duplicate switches.
    /// </summary>
    public static string ApplySwitches(string existing, IEnumerable<SwitchSpec> specs)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            parts.Add(existing.Trim());
        }

        foreach (var spec in specs)
        {
            bool alreadyCovered = spec.Aliases.Any(alias =>
                !string.IsNullOrEmpty(alias) &&
                existing.Contains(alias, StringComparison.OrdinalIgnoreCase));

            if (!alreadyCovered)
            {
                parts.Add(spec.Switch);
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Turns a bare command name into a real path. Registered uninstall strings commonly
    /// reference "MsiExec.exe" or "winget", which CreateProcess would find via PATH - but
    /// resolving them explicitly keeps diagnostics honest and handles the missing-.exe case.
    /// </summary>
    public static string ResolveExecutable(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return file;
        }

        string expanded = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));

        if (File.Exists(expanded))
        {
            return expanded;
        }

        if (Path.IsPathRooted(expanded))
        {
            return File.Exists(expanded + ".exe") ? expanded + ".exe" : expanded;
        }

        string[] candidates = Path.HasExtension(expanded) ? [expanded] : [expanded, expanded + ".exe"];

        foreach (string name in candidates)
        {
            foreach (string directory in EnumeratePathDirectories())
            {
                try
                {
                    string full = Path.Combine(directory, name);
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
                catch
                {
                    // Malformed PATH entry.
                }
            }
        }

        return Path.HasExtension(expanded) ? expanded : expanded + ".exe";
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            yield break;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return directory.Trim('"');
        }
    }
}
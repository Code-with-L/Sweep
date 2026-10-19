namespace Sweep;

/// <summary>
/// Who this process is really working for.
///
/// When a standard user answers the UAC prompt with a *different* administrator account,
/// the elevated process runs as that administrator: Environment.UserName, the profile paths
/// and HKCU all change. Without this, Sweep silently scanned the administrator's per-user
/// data while the window was titled after the original user's programs - and offered the
/// administrator's leftovers for deletion.
///
/// The launching (medium-integrity) process passes its own identity on the command line, so
/// the elevated copy can tell the two apart.
/// </summary>
internal static class RunContext
{
    private const string InvokerUserFlag = "--invoker-user=";
    private const string InvokerProfileFlag = "--invoker-profile=";

    /// <summary>The account the user launched Sweep as, when elevation changed it.</summary>
    public static string? InvokerUser { get; private set; }

    /// <summary>That account's profile root, e.g. C:\Users\leon2.</summary>
    public static string? InvokerProfile { get; private set; }

    /// <summary>True when the elevated token belongs to somebody other than the invoker.</summary>
    public static bool IsCrossUser =>
        InvokerUser is { Length: > 0 } &&
        !string.Equals(InvokerUser, Environment.UserName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The user whose per-user data should be inspected.</summary>
    public static string EffectiveUserName =>
        InvokerUser is { Length: > 0 } ? InvokerUser : Environment.UserName;

    /// <summary>The profile root that per-user file scans should start from.</summary>
    public static string EffectiveProfile
    {
        get
        {
            if (InvokerProfile is { Length: > 0 } profile && Directory.Exists(profile))
            {
                return profile;
            }

            // Never fall back to this process's profile when the token belongs to somebody
            // else: that is exactly the wrong-user scan this type exists to prevent. Callers
            // treat an empty result as "no per-user profile to scan".
            return IsCrossUser
                ? string.Empty
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    public static void Initialize(string[] args)
    {
        InvokerUser = Value(args, InvokerUserFlag);
        InvokerProfile = Value(args, InvokerProfileFlag);
    }

    /// <summary>Arguments that carry the invoker's identity into the elevated copy.</summary>
    public static IEnumerable<string> ElevationArguments()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return InvokerUserFlag + Environment.UserName;
        if (!string.IsNullOrWhiteSpace(profile))
        {
            yield return InvokerProfileFlag + profile;
        }
    }

    private static string? Value(string[] args, string prefix)
    {
        foreach (string arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && arg.Length > prefix.Length)
            {
                return arg[prefix.Length..].Trim().Trim('"');
            }
        }

        return null;
    }
}

namespace Sweep;

/// <summary>
/// Per-user folders for the account Sweep is working for.
///
/// Under over-the-shoulder elevation the process runs as a different administrator, so
/// Environment.GetFolderPath points at that administrator's profile. Everything that is
/// per-user - browser profiles, caches, the recent-items list - has to come from the invoker
/// instead, or the app quietly operates on the wrong person's data.
/// </summary>
internal static class UserFolders
{
    public static string LocalAppData
    {
        get
        {
            if (!RunContext.IsCrossUser)
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }

            return Sub("AppData", "Local");
        }
    }

    public static string Roaming
    {
        get
        {
            if (!RunContext.IsCrossUser)
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }

            return Sub("AppData", "Roaming");
        }
    }

    public static string Recent
    {
        get
        {
            if (!RunContext.IsCrossUser)
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            }

            string roaming = Roaming;
            return roaming.Length == 0
                ? string.Empty
                : Path.Combine(roaming, "Microsoft", "Windows", "Recent");
        }
    }

    public static string Temp
    {
        get
        {
            if (!RunContext.IsCrossUser)
            {
                return Path.GetTempPath().TrimEnd('\\');
            }

            string local = LocalAppData;
            return local.Length == 0 ? string.Empty : Path.Combine(local, "Temp");
        }
    }

    /// <summary>
    /// Builds a path under the invoker's profile. An empty result means there is no profile to
    /// use, and callers must skip the location: Path.Combine("", "x") would produce a relative
    /// path and silently target the working directory.
    /// </summary>
    private static string Sub(params string[] parts)
    {
        string profile = RunContext.EffectiveProfile;
        if (string.IsNullOrEmpty(profile))
        {
            return string.Empty;
        }

        var all = new List<string>(parts.Length + 1) { profile };
        all.AddRange(parts);
        return Path.Combine(all.ToArray());
    }
}

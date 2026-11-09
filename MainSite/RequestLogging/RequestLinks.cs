namespace MainSite.RequestLogging;

/// <summary>
/// A logged path is attacker-controlled. Only same-origin absolute paths are
/// turned into links: protocol-relative ("//host") and backslash forms would
/// send the reader to another origin.
/// </summary>
public static class RequestLinks
{
    public static string? SafeHref(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return null;
        }

        if (path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\'))
        {
            return null;
        }

        return path;
    }
}

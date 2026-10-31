namespace Cmux.Desktop;

// OSC 7 is untrusted terminal metadata. It may select a local directory only;
// it never supplies a shell, arguments, environment or executable content.
internal static class WorkingDirectoryReport
{
    internal const int MaxUriCharacters = 300000;

    internal static string? Validate(string value)
    {
        if (value.Length > MaxUriCharacters || value.Any(char.IsControl)
            || !value.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || !ValidEscapes(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile
            || uri.IsUnc || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port != -1
            || (uri.Host.Length != 0 && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))) return null;
        try
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(uri.LocalPath));
            return SessionStore.IsExistingLocalDirectory(path) ? path : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    private static bool ValidEscapes(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (value[index] == '%' && (index + 2 >= value.Length
                || !char.IsAsciiHexDigit(value[++index]) || !char.IsAsciiHexDigit(value[++index]))) return false;
        return true;
    }
}

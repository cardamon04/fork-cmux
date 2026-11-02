namespace Cmux.Desktop;

internal static class TerminalLink
{
    internal static bool TryAddress(string? text, out string address)
    {
        address = "";
        if (text is null || text != text.Trim() || text == "about:blank"
            || !SessionSnapshot.IsBrowserAddress(text)) return false;
        address = new Uri(text, UriKind.Absolute).AbsoluteUri;
        return true;
    }
}

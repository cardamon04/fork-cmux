namespace Cmux.Desktop;

// Live display metadata only; this value never enters a session snapshot.
internal sealed class TerminalTitle
{
    internal const int MaxCharacters = WorkspaceTitle.MaxCharacters;
    internal string Value { get; private set; } = "";

    internal bool Update(string? value)
    {
        if (value is null || value.Length != 0 && !WorkspaceTitle.IsValidRename(value) || value == Value) return false;
        Value = value;
        return true;
    }

    internal static string Caption(Strings strings, string? value) => string.IsNullOrEmpty(value)
        ? strings["AppTitle"] : strings.Format("TerminalWindowTitle", value);
}

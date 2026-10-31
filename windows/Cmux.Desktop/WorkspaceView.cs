namespace Cmux.Desktop;

internal sealed class WorkspaceView(string title) : Panel
{
    internal readonly List<TerminalPane> Panes = [];
    internal TerminalPane? Active;
    public override string ToString() => title;
}

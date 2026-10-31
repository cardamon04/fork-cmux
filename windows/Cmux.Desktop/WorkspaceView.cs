namespace Cmux.Desktop;

internal sealed class WorkspaceView(string title) : Panel
{
    internal readonly List<WorkspacePane> Panes = [];
    internal WorkspacePane? Active;
    public override string ToString() => title;
}

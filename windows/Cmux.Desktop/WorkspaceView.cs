namespace Cmux.Desktop;

internal sealed class WorkspaceView(string title) : Panel
{
    internal readonly List<WorkspacePane> Panes = [];
    internal WorkspacePane? Active;
    internal long SelectionRevision;
    public override string ToString() => title;
}

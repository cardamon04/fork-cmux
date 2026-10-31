namespace Cmux.Desktop;

internal static class SessionLayout
{
    internal static WorkspaceSnapshot Capture(WorkspaceView workspace)
    {
        var panes = new List<WorkspacePane>();
        LayoutSnapshot Visit(Control control)
        {
            if (control is SplitContainer split)
                return new("split", Orientation: split.Orientation == Orientation.Vertical ? "vertical" : "horizontal",
                    Ratio: workspace.SplitRatio(split), First: Visit(split.Panel1.Controls[0]), Second: Visit(split.Panel2.Controls[0]));
            var pane = (WorkspacePane)control;
            panes.Add(pane);
            return pane is TerminalPane terminal ? new("terminal", Directory: terminal.LaunchDirectory)
                : new("browser", Url: ((BrowserPane)pane).SessionAddress);
        }
        var layout = Visit(workspace.Controls[0]);
        return new(workspace.Title, panes.IndexOf(workspace.Active!), layout);
    }

    internal static Control Restore(LayoutSnapshot node, WorkspaceView workspace, Func<LayoutSnapshot, WorkspacePane> addPane)
    {
        if (node.Kind != "split") return addPane(node);
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = node.Orientation == "vertical" ? Orientation.Vertical : Orientation.Horizontal,
            SplitterWidth = 5, Panel1MinSize = 0, Panel2MinSize = 0,
        };
        split.Panel1.Controls.Add(Restore(node.First!, workspace, addPane));
        split.Panel2.Controls.Add(Restore(node.Second!, workspace, addPane));
        workspace.PendingRatios.Add(split, node.Ratio!.Value);
        return split;
    }
}

namespace Cmux.Desktop;

internal sealed class WorkspaceView(string title) : Panel
{
    internal string Title => title;
    internal readonly List<WorkspacePane> Panes = [];
    internal readonly Dictionary<SplitContainer, double> PendingRatios = [];
    internal WorkspacePane? Active;
    internal long SelectionRevision;
    internal double SplitRatio(SplitContainer split)
    {
        if (PendingRatios.TryGetValue(split, out var pending)) return pending;
        var available = (split.Orientation == Orientation.Vertical ? split.ClientSize.Width : split.ClientSize.Height) - split.SplitterWidth;
        return available <= 0 ? .5 : Math.Clamp((double)split.SplitterDistance / available, 0, 1);
    }
    internal void ApplyRestoredRatios()
    {
        PerformLayout();
        void Apply(Control control)
        {
            if (control is not SplitContainer split) return;
            if (PendingRatios.Remove(split, out var ratio))
            {
                var available = Math.Max(0, (split.Orientation == Orientation.Vertical ? split.ClientSize.Width : split.ClientSize.Height) - split.SplitterWidth);
                split.SplitterDistance = Math.Clamp((int)Math.Round(available * ratio), 0, available);
                split.PerformLayout();
            }
            Apply(split.Panel1.Controls[0]);
            Apply(split.Panel2.Controls[0]);
        }
        if (Controls.Count != 0) Apply(Controls[0]);
    }
    public override string ToString() => title;
}

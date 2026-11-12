namespace Cmux.Desktop;

internal sealed class WorkspaceView(string title) : Panel
{
    internal string Title { get; private set; } = title;
    internal bool TryRename(string? value)
    {
        if (value is null || !WorkspaceTitle.IsValidRename(value)) return false;
        Title = value;
        return true;
    }
    internal readonly List<WorkspacePane> Panes = [];
    internal readonly Dictionary<SplitContainer, double> PendingRatios = [];
    private readonly HashSet<SplitContainer> waitingForExtent = [];
    private bool applyingRatios;
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
        if (applyingRatios || IsDisposed) return;
        applyingRatios = true;
        try
        {
            PerformLayout();
            void Apply(Control control)
            {
                // Resizing can also occur while a pane transaction reparents children.
                if (control.IsDisposed || control is not SplitContainer split
                    || split.Panel1.Controls.Count != 1 || split.Panel2.Controls.Count != 1) return;
                if (PendingRatios.TryGetValue(split, out var ratio))
                {
                    var available = (split.Orientation == Orientation.Vertical ? split.ClientSize.Width : split.ClientSize.Height) - split.SplitterWidth;
                    var maximum = available - split.Panel2MinSize;
                    if (available <= 0 || maximum < split.Panel1MinSize) WaitForExtent(split);
                    else
                    {
                        split.SplitterDistance = Math.Clamp((int)Math.Round(available * ratio), split.Panel1MinSize, maximum);
                        PendingRatios.Remove(split);
                        StopWaiting(split);
                        split.PerformLayout();
                    }
                }
                else StopWaiting(split);
                Apply(split.Panel1.Controls[0]);
                Apply(split.Panel2.Controls[0]);
            }
            if (Controls.Count == 1) Apply(Controls[0]);
        }
        finally { applyingRatios = false; }
    }
    private void WaitForExtent(SplitContainer split)
    {
        if (!waitingForExtent.Add(split)) return;
        split.SizeChanged += ExtentChanged;
        split.Disposed += SplitDisposed;
    }
    private void StopWaiting(SplitContainer split)
    {
        if (!waitingForExtent.Remove(split)) return;
        split.SizeChanged -= ExtentChanged;
        split.Disposed -= SplitDisposed;
    }
    private void ExtentChanged(object? sender, EventArgs e)
    {
        if (sender is not SplitContainer split) return;
        if (!PendingRatios.ContainsKey(split)) { StopWaiting(split); return; }
        if (Visible) ApplyRestoredRatios();
    }
    private void SplitDisposed(object? sender, EventArgs e)
    {
        if (sender is not SplitContainer split) return;
        PendingRatios.Remove(split);
        StopWaiting(split);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var split in waitingForExtent.ToArray()) StopWaiting(split);
            PendingRatios.Clear();
        }
        base.Dispose(disposing);
    }
    public override string ToString() => Title;
}

using Microsoft.Web.WebView2.Core;

namespace Cmux.Desktop;

internal abstract class WorkspacePane : UserControl, IAsyncDisposable
{
    protected WorkspacePane()
    {
        TabStop = true;
        GotFocus += (_, _) => NotifyActivated();
    }
    internal event Action<WorkspacePane>? Activated;
    internal event Action<string>? Failed;
    internal abstract CoreWebView2 Core { get; }
    internal abstract PaneKind Kind { get; }
    internal virtual bool SupportsClipboard => false;
    internal abstract Task Start(WebViewProfile profile);
    internal abstract void FocusPane();
    internal virtual Task Copy() => Task.CompletedTask;
    internal virtual void Paste() { }
    protected void NotifyActivated() => Activated?.Invoke(this);
    protected void NotifyFailure(string message) => Failed?.Invoke(message);
    public abstract ValueTask DisposeAsync();
}

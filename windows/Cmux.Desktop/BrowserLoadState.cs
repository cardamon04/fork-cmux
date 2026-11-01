namespace Cmux.Desktop;

// Pane-owned navigation identity; stale completions cannot finish a newer load.
internal sealed class BrowserLoadState
{
    internal ulong NavigationId { get; private set; }
    internal bool IsLoading { get; private set; }
    internal bool StopRequested { get; private set; }

    internal void Begin(ulong navigationId)
    {
        if (IsLoading && NavigationId == navigationId) return; // Redirect in this navigation.
        NavigationId = navigationId;
        IsLoading = true;
        StopRequested = false;
    }
    internal bool RequestStop()
    {
        if (!IsLoading || StopRequested) return false;
        StopRequested = true;
        return true;
    }
    internal bool Complete(ulong navigationId)
    {
        if (!IsLoading || NavigationId != navigationId) return false;
        IsLoading = false;
        return true;
    }

    // Null leaves an existing policy/cancellation explanation visible.
    internal string? CompletionStatus(bool success, bool canceled) => success ? "Ready"
        : canceled ? (StopRequested ? "NavigationStopped" : null) : "NavigationFailed";
}

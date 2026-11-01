using System.Globalization;
using Cmux.Desktop;

// Pure background entrypoint: no Form/WebView or native desktop operations.
internal static class BrowserLoadStateScenario
{
    internal static void Run(List<string> passed)
    {
        var state = new BrowserLoadState();
        Require(!state.IsLoading && !state.StopRequested && !state.RequestStop() && !state.Complete(0), "Idle navigation accepted Stop/completion.");
        Pass(passed, "browser-load-idle-does-not-stop");
        state.Begin(42);
        Require(state.IsLoading && !state.StopRequested && state.RequestStop() && !state.RequestStop(), "A pending navigation did not accept exactly one Stop.");
        state.Begin(42);
        Require(state.IsLoading && state.StopRequested, "A redirect discarded its pending Stop.");
        Require(state.Complete(42) && !state.IsLoading && state.StopRequested && !state.Complete(42), "Stop completion was not idempotent.");
        Pass(passed, "browser-load-stop-redirect-and-idempotent-completion");
        state.Begin(43);
        Require(state.IsLoading && !state.StopRequested && !state.Complete(42), "A stale completion finished or contaminated the new load.");
        state.RequestStop();
        state.Begin(44);
        Require(state.NavigationId == 44 && state.IsLoading && !state.StopRequested && !state.Complete(43), "A superseded Stop reached a new navigation.");
        Require(state.Complete(44) && !state.IsLoading && !state.StopRequested, "A normal completion retained Stop state.");
        Pass(passed, "browser-load-new-navigation-rejects-stale-stop-and-completion");
        var other = new BrowserLoadState();
        other.Begin(ulong.MaxValue);
        state.Begin(0);
        state.RequestStop();
        Require(other.IsLoading && !other.StopRequested && other.Complete(ulong.MaxValue), "Stop state leaked between panes or truncated a navigation ID.");
        Require(state.IsLoading && state.StopRequested && state.Complete(0), "Zero navigation identity was treated as an idle sentinel.");
        Pass(passed, "browser-load-pane-isolation-and-full-width-identity");
        state.Begin(45);
        state.Complete(45);
        Require(state.CompletionStatus(false, true) is null, "A non-user cancellation overwrote an existing policy-denial message.");
        Require(state.CompletionStatus(true, false) == "Ready" && state.CompletionStatus(false, false) == "NavigationFailed", "Completion status confused success and a real engine failure.");
        state.Begin(46);
        state.RequestStop();
        state.Complete(46);
        Require(state.CompletionStatus(false, true) == "NavigationStopped", "An explicit Stop lost its cancellation explanation.");
        Pass(passed, "browser-load-cancellation-preserves-policy-denial");
        foreach (var locale in new[] { "en", "ja", "de", "fr", "ar", "es", "zh-Hant", "zh-Hans", "ko" })
        {
            var strings = new Strings(CultureInfo.GetCultureInfo(locale));
            Require(!string.IsNullOrWhiteSpace(strings["Stop"]) && !string.IsNullOrWhiteSpace(strings["NavigationStopped"]), "Stop localization is missing.");
        }
        Pass(passed, "browser-load-nine-locale-stop-controls");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pass(List<string> passed, string name) { passed.Add(name); Console.WriteLine("passed: " + name); }
}

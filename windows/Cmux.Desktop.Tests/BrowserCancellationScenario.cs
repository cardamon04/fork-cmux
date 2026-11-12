using System.Reflection;
using System.Text.Json;
using Cmux.Desktop;
using Microsoft.Web.WebView2.Core;

// Runs only in the existing isolated native CI lane while the user's PC is reserved.
internal static class BrowserCancellationScenario
{
    internal static async Task Run(DesktopWindow window, List<string> passed)
    {
        await using var server = new BrowserFixture();
        var errors = new List<Exception>();
        window.SelectWorkspace(0);
        var terminal = (TerminalPane)window.Current!.Active!;
        var terminalPid = terminal.ProcessId;
        foreach (var mode in new[] { "engine-stop", "protocol-stop" })
        {
            await window.ClickAction("OpenBrowser");
            var browser = (BrowserPane)window.Current!.Active!;
            try { await Exercise(browser, server, mode, passed); }
            catch (Exception error)
            {
                Console.WriteLine("BROWSER_CANCEL_FAILURE " + JsonSerializer.Serialize(new { mode, error = error.Message }));
                errors.Add(error);
            }
            finally
            {
                if (!browser.IsDisposed && window.Current?.Panes.Contains(browser) == true)
                {
                    browser.FocusPane();
                    await window.ClickAction("ClosePane");
                }
                Require(window.Current!.Active == terminal && terminal.ProcessId == terminalPid,
                    "Canceling/closing an owned browser changed the original terminal.");
            }
        }
        if (errors.Count != 0) throw new AggregateException("Browser cancellation status regressions failed.", errors);
    }

    private static async Task Exercise(BrowserPane pane, BrowserFixture server, string mode, List<string> passed)
    {
        var strings = new Strings();
        await Navigate(pane, server.Url("one"));
        var core = pane.Core;
        var profile = core.Environment.UserDataFolder;
        var value = "owned-cancel-" + Guid.NewGuid().ToString("N");
        await core.ExecuteScriptAsync("window.cmuxCancellation=" + JsonSerializer.Serialize(value));
        var loading = (BrowserLoadState)typeof(BrowserPane).GetField("loading", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pane)!;
        using (var pending = new Navigation(pane))
        {
            var held = server.HoldRequest();
            pane.NavigateFromAddress(server.Url(held.Path));
            await held.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Require(!held.Closed.Task.IsCompleted && !pending.Completed.IsCompleted && loading.IsLoading
                && !loading.StopRequested && pane.IsNavigationEnabled("Stop") && pane.StatusText == strings["Loading"],
                "The exact owned navigation was not pending without an application Stop request.");
            Console.WriteLine("BROWSER_CANCEL_PRECONDITION " + JsonSerializer.Serialize(new { mode, id = pending.Id,
                loading.IsLoading, loading.StopRequested, status = pane.StatusText, held.Path }));
            if (mode == "engine-stop") core.Stop();
            else await core.CallDevToolsProtocolMethodAsync("Page.stopLoading", "{}");
            var result = await pending.Completed.WaitAsync(TimeSpan.FromSeconds(20));
            var disconnect = await held.Closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine("BROWSER_CANCEL_COMPLETION " + JsonSerializer.Serialize(new { mode, id = pending.Id,
                result.Id, result.Success, error = result.Error.ToString(), loading.IsLoading, loading.StopRequested,
                status = pane.StatusText, stopEnabled = pane.IsNavigationEnabled("Stop"), disconnect }));
            Require(!result.Success && result.Error == CoreWebView2WebErrorStatus.OperationCanceled && result.Id == pending.Id
                && !loading.IsLoading && !loading.StopRequested && !pane.IsNavigationEnabled("Stop"),
                "The test did not observe completion of the current engine-canceled navigation.");
            Require(pane.StatusText == strings["NavigationStopped"], "An engine-canceled navigation left its transient Loading status visible.");
            Pass(mode, "current-cancel-resolves-loading", passed);
            Require(core == pane.Core && core.Environment.UserDataFolder == profile
                && JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("window.cmuxCancellation")) == value,
                "Cancellation replaced the committed document, browser instance or owned profile.");
            Pass(mode, "committed-document-and-profile-preserved", passed);
        }

        await Navigate(pane, server.Url("two"));
        Require(pane.StatusText == strings["Ready"] && !pane.IsNavigationEnabled("Stop"), "The next navigation did not recover from cancellation.");
        using (var back = new Navigation(pane))
        {
            pane.ClickNavigation("Back");
            Require((await back.Completed.WaitAsync(TimeSpan.FromSeconds(20))).Success && core.Source == server.Url("one"), "Cancellation corrupted Back history.");
        }
        using (var forward = new Navigation(pane))
        {
            pane.ClickNavigation("Forward");
            Require((await forward.Completed.WaitAsync(TimeSpan.FromSeconds(20))).Success && core.Source == server.Url("two"), "Cancellation corrupted Forward history.");
        }
        Pass(mode, "next-navigation-and-history-recover", passed);

        using (var pending = new Navigation(pane))
        {
            var held = server.HoldRequest();
            pane.NavigateFromAddress(server.Url(held.Path));
            await held.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Require(loading.IsLoading && !loading.StopRequested && !pending.Completed.IsCompleted, "The policy control lacks an accepted pending navigation.");
            using (var blocked = new Navigation(pane))
            {
                var forbidden = new Uri(Path.Combine(Path.GetTempPath(), "cmux-cancel-blocked-" + Guid.NewGuid().ToString("N") + ".txt")).AbsoluteUri;
                core.Navigate(forbidden);
                Require(!(await blocked.Completed.WaitAsync(TimeSpan.FromSeconds(20))).Success && pane.StatusText == strings["BlockedAddress"], "The policy control did not refuse the navigation.");
            }
            if (!pending.Completed.IsCompleted) core.Stop();
            var result = await pending.Completed.WaitAsync(TimeSpan.FromSeconds(20));
            await held.Closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Require(!result.Success && result.Error == CoreWebView2WebErrorStatus.OperationCanceled && !loading.StopRequested
                && !loading.IsLoading && pane.StatusText == strings["BlockedAddress"], "Cancellation overwrote a policy explanation or retained its pending state.");
            Pass(mode, "policy-explanation-preserved", passed);
        }
    }

    private static async Task Navigate(BrowserPane pane, string address)
    {
        using var navigation = new Navigation(pane);
        pane.NavigateFromAddress(address);
        Require((await navigation.Completed.WaitAsync(TimeSpan.FromSeconds(20))).Success, "The owned browser fixture did not load.");
    }
    private sealed class Navigation : IDisposable
    {
        private readonly CoreWebView2 core;
        private readonly TaskCompletionSource<(ulong Id, bool Success, CoreWebView2WebErrorStatus Error)> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ulong? Id { get; private set; }
        internal Task<(ulong Id, bool Success, CoreWebView2WebErrorStatus Error)> Completed => completion.Task;
        internal Navigation(BrowserPane pane) { core = pane.Core; core.NavigationStarting += Starting; core.NavigationCompleted += Finished; }
        private void Starting(object? sender, CoreWebView2NavigationStartingEventArgs args) { Id ??= args.NavigationId; }
        private void Finished(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId == Id) completion.TrySetResult((args.NavigationId, args.IsSuccess, args.WebErrorStatus));
        }
        public void Dispose() { core.NavigationStarting -= Starting; core.NavigationCompleted -= Finished; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string mode, string name, List<string> passed) { var value = "browser-cancel-" + mode + "-" + name; passed.Add(value); Console.WriteLine("passed: " + value); }
}

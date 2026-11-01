using System.Text.Json;
using Cmux.Desktop;
using Microsoft.Web.WebView2.Core;

// GUI scenario: isolated hosted runner only while the user's desktop is reserved.
internal static class BrowserStopScenario
{
    internal static async Task Run(DesktopWindow window, List<string> passed, string? artifacts = null)
    {
        await using var fixture = new BrowserFixture();
        window.SelectWorkspace(0);
        var terminal = (TerminalPane)window.Current!.Active!;
        var process = terminal.ProcessId;
        await window.ClickAction("OpenBrowser");
        var browser = (BrowserPane)window.Current.Active!;
        Require(browser.Controls.OfType<ToolStrip>().Single(strip => strip is not StatusStrip).Items["Stop"] is ToolStripButton,
            "The browser has no user action to stop an in-flight page load.");
        Require(!browser.IsNavigationEnabled("Stop"), "Stop was enabled for the idle initial document.");
        try
        {
            await Navigate(browser, fixture.Url("one"));
            await browser.Core.ExecuteScriptAsync("document.querySelector('#name').value='keep 日本語🍀'");
            using (var loading = new Navigation(browser))
            {
                var held = fixture.HoldRequest();
                browser.NavigateFromAddress(fixture.Url(held.Path));
                await held.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Require(!held.Closed.Task.IsCompleted && !loading.Completed.IsCompleted && browser.IsNavigationEnabled("Stop"), "The exact owned connection was not pending with Stop available.");
                browser.ClickNavigation("Stop");
                var result = await loading.Completed.WaitAsync(TimeSpan.FromSeconds(20));
                string? source = null;
                string? sourceError = null;
                try { source = browser.Core.Source; }
                catch (Exception error) { sourceError = error.GetType().Name; }
                Console.WriteLine("BROWSER_STOP_COMPLETION " + JsonSerializer.Serialize(new
                {
                    result.Success, error = result.Error.ToString(), code = (int)result.Error,
                    navigationId = loading.Id, status = browser.StatusText,
                    stopEnabled = browser.IsNavigationEnabled("Stop"), source, sourceError,
                }));
                var disconnect = await held.Closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Require(!result.Success && IsStopped(result.Error), "Stop did not cancel the actual engine navigation.");
                Require(!browser.IsNavigationEnabled("Stop") && browser.StatusText == new Strings()["NavigationStopped"], "Canceled loading retained the loading state.");
                var input = JsonSerializer.Deserialize<string>(await browser.Core.ExecuteScriptAsync("document.querySelector('#name').value"));
                Require(input == "keep 日本語🍀", "Stopping a pending load replaced the committed document/input.");
                Console.WriteLine("BROWSER_STOP_NATIVE " + JsonSerializer.Serialize(new { result.Success, error = result.Error.ToString(), input, status = browser.StatusText, held.Path, disconnect }));
            }
            if (artifacts is not null)
            {
                Directory.CreateDirectory(artifacts);
                OwnedWindows.Capture(window.Handle, Path.Combine(artifacts, "browser-stop.png"));
            }
            Pass(passed, "browser-stop-real-pending-request-and-committed-document");

            await Navigate(browser, fixture.Url("two"));
            Require(!browser.IsNavigationEnabled("Stop") && browser.StatusText == new Strings()["Ready"], "Navigation after Stop did not recover.");
            using (var back = new Navigation(browser))
            {
                browser.ClickNavigation("Back");
                Require((await back.Completed.WaitAsync(TimeSpan.FromSeconds(20))).Success, "Back failed after a stopped navigation.");
            }
            Require(browser.Core.Source == fixture.Url("one") && browser.IsNavigationEnabled("Forward"), "Stop corrupted browser history.");
            using (var forward = new Navigation(browser))
            {
                browser.ClickNavigation("Forward");
                Require((await forward.Completed.WaitAsync(TimeSpan.FromSeconds(20))).Success, "Forward failed after Stop.");
            }
            Require(browser.Core.Source == fixture.Url("two"), "Forward did not recover the loaded page.");
            Pass(passed, "browser-stop-next-navigation-and-history-recover");

            using (var firstLoading = new Navigation(browser))
            {
                var firstRequest = fixture.HoldRequest();
                browser.NavigateFromAddress(fixture.Url(firstRequest.Path));
                await firstRequest.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await window.ClickAction("OpenBrowser");
                var second = (BrowserPane)window.Current!.Active!;
                var secondRequest = fixture.HoldRequest();
                using (var secondLoading = new Navigation(second))
                {
                    second.NavigateFromAddress(fixture.Url(secondRequest.Path));
                    await secondRequest.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    var pending = new
                    {
                        firstClosed = firstRequest.Closed.Task.IsCompleted, secondClosed = secondRequest.Closed.Task.IsCompleted,
                        firstCompleted = firstLoading.Completed.IsCompleted, secondCompleted = secondLoading.Completed.IsCompleted,
                        firstStop = browser.IsNavigationEnabled("Stop"), secondStop = second.IsNavigationEnabled("Stop")
                    };
                    Console.WriteLine("BROWSER_STOP_PENDING " + JsonSerializer.Serialize(pending));
                    Require(!pending.firstClosed && !pending.secondClosed && !pending.firstCompleted && !pending.secondCompleted
                        && pending.firstStop && pending.secondStop, "The two exact connections were not independently pending.");
                    browser.ClickNavigation("Stop");
                    var result = await firstLoading.Completed.WaitAsync(TimeSpan.FromSeconds(20));
                    var disconnect = await firstRequest.Closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    Require(!result.Success && IsStopped(result.Error), "The intended first pane did not stop.");
                    Require(!secondRequest.Closed.Task.IsCompleted && !secondLoading.Completed.IsCompleted && second.IsNavigationEnabled("Stop"), "Stopping one pane canceled its neighbor.");
                    Console.WriteLine("BROWSER_STOP_ISOLATION " + JsonSerializer.Serialize(new { firstRequest = firstRequest.Path, disconnect, secondRequest = secondRequest.Path, neighborStillPending = true }));
                    second.FocusPane();
                }
                await window.ClickAction("ClosePane");
                var closed = await secondRequest.Closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Console.WriteLine("BROWSER_STOP_CLOSE " + JsonSerializer.Serialize(new { secondRequest.Path, disconnect = closed }));
            }
            browser.FocusPane();
            await window.ClickAction("ClosePane");
            Require(window.Current!.Active == terminal && terminal.ProcessId == process, "Browser Stop changed the original terminal process/selection.");
            Pass(passed, "browser-stop-pane-isolation-and-close-while-loading");
        }
        finally
        {
            if (!browser.IsDisposed && window.Current?.Panes.Contains(browser) == true)
            {
                browser.FocusPane();
                await window.ClickAction("ClosePane");
            }
        }
    }

    private static bool IsStopped(CoreWebView2WebErrorStatus error) => error is
        CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted;

    private static async Task Navigate(BrowserPane pane, string address)
    {
        using var navigation = new Navigation(pane);
        pane.NavigateFromAddress(address);
        Require((await navigation.Completed.WaitAsync(TimeSpan.FromSeconds(20))).Success, "The owned document did not load.");
    }
    private sealed class Navigation : IDisposable
    {
        private readonly CoreWebView2 core;
        private readonly TaskCompletionSource<(bool Success, CoreWebView2WebErrorStatus Error)> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ulong? id;
        internal Task<(bool Success, CoreWebView2WebErrorStatus Error)> Completed => completed.Task;
        internal ulong? Id => id;
        internal Navigation(BrowserPane pane)
        {
            core = pane.Core;
            core.NavigationStarting += Starting;
            core.NavigationCompleted += Finished;
        }
        private void Starting(object? sender, CoreWebView2NavigationStartingEventArgs args) => id = args.NavigationId;
        private void Finished(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId == id) completed.TrySetResult((args.IsSuccess, args.WebErrorStatus));
        }
        public void Dispose() { core.NavigationStarting -= Starting; core.NavigationCompleted -= Finished; }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pass(List<string> passed, string name) { passed.Add(name); Console.WriteLine("passed: " + name); }
}

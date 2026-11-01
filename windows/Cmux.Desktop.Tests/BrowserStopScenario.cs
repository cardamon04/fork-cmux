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
                browser.NavigateFromAddress(fixture.Url("slow"));
                await fixture.Requested("slow").WaitAsync(TimeSpan.FromSeconds(20));
                Require(!loading.Completed.IsCompleted && browser.IsNavigationEnabled("Stop"), "The owned request was not pending with Stop available.");
                browser.ClickNavigation("Stop");
                var result = await loading.Completed.WaitAsync(TimeSpan.FromSeconds(20));
                Require(!result.Success && result.Error == CoreWebView2WebErrorStatus.OperationCanceled, "Stop did not cancel the actual engine navigation.");
                Require(!browser.IsNavigationEnabled("Stop") && browser.StatusText == new Strings()["NavigationStopped"], "Canceled loading retained the loading state.");
                var input = JsonSerializer.Deserialize<string>(await browser.Core.ExecuteScriptAsync("document.querySelector('#name').value"));
                Require(input == "keep 日本語🍀", "Stopping a pending load replaced the committed document/input.");
                Console.WriteLine("BROWSER_STOP_NATIVE " + JsonSerializer.Serialize(new { result.Success, result.Error, input, status = browser.StatusText }));
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
                browser.NavigateFromAddress(fixture.Url("slow"));
                await Program.Until(() => Task.FromResult(fixture.Requests("slow") >= 2), "The first pane did not enter its second held request.");
                await window.ClickAction("OpenBrowser");
                var second = (BrowserPane)window.Current!.Active!;
                using (var secondLoading = new Navigation(second))
                {
                    second.NavigateFromAddress(fixture.Url("slow"));
                    await Program.Until(() => Task.FromResult(fixture.Requests("slow") >= 3), "The second pane's held request did not arrive.");
                    Require(browser.IsNavigationEnabled("Stop") && second.IsNavigationEnabled("Stop"), "The two pending panes did not retain independent stop state.");
                    browser.ClickNavigation("Stop");
                    Require((await firstLoading.Completed.WaitAsync(TimeSpan.FromSeconds(20))).Error == CoreWebView2WebErrorStatus.OperationCanceled, "The intended first pane did not stop.");
                    Require(!secondLoading.Completed.IsCompleted && second.IsNavigationEnabled("Stop"), "Stopping one pane canceled its neighbor.");
                }
                second.FocusPane();
                await window.ClickAction("ClosePane");
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

using System.Text.Json;
using Cmux.Desktop;
using Microsoft.Web.WebView2.Core;

internal static class BrowserScenario
{
    internal static async Task Run(DesktopWindow window, List<string> passed, string? artifacts)
    {
        await using var fixture = new BrowserFixture();
        window.SelectWorkspace(0);
        var terminal = (TerminalPane)window.Current!.Active!;
        var terminalPid = terminal.ProcessId;
        await window.ClickAction("OpenBrowser");
        var browser = (BrowserPane)window.Current.Active!;
        Require(window.Current.Panes.Count == 2, "Browser did not open beside the terminal.");
        Require(!window.IsActionEnabled("Copy") && !window.IsActionEnabled("Paste"), "Terminal clipboard actions remained enabled for web content.");
        var webMessages = 0;
        browser.Core.WebMessageReceived += (_, _) => webMessages++;
        await Navigate(browser, fixture.Url("one"));
        Require(browser.AddressText == fixture.Url("one"), "Address bar did not reflect the loaded document.");
        Require(await Evaluate(browser, "document.querySelector('#page').textContent") == "\"/one\"", "Wrong browser document loaded.");
        await Click(browser, "#name");
        await browser.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = "ブラウザ😀" }));
        await Click(browser, "#apply");
        var result = JsonSerializer.Deserialize<string>(await Evaluate(browser, "document.querySelector('#result').textContent"));
        Require(result == "ブラウザ😀", "Browser input did not reach the page: " + await Evaluate(browser, "JSON.stringify({value:document.querySelector('#name').value,active:document.activeElement.id,trusted:window.trusted,result:document.querySelector('#result').textContent})"));
        Require(await Evaluate(browser, "window.trusted") == "true", "Browser click was not trusted.");
        Pass("browser-pane-and-interactive-page", passed);

        await Navigate(browser, fixture.Url("two"));
        Require(browser.IsNavigationEnabled("Back"), "Back is unavailable after navigation.");
        await Navigation(browser, () => browser.ClickNavigation("Back"));
        Require(browser.Core.Source == fixture.Url("one"), "Back did not restore the previous page.");
        Require(browser.IsNavigationEnabled("Forward"), "Forward is unavailable after back.");
        await Navigation(browser, () => browser.ClickNavigation("Forward"));
        Require(browser.Core.Source == fixture.Url("two"), "Forward did not restore the next page.");
        var before = fixture.Requests("two");
        await Navigation(browser, () => browser.ClickNavigation("Reload"));
        Require(fixture.Requests("two") > before, "Reload did not fetch the document.");
        Pass("browser-address-history-and-reload", passed);

        var browserProcess = browser.Core.BrowserProcessId;
        var beforeInspector = OwnedWindows.Visible(browserProcess);
        browser.ClickNavigation("DeveloperTools");
        await Program.Until(() => Task.FromResult(OwnedWindows.Visible(browserProcess).Except(beforeInspector).Any()), "Developer tools did not open an owned window.");
        foreach (var inspector in OwnedWindows.Visible(browserProcess).Except(beforeInspector)) OwnedWindows.Close(inspector, browserProcess);
        await Program.Until(() => Task.FromResult(!OwnedWindows.Visible(browserProcess).Except(beforeInspector).Any()), "The owned inspector did not close.");
        Require(browser.Core.Source == fixture.Url("two"), "Opening the inspector replaced the page.");
        Pass("browser-native-developer-tools", passed);

        var browserCookies = await browser.Core.CookieManager.GetCookiesAsync(fixture.Origin);
        Require(browserCookies.Any(c => c.Name == "cmuxBrowser" && c.Value == "owned"), "Cookie separation lacks a stored-cookie precondition.");
        Require((await terminal.Core.CookieManager.GetCookiesAsync(fixture.Origin)).Count == 0, "Browser cookies reached the terminal profile.");
        Require(!string.Equals(browser.Core.Environment.UserDataFolder, terminal.Core.Environment.UserDataFolder, StringComparison.OrdinalIgnoreCase), "Terminal and browser share a profile folder.");
        Require(webMessages == 0, "Untrusted page obtained a native message channel.");
        Require(!browser.Core.Settings.AreHostObjectsAllowed && !browser.Core.Settings.IsWebMessageEnabled, "Native web capabilities were enabled.");
        Require(!(await Program.Text(terminal)).Contains("BROWSER_INJECTION"), "Web content sent input to the terminal.");
        Require(terminal.Core.Source == TerminalPane.Page && terminal.ProcessId == terminalPid, "Web navigation replaced the terminal.");
        await window.ClickAction("OpenBrowser");
        var other = (BrowserPane)window.Current.Active!;
        await Navigate(other, fixture.Url("two"));
        Require(string.Equals(other.Core.Environment.UserDataFolder, browser.Core.Environment.UserDataFolder, StringComparison.OrdinalIgnoreCase), "Browser panes used different profiles.");
        Require((await other.Core.CookieManager.GetCookiesAsync(fixture.Origin)).Any(c => c.Name == "cmuxBrowser"), "Browser panes did not share their browser profile.");
        Require(await Evaluate(other, "localStorage.getItem('cmuxBrowser')") == "\"owned\"", "Browser storage was not retained across panes.");
        await window.ClickAction("ClosePane");
        Pass("browser-profile-and-native-boundary", passed);

        var forbiddenFile = new Uri(Path.Combine(Path.GetTempPath(), "cmux-disallowed-" + Guid.NewGuid().ToString("N") + ".txt")).AbsoluteUri;
        foreach (var invalid in new[] { "javascript:alert(1)", forbiddenFile, "cmux-test-external://blocked", "", "https://example.invalid/\nnext" })
        {
            browser.NavigateFromAddress(invalid);
            Require(browser.Core.Source == fixture.Url("two"), "Invalid address changed the loaded document.");
            Require(browser.StatusText == new Strings()["InvalidAddress"], "Invalid address was not explained.");
        }
        Require(BrowserPane.TryAddress("localhost:3000", out var local) && local == "http://localhost:3000/", "Local development address was not normalized.");
        Require(BrowserPane.TryAddress("example.com", out var remote) && remote == "https://example.com/", "HTTPS default was not applied.");
        await Navigation(browser, () => browser.Core.Navigate(forbiddenFile), success: false);
        Require(browser.StatusText == new Strings()["BlockedAddress"], "Engine-level file navigation was not blocked.");
        var popup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.Core.NewWindowRequested += (_, e) => popup.TrySetResult(e.Handled);
        await Click(browser, "#popup");
        Require(await popup.Task.WaitAsync(TimeSpan.FromSeconds(20)), "Web popup was not handled.");
        var download = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.Core.DownloadStarting += (_, e) => download.TrySetResult(e.Cancel);
        await Click(browser, "#download");
        Require(await download.Task.WaitAsync(TimeSpan.FromSeconds(20)), "Download was not cancelled before a user path was written.");
        await Navigate(browser, fixture.Url("two"));
        await Click(browser, "#location");
        await Program.Until(async () => await Evaluate(browser, "window.locationResult") == "\"denied\"", "Page permission was not denied.");
        Pass("browser-navigation-popup-download-and-permission-policy", passed);

        await Navigate(browser, fixture.Url("one"));
        browser.FocusPane();
        await Program.Until(() => Task.FromResult(window.Current.Active == browser), "Browser focus did not select its pane.");
        window.SelectWorkspace(1);
        window.SelectWorkspace(0);
        Require(window.Current!.Active == browser && browser.Core.Source == fixture.Url("one"), "Workspace switching lost browser state.");
        if (artifacts is not null)
        {
            Directory.CreateDirectory(artifacts);
            using var image = File.Create(Path.Combine(artifacts, "browser.png"));
            await browser.Core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            OwnedWindows.Capture(window.Handle, Path.Combine(artifacts, "desktop.png"));
        }
        browser.NavigateFromAddress(fixture.Url("slow"));
        await fixture.Requested("slow").WaitAsync(TimeSpan.FromSeconds(20));
        await window.ClickAction("ClosePane");
        Require(window.Current.Active == terminal && window.IsActionEnabled("Paste"), "Closing browser did not restore terminal actions.");
        await Program.Command(terminal, "Write-Output ('BROWSER_' + 'CLOSED')");
        await Program.Contains(terminal, "BROWSER_CLOSED");
        await window.ClickAction("OpenBrowser");
        var reopened = (BrowserPane)window.Current.Active!;
        await Navigate(reopened, fixture.Url("two"));
        Require((await reopened.Core.CookieManager.GetCookiesAsync(fixture.Origin)).Any(c => c.Name == "cmuxBrowser"), "Reopening a browser lost this application session's cookies.");
        await window.ClickAction("ClosePane");
        Pass("mixed-workspace-and-browser-close-during-load", passed);
    }

    private static Task<string> Evaluate(BrowserPane pane, string expression) => pane.Core.ExecuteScriptAsync(expression);
    private static Task Navigate(BrowserPane pane, string url) => Navigation(pane, () => pane.NavigateFromAddress(url));
    private static async Task Navigation(BrowserPane pane, Action action, bool success = true)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong id = 0;
        void Starting(object? _, CoreWebView2NavigationStartingEventArgs e) => id = e.NavigationId;
        void Finished(object? _, CoreWebView2NavigationCompletedEventArgs e) { if (e.NavigationId == id) done.TrySetResult(e.IsSuccess); }
        pane.Core.NavigationStarting += Starting;
        pane.Core.NavigationCompleted += Finished;
        try { action(); Require(await done.Task.WaitAsync(TimeSpan.FromSeconds(20)) == success, "Browser navigation had an unexpected outcome."); }
        finally { pane.Core.NavigationStarting -= Starting; pane.Core.NavigationCompleted -= Finished; }
    }
    private static async Task Click(BrowserPane pane, string selector)
    {
        pane.FocusPane();
        var position = await pane.Core.ExecuteScriptAsync("(()=>{const r=document.querySelector(" + JsonSerializer.Serialize(selector) + ").getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()");
        using var json = JsonDocument.Parse(position);
        var x = json.RootElement.GetProperty("x").GetDouble();
        var y = json.RootElement.GetProperty("y").GetDouble();
        await pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x, y }));
        foreach (var type in new[] { "mousePressed", "mouseReleased" })
            await pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = "left", buttons = type == "mousePressed" ? 1 : 0, clickCount = 1 }));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string name, List<string> passed) { passed.Add(name); Console.WriteLine("passed: " + name); }
}

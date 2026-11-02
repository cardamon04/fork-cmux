using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cmux.Desktop;

internal static class TerminalLinkScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed)
    {
        await StartupSelection(source, passed);
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TerminalLinks.Parser.cjs"));
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Assets"));
        using (var parser = Process.Start(start)!)
        {
            _ = parser.SafeHandle;
            var stdout = parser.StandardOutput.ReadToEndAsync();
            var stderr = parser.StandardError.ReadToEndAsync();
            try
            {
                await parser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(parser.ExitCode == 0, "The production terminal link contract failed: " + await stderr);
                Console.WriteLine("TERMINAL_LINKS_PARSER " + await stdout);
            }
            finally { if (!parser.HasExited) { parser.Kill(); await parser.WaitForExitAsync(); } }
        }
        Pass(passed, "terminal-links-pinned-addon-and-activation-contract");
        foreach (var address in new[] { "http://localhost:8080/path?q=1#part", "https://example.invalid/日本語", "HTTPS://example.invalid/" })
            Require(TerminalLink.TryAddress(address, out var normalized) && SessionSnapshot.IsBrowserAddress(normalized), "A valid HTTP link was rejected.");
        var invalid = new[] { "", "about:blank", "file:///C:/test", "javascript:alert(1)", "data:text/html,test",
            "cmux://test", "example.invalid", "//example.invalid", "https://user:password@example.invalid/",
            "https://example.invalid/\nnext", " https://example.invalid/", "https://example.invalid/" + new string('x', 32768) };
        foreach (var address in invalid) Require(!TerminalLink.TryAddress(address, out _), "An unsafe terminal URL was accepted.");
        Pass(passed, "terminal-links-native-address-policy");

        var root = Directory.CreateTempSubdirectory("cmux-terminal-links-").FullName;
        using var window = new DesktopWindow(source with { Directory = root }, new MemoryClipboard()) { ShowInTaskbar = false };
        await using var fixture = new BrowserFixture();
        Process? process = null;
        string? profile = null, browserProfile = null;
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = window.ProfilePath;
            var pane = (TerminalPane)window.Current!.Active!;
            process = Process.GetProcessById(pane.ProcessId);
            _ = process.SafeHandle;
            await Program.Contains(pane, "CMUX_TEST_READY>");
            var firstOwner = window.Current;
            var url = fixture.Url("plain-terminal-link");
            await Output(pane, "\r\n" + url + "\r\n");
            var point = await Hover(pane, url, expectHint: true);
            foreach (var modifiers in new[] { 0, 1, 4, 8, 3, 6, 10 })
            {
                await Click(pane, point, modifiers);
                await Barrier(pane);
                Require(firstOwner.Panes.Count == 1 && fixture.Requests("plain-terminal-link") == 0, "A plain or unsupported modifier click opened a link.");
            }
            // Synthetic DOM input does not carry a user gesture.
            await pane.Core.ExecuteScriptAsync("links.handler.activate(new MouseEvent('click',{button:0,ctrlKey:true})," + JsonSerializer.Serialize(url) + ")");
            await Barrier(pane);
            Require(firstOwner.Panes.Count == 1, "An untrusted script activation opened a browser.");
            Pass(passed, "terminal-links-explicit-trusted-ctrl-click-only");

            await Click(pane, await Hover(pane, url, expectHint: true), 2);
            await fixture.Requested("plain-terminal-link").WaitAsync(TimeSpan.FromSeconds(30));
            await window.PendingAction;
            var browser = (BrowserPane)firstOwner.Active!;
            browserProfile = window.BrowserProfilePath;
            await Program.Until(() => Task.FromResult(browser.Core.Source == url), "The plain link did not navigate the browser pane.");
            Require(firstOwner.Panes.Count == 2 && pane.ProcessId == process.Id && pane.Core.Source == TerminalPane.Page,
                "Link navigation replaced the terminal or opened multiple panes.");
            Require(browser.Core.Environment.UserDataFolder != pane.Core.Environment.UserDataFolder && !browser.Core.Settings.IsWebMessageEnabled,
                "A terminal link bypassed the isolated browser boundary.");
            await window.ClickAction("ClosePane");
            pane.FocusTerminal();
            Pass(passed, "terminal-links-plain-url-opens-isolated-browser-beside-live-shell");

            var oscUrl = fixture.Url("osc8-terminal-link");
            const string label = "OSC8_リンク_😀";
            await Output(pane, "\r\n\u001b]8;;" + oscUrl + "\u001b\\" + label + "\u001b]8;;\u001b\\\r\n");
            await Click(pane, await Hover(pane, label, expectHint: true), 2);
            await fixture.Requested("osc8-terminal-link").WaitAsync(TimeSpan.FromSeconds(30));
            await window.PendingAction;
            Require(firstOwner.Active is BrowserPane oscBrowser && oscBrowser.SessionAddress == oscUrl && firstOwner.Panes.Count == 2,
                "An OSC 8 label did not use the same browser split action.");
            await window.ClickAction("ClosePane");
            pane.FocusTerminal();
            Pass(passed, "terminal-links-osc8-unicode-label-shared-navigation");

            // Test the native boundary directly, independently of renderer filtering.
            foreach (var address in invalid)
                await pane.Core.ExecuteScriptAsync("window.chrome.webview.postMessage(" + JsonSerializer.Serialize(new { kind = "openLink", data = address }) + ")");
            await Barrier(pane);
            Require(firstOwner.Panes.Count == 1 && window.LastError is null, "An invalid native link request changed the layout or raised an error.");
            const string unsafeLabel = "UNSAFE_OSC8_LABEL";
            await Output(pane, "\r\n\u001b]8;;https://user:password@example.invalid/\u001b\\" + unsafeLabel + "\u001b]8;;\u001b\\\r\n");
            await Click(pane, await Hover(pane, unsafeLabel, expectHint: false), 2);
            await Barrier(pane);
            Require(firstOwner.Panes.Count == 1 && pane.Core.Source == TerminalPane.Page, "An unsafe OSC 8 link escaped its terminal page.");
            Pass(passed, "terminal-links-renderer-and-native-reject-unsafe-addresses");

            await window.ClickAction("NewWorkspace");
            var secondOwner = window.Current!;
            var other = (TerminalPane)secondOwner.Active!;
            using var otherProcess = Process.GetProcessById(other.ProcessId);
            _ = otherProcess.SafeHandle;
            await Program.Contains(other, "CMUX_TEST_READY>");
            await pane.Core.ExecuteScriptAsync("window.chrome.webview.postMessage(" + JsonSerializer.Serialize(new { kind = "openLink", data = fixture.Url("stale-link") }) + ")");
            await Barrier(pane);
            Require(firstOwner.Panes.Count == 1 && secondOwner.Panes.Count == 1 && fixture.Requests("stale-link") == 0,
                "A hidden source opened a browser in another workspace.");
            await window.ClickAction("CloseWorkspace");
            Require(otherProcess.WaitForExit(0), "The fixture's second shell survived workspace close.");
            pane.FocusTerminal();
            await window.ClickAction("SplitRight");
            var neighbor = (TerminalPane)window.Current!.Active!;
            using var neighborProcess = Process.GetProcessById(neighbor.ProcessId);
            _ = neighborProcess.SafeHandle;
            await Program.Contains(neighbor, "CMUX_TEST_READY>");
            pane.FocusTerminal();
            await window.ClickAction("MovePaneToNewWorkspace");
            var movedOwner = window.Current!;
            Require(movedOwner != firstOwner && movedOwner.Active == pane, "The live-source move precondition failed.");
            var movedUrl = fixture.Url("moved-terminal-link");
            await Output(pane, "\r\n" + movedUrl + "\r\n");
            await Click(pane, await Hover(pane, movedUrl, expectHint: true), 2);
            await fixture.Requested("moved-terminal-link").WaitAsync(TimeSpan.FromSeconds(30));
            await window.PendingAction;
            Require(movedOwner.Panes.Count == 2 && firstOwner.Panes.Count == 1 && firstOwner.Active == neighbor
                && pane.ProcessId == process.Id && movedOwner.Active is BrowserPane,
                "A moved terminal link used its former workspace or replaced a shell.");
            Pass(passed, "terminal-links-hidden-source-rejected-and-live-moved-owner-resolved");
            Console.WriteLine("TERMINAL_LINKS " + JsonSerializer.Serialize(new { plainRequests = fixture.Requests("plain-terminal-link"),
                osc8Requests = fixture.Requests("osc8-terminal-link"), movedRequests = fixture.Requests("moved-terminal-link"),
                staleRequests = fixture.Requests("stale-link"), preservedShell = pane.ProcessId == process.Id }));
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            Require(neighborProcess.WaitForExit(0), "The neighboring fixture shell survived shutdown.");
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            if (process is not null) { Require(process.WaitForExit(0), "The original link fixture shell survived shutdown."); process.Dispose(); }
            if (profile is not null) Require(!Directory.Exists(profile), "The link fixture terminal profile survived shutdown.");
            if (browserProfile is not null) Require(!Directory.Exists(browserProfile), "The link fixture browser profile survived shutdown.");
            var full = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                && Path.GetFileName(full).StartsWith("cmux-terminal-links-", StringComparison.Ordinal), "Unexpected link fixture cleanup path.");
            Directory.Delete(full, recursive: true);
        }
        Pass(passed, "terminal-links-owned-shell-and-profile-cleanup");
    }

    internal static async Task StartupSelection(ShellLaunch source, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-link-selection-").FullName;
        using var window = new DesktopWindow(source with { Directory = root }, new MemoryClipboard()) { ShowInTaskbar = false };
        await using var fixture = new BrowserFixture();
        Process? process = null;
        string? profile = null, browserProfile = null;
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = window.ProfilePath;
            var pane = (TerminalPane)window.Current!.Active!;
            process = Process.GetProcessById(pane.ProcessId);
            _ = process.SafeHandle;
            await Program.Contains(pane, "CMUX_TEST_READY>");
            var owner = window.Current;
            var url = fixture.Url("newer-selection-link");
            await Output(pane, "\r\n" + url + "\r\n");
            var point = await Hover(pane, url, expectHint: true);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = false;
            var reselected = false;
            void Reselect(string address)
            {
                pending = !window.PendingAction.IsCompleted && owner.Active is BrowserPane && owner.Panes.Count == 2;
                // Same native pane-focus action as selecting the source while
                // the new browser profile/controller initialization is pending.
                pane.FocusPane();
                reselected = owner.Active == pane;
                received.TrySetResult();
            }
            pane.LinkRequested += Reselect;
            try { await Click(pane, point, 2); await received.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { pane.LinkRequested -= Reselect; }
            Require(pending && reselected, "The link selection regression did not exercise a newer source selection during pending creation.");
            await window.PendingAction.WaitAsync(TimeSpan.FromSeconds(30));
            await fixture.Requested("newer-selection-link").WaitAsync(TimeSpan.FromSeconds(30));
            browserProfile = window.BrowserProfilePath;
            var browser = owner.Panes.OfType<BrowserPane>().Single();
            Console.WriteLine("TERMINAL_LINK_SELECTION " + JsonSerializer.Serialize(new { pending, reselected,
                sourceRemainsActive = owner.Active == pane, browser.SessionAddress, requested = fixture.Requests("newer-selection-link") }));
            Require(owner.Active == pane && window.Current == owner, "Link browser navigation overrode a newer terminal selection.");
            Require(browser.SessionAddress == url && fixture.Requests("newer-selection-link") == 1 && window.LastError is null,
                "Preserving source selection lost or duplicated link navigation.");
            Pass(passed, "terminal-links-startup-preserves-newer-selection");
        }
        finally
        {
            browserProfile ??= window.BrowserProfilePath;
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            if (process is not null) { Require(process.WaitForExit(0), "The selection fixture shell survived shutdown."); process.Dispose(); }
            if (profile is not null) Require(!Directory.Exists(profile), "The selection terminal profile survived shutdown.");
            if (browserProfile is not null) Require(!Directory.Exists(browserProfile), "The selection browser profile survived shutdown.");
            var full = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                && Path.GetFileName(full).StartsWith("cmux-link-selection-", StringComparison.Ordinal), "Unexpected selection cleanup path.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static async Task Output(TerminalPane pane, string text)
    {
        await pane.Core.ExecuteScriptAsync("""
            if (!window.cmuxLinkOutput) {
              window.cmuxLinkOutput = [];
              window.chrome.webview.addEventListener('message', event => {
                if (event.data.kind !== 'output') return;
                window.cmuxLinkOutput.push(event.data.data.slice(-8192));
                if (window.cmuxLinkOutput.length > 32) window.cmuxLinkOutput.shift();
              });
            }
            """);
        var suffix = Guid.NewGuid().ToString("N");
        // Use the PowerShell host's Unicode writer. Console.Write in the
        // owned console's legacy encoding replaces this fixture's label
        // before ConPTY/xterm receives it; changing that encoding is unneeded.
        await Program.Command(pane, "Microsoft.PowerShell.Utility\\Write-Host -NoNewline ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "'))); [Console]::WriteLine(('LINK_OUTPUT_' + '" + suffix + "'))");
        await Program.Contains(pane, "LINK_OUTPUT_" + suffix);
    }

    private static async Task<(double X, double Y)> Hover(TerminalPane pane, string label, bool expectHint)
    {
        try { await Program.Contains(pane, label); }
        catch
        {
            Console.WriteLine("TERMINAL_LINK_MISSING_OUTPUT " + await pane.Core.ExecuteScriptAsync("JSON.stringify({text:window.cmux.text(),output:window.cmuxLinkOutput})"));
            throw;
        }
        await pane.Core.ExecuteScriptAsync("term.scrollToBottom()");
        using var json = JsonDocument.Parse(await pane.Core.ExecuteScriptAsync("""
            (() => {
              const label = LABEL, b = term.buffer.active;
              let row = -1;
              for (let y = b.viewportY; y < b.viewportY + term.rows; y++)
                if (b.getLine(y)?.translateToString(true) === label) row = y - b.viewportY;
              const r = term.element.querySelector('.xterm-screen').getBoundingClientRect();
              return {row, x:r.x + r.width / term.cols * 2.5, otherX:r.x + r.width / term.cols * 4.5, y:r.y + r.height / term.rows * (row + .5)};
            })()
            """.Replace("LABEL", JsonSerializer.Serialize(label), StringComparison.Ordinal)));
        var p = json.RootElement;
        Require(p.GetProperty("row").GetInt32() >= 0, "The actual link label is not visible in the renderer.");
        var point = (X: p.GetProperty("x").GetDouble(), Y: p.GetProperty("y").GetDouble());
        // Leave a previously active link, then await the real linkifier hover callback.
        await Mouse(pane, "mouseMoved", (1, 1), 0, 0);
        // xterm caches the last buffer cell across mouseleave. Enter a
        // different real cell before returning to the target cell.
        await Mouse(pane, "mouseMoved", (p.GetProperty("otherX").GetDouble(), point.Y), 0, 0);
        await Mouse(pane, "mouseMoved", point, 0, 0);
        if (expectHint) await Program.Until(async () => JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("term.element.title")) == new Strings()["OpenLinkHint"], "The real link provider did not activate its localized hover hint.");
        return point;
    }

    private static async Task Click(TerminalPane pane, (double X, double Y) point, int modifiers)
    {
        await Mouse(pane, "mousePressed", point, modifiers, 1);
        await Mouse(pane, "mouseReleased", point, modifiers, 0);
    }

    private static Task Mouse(TerminalPane pane, string type, (double X, double Y) point, int modifiers, int buttons) =>
        pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = point.X, y = point.Y,
            modifiers, button = type == "mouseMoved" ? "none" : "left", buttons, clickCount = type == "mouseMoved" ? 0 : 1 }));

    private static async Task Barrier(TerminalPane pane)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Bell() => received.TrySetResult();
        pane.Bell += Bell;
        try { await pane.Core.ExecuteScriptAsync("window.chrome.webview.postMessage({kind:'bell'})"); await received.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { pane.Bell -= Bell; }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pass(List<string> passed, string name) { passed.Add(name); Console.WriteLine("passed: " + name); }

}

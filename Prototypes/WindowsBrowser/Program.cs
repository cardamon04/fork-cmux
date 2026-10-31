using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WindowsBrowser;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length is < 1 or > 2 || args[0] != "--self-test")
        {
            Console.Error.WriteLine("dotnet run --project Prototypes/WindowsBrowser -- --self-test [test-name]");
            return 2;
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using var context = new ApplicationContext();
        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var exit = 1;
        dispatcher.BeginInvoke(async () =>
        {
            try { exit = await Run(args.Length == 2 ? args[1] : null); }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { context.ExitThread(); }
        });
        Application.Run(context);
        return exit;
    }

    private static async Task<int> Run(string? filter)
    {
        using var window = new HiddenBrowserWindow();
        var tests = new List<(string Name, Func<nint, Task> Run)>
        {
            ("navigation-history-and-eval", Navigation),
            ("unicode-input-and-trusted-click", Input),
            ("accessibility-tree", Accessibility),
            ("console-and-network-events", Events),
            ("isolated-profiles-and-cleanup", Profiles),
            ("rejected-navigation-and-script-errors", Errors),
        };
        if (filter is not null) tests = tests.Where(test => test.Name == filter).ToList();
        if (tests.Count == 0)
        {
            Console.Error.WriteLine("No test matched the requested name.");
            return 2;
        }
        var outcomes = new List<object>();
        var failed = false;
        foreach (var test in tests)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await test.Run(window.Handle);
                Require(!window.IsVisible, "The engine probe displayed a desktop window.");
                outcomes.Add(new { test = test.Name, status = "passed", milliseconds = watch.ElapsedMilliseconds });
                Console.Error.WriteLine("passed: " + test.Name);
            }
            catch (Exception error)
            {
                failed = true;
                outcomes.Add(new { test = test.Name, status = "failed", milliseconds = watch.ElapsedMilliseconds, error = error.ToString() });
                Console.Error.WriteLine("failed: " + test.Name);
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            runtime = Environment.Version.ToString(), webview2 = browserVersion,
            scope = "isolated-engine-probe; product-browser-driver-not-implemented", outcomes,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failed ? 1 : 0;
    }

    private static string? browserVersion;
    private static async Task<BrowserProbeSession> Create(nint parent)
    {
        var session = await BrowserProbeSession.Create(parent);
        browserVersion = session.RuntimeVersion;
        return session;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Navigation(nint parent)
    {
        await using var browser = await Create(parent);
        await browser.Navigate(BrowserProbeSession.Origin + "/one");
        Require((await browser.Evaluate("location.pathname")).GetString() == "/one", "Initial navigation did not commit.");
        await browser.Navigate(BrowserProbeSession.Origin + "/two");
        Require((await browser.Evaluate("location.pathname")).GetString() == "/two", "Second navigation did not commit.");
        await browser.Back();
        Require((await browser.Evaluate("location.pathname")).GetString() == "/one", "Back did not restore the previous page.");
        await browser.Evaluate("window.probe = 'set'");
        await browser.Reload();
        Require((await browser.Evaluate("typeof window.probe")).GetString() == "undefined", "Reload did not replace the document.");
        Require((await browser.Evaluate("'日本語🙂' + ':' + (6 * 7)")).GetString() == "日本語🙂:42", "JavaScript or Unicode result was lost.");
    }

    private static async Task Input(nint parent)
    {
        await using var browser = await Create(parent);
        await browser.Navigate(BrowserProbeSession.Origin + "/input");
        await browser.Fill("#name", "日本語🙂");
        await browser.Click("#apply");
        var actual = await browser.Evaluate("({value:document.querySelector('#result').textContent,input:document.querySelector('#name').value,focused:document.hasFocus(),clicks:window.clicks,trusted:window.trustedClick})");
        Require(actual.GetProperty("value").GetString() == "日本語🙂", "Input did not reach the page: " + actual.GetRawText());
        Require(actual.GetProperty("clicks").GetInt32() == 1 && actual.GetProperty("trusted").GetBoolean(), "CDP did not dispatch a trusted mouse click.");
    }

    private static async Task Accessibility(nint parent)
    {
        await using var browser = await Create(parent);
        await browser.Navigate(BrowserProbeSession.Origin + "/accessibility");
        await browser.Call("Accessibility.enable", new { });
        var snapshot = await browser.Call("Accessibility.getFullAXTree", new { });
        var nodes = snapshot.GetProperty("nodes").EnumerateArray().ToArray();
        static bool Match(JsonElement node, string role, string name) => node.TryGetProperty("role", out var r)
            && r.GetProperty("value").GetString() == role && node.TryGetProperty("name", out var n) && n.GetProperty("value").GetString() == name;
        Require(nodes.Any(node => Match(node, "button", "適用")), "Button is missing from the accessibility tree.");
        Require(nodes.Any(node => Match(node, "textbox", "名前")), "Named input is missing from the accessibility tree.");
    }

    private static async Task Events(nint parent)
    {
        await using var browser = await Create(parent);
        await browser.Navigate(BrowserProbeSession.Origin + "/events");
        using var console = new DevToolsEventWaiter(browser.Core, "Runtime.consoleAPICalled", data =>
            data.GetProperty("args").EnumerateArray().Any(arg => arg.TryGetProperty("value", out var value) && value.GetString() == "probe-console"));
        using var network = new DevToolsEventWaiter(browser.Core, "Network.responseReceived", data =>
            data.GetProperty("response").GetProperty("url").GetString() == BrowserProbeSession.Origin + "/data");
        await browser.Call("Runtime.enable", new { });
        await browser.Call("Network.enable", new { });
        var actual = await browser.Evaluate("console.log('probe-console'); fetch('/data').then(response => response.json()).then(data => data.message)");
        Require(actual.GetString() == "日本語🙂", "Fixture network response was lost.");
        await console.Wait();
        var response = await network.Wait();
        Require(response.GetProperty("response").GetProperty("status").GetInt32() == 200, "Network status was not observed.");
    }

    private static async Task Profiles(nint parent)
    {
        await using var first = await Create(parent);
        await first.Navigate(BrowserProbeSession.Origin + "/profile");
        await first.Evaluate("localStorage.setItem('probe-key','first'); document.cookie='probe-cookie=first; SameSite=Strict'; true");
        await using var second = await Create(parent);
        await second.Navigate(BrowserProbeSession.Origin + "/profile");
        var state = await second.Evaluate("({value:localStorage.getItem('probe-key'),cookie:document.cookie})");
        Require(state.GetProperty("value").ValueKind == JsonValueKind.Null && state.GetProperty("cookie").GetString() == "", "Isolated profiles shared storage.");
        Require((await first.Evaluate("localStorage.getItem('probe-key')")).GetString() == "first", "Creating a second profile changed the first.");
        var firstPath = first.ProfilePath;
        var secondPath = second.ProfilePath;
        await first.DisposeAsync();
        await second.DisposeAsync();
        Require(!Directory.Exists(firstPath) && !Directory.Exists(secondPath), "Closed profiles were not removed after browser exit.");
    }

    private static async Task Errors(nint parent)
    {
        await using var browser = await Create(parent);
        await browser.Navigate(BrowserProbeSession.Origin + "/allowed");
        try { await browser.Navigate("https://example.invalid/"); throw new Exception("An external origin was accepted."); }
        catch (ArgumentException) { }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await browser.Navigate(BrowserProbeSession.Origin + "/cancelled", cancellation.Token); throw new Exception("A cancelled navigation started."); }
        catch (OperationCanceledException) { }
        Require((await browser.Evaluate("location.pathname")).GetString() == "/allowed", "Rejected navigation changed the document.");
        try { await browser.Evaluate("throw new Error('intentional-probe-error')"); throw new Exception("A script failure was reported as success."); }
        catch (InvalidOperationException error) when (error.Message.Contains("intentional-probe-error")) { }
        await browser.DisposeAsync();
        try { await browser.Evaluate("1"); throw new Exception("A closed browser accepted a call."); }
        catch (ObjectDisposedException) { }
    }
}

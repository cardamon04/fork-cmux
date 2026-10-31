using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace WindowsBrowser;

// This is an isolated engine probe, not a remotely callable browser service.
// All methods and disposal are owned by one STA thread; only the fixture origin
// can navigate. The production driver's frame, ownership and policy gates are
// not implemented here and must not be inferred from a successful engine test.
internal sealed class BrowserProbeSession : IAsyncDisposable
{
    internal const string Origin = "https://cmux-probe.invalid";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private readonly string profile;
    private CoreWebView2Environment? environment;
    private CoreWebView2Controller? controller;
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private bool disposed;
    private bool navigating;

    private BrowserProbeSession(string profile) => this.profile = profile;
    internal CoreWebView2 Core => controller?.CoreWebView2 ?? throw new ObjectDisposedException(nameof(BrowserProbeSession));
    internal string ProfilePath => profile;
    internal string RuntimeVersion => environment!.BrowserVersionString;

    internal static async Task<BrowserProbeSession> Create(nint parent)
    {
        var profile = Directory.CreateTempSubdirectory("cmux-webview-").FullName;
        var session = new BrowserProbeSession(profile);
        try
        {
            session.environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            session.environment.BrowserProcessExited += session.BrowserExited;
            session.controller = await session.environment.CreateCoreWebView2ControllerAsync(parent);
            session.controller.Bounds = new Rectangle(0, 0, 800, 600);
            // The controller needs layout/input enabled. Its parent HWND lacks
            // WS_VISIBLE, so this does not display or activate a desktop window.
            session.controller.IsVisible = true;
            session.Core.Settings.AreDefaultScriptDialogsEnabled = false;
            session.Core.Settings.IsStatusBarEnabled = false;
            session.Core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            session.Core.NewWindowRequested += (_, args) => args.Handled = true;
            session.Core.NavigationStarting += session.CheckNavigation;
            session.Core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
            session.Core.WebResourceRequested += session.ServeFixture;
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private void BrowserExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs args) => exited.TrySetResult();

    private void CheckNavigation(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsFixtureUrl(args.Uri)) args.Cancel = true;
    }

    internal static bool IsFixtureUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "cmux-probe.invalid" && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);

    private void ServeFixture(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var uri = new Uri(args.Request.Uri);
        var isData = uri.AbsolutePath == "/data";
        var content = isData ? "{\"message\":\"日本語🙂\"}" : FixtureHtml(uri.AbsolutePath);
        // WebView2 takes a COM stream reference for the response; retain this
        // managed stream through the response's lifetime rather than disposing
        // it before the browser has consumed its contents.
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        args.Response = environment!.CreateWebResourceResponse(stream, 200, "OK",
            "Content-Type: " + (isData ? "application/json" : "text/html") + "; charset=utf-8\r\nCache-Control: no-store");
    }

    private static string FixtureHtml(string path) => """
        <!doctype html><html lang="ja"><meta charset="utf-8"><title>Browser probe</title>
        <style>body{font:18px sans-serif}input,button{display:block;margin:20px;padding:12px}</style>
        <label for="name">名前</label><input id="name" aria-label="名前">
        <button id="apply">適用</button><output id="result"></output>
        <script>
        window.clicks = 0; window.trustedClick = false;
        document.querySelector('#apply').addEventListener('click', event => {
          window.clicks++; window.trustedClick = event.isTrusted;
          document.querySelector('#result').textContent = document.querySelector('#name').value;
        });
        </script></html>
        """;

    internal async Task Navigate(string url, CancellationToken cancellation = default)
    {
        AssertOwner();
        if (!IsFixtureUrl(url)) throw new ArgumentException("Only the isolated fixture origin is allowed.", nameof(url));
        await Navigation(() => Core.Navigate(url), cancellation);
    }

    internal async Task Back(CancellationToken cancellation = default)
    {
        AssertOwner();
        if (!Core.CanGoBack) throw new InvalidOperationException("No previous fixture page.");
        await Navigation(Core.GoBack, cancellation);
    }

    internal async Task Reload(CancellationToken cancellation = default) => await Navigation(Core.Reload, cancellation);

    private async Task Navigation(Action start, CancellationToken cancellation)
    {
        AssertOwner();
        cancellation.ThrowIfCancellationRequested();
        if (navigating) throw new InvalidOperationException("Only one navigation may be in flight.");
        navigating = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs args) => navigationId ??= args.NavigationId;
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId != navigationId) return;
            if (args.IsSuccess) completion.TrySetResult();
            else completion.TrySetException(new InvalidOperationException("Navigation failed: " + args.WebErrorStatus));
        }
        Core.NavigationStarting += Starting;
        Core.NavigationCompleted += Completed;
        try
        {
            start();
            await completion.Task.WaitAsync(Deadline, cancellation);
        }
        catch
        {
            Core.Stop();
            throw;
        }
        finally
        {
            Core.NavigationStarting -= Starting;
            Core.NavigationCompleted -= Completed;
            navigating = false;
        }
    }

    internal async Task<JsonElement> Call(string method, object parameters)
    {
        AssertOwner();
        using var result = JsonDocument.Parse(await Core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters)).WaitAsync(Deadline));
        return result.RootElement.Clone();
    }

    internal async Task<JsonElement> Evaluate(string expression)
    {
        var response = await Call("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true });
        if (response.TryGetProperty("exceptionDetails", out var error))
            throw new InvalidOperationException("JavaScript failed: " + error.GetRawText());
        return response.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : default;
    }

    internal async Task Click(string selector)
    {
        var quoted = JsonSerializer.Serialize(selector);
        var point = await Evaluate("(() => {const e=document.querySelector(" + quoted
            + "); if(!e) throw new Error('missing element'); const r=e.getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+r.height/2};})()");
        var x = point.GetProperty("x").GetDouble();
        var y = point.GetProperty("y").GetDouble();
        await Call("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y, button = "none" });
        await Call("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 });
        await Call("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 });
    }

    internal async Task Fill(string selector, string text)
    {
        var quoted = JsonSerializer.Serialize(selector);
        await Evaluate("(() => {const e=document.querySelector(" + quoted + "); if(!e) throw new Error('missing element'); e.focus(); e.select(); return true;})()");
        await Call("Input.insertText", new { text });
    }

    private void AssertOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("WebView2 requires its owning STA thread.");
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        AssertOwner();
        disposed = true;
        var hadBrowser = controller is not null || (environment?.GetProcessInfos().Count > 0);
        controller?.Close();
        controller = null;
        if (hadBrowser) await exited.Task.WaitAsync(Deadline);
        if (environment is not null) environment.BrowserProcessExited -= BrowserExited;
        environment = null;
        var expectedRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(profile);
        if (!resolved.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("cmux-webview-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a profile outside this probe's temporary directory.");
        Directory.Delete(resolved, recursive: true);
    }
}

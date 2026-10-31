using Microsoft.Web.WebView2.Core;

namespace Cmux.Desktop;

internal sealed class WebViewProfile : IAsyncDisposable
{
    private readonly string profile;
    private TaskCompletionSource browserExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint browserProcessId;
    internal CoreWebView2Environment Core { get; }
    internal string Profile => profile;
    private WebViewProfile(string profile, CoreWebView2Environment core)
    {
        this.profile = profile;
        Core = core;
        core.BrowserProcessExited += (_, e) =>
        {
            if (e.BrowserProcessId == browserProcessId) browserExit.TrySetResult();
        };
    }

    internal async Task<CoreWebView2Controller> CreateController(nint parent)
    {
        var controller = await Core.CreateCoreWebView2ControllerAsync(parent);
        var processId = controller.CoreWebView2.BrowserProcessId;
        if (processId != browserProcessId || browserExit.Task.IsCompleted)
        {
            browserProcessId = processId;
            browserExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return controller;
    }

    internal static async Task<WebViewProfile> Create(Strings? strings = null, string prefix = "cmux-desktop-")
    {
        strings ??= new Strings();
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER")))
            throw new InvalidOperationException(strings["ProfileOverride"]);
        var profile = Directory.CreateTempSubdirectory(prefix).FullName;
        try
        {
            var core = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(core.UserDataFolder)),
                    Path.TrimEndingDirectorySeparator(profile), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(strings["ProfileRedirected"]);
            return new(profile, core);
        }
        catch
        {
            // No controller has been created. Only remove our newly owned folder.
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // All controllers must have closed before this owner is disposed.
        if (browserProcessId != 0) await browserExit.Task.WaitAsync(TimeSpan.FromSeconds(15));
        for (var attempt = 0; ; attempt++)
        {
            try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); break; }
            catch (IOException) when (attempt < 20) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) when (attempt < 20) { await Task.Delay(100); }
        }
    }
}

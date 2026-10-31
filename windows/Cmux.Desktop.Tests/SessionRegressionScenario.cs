using System.Text.Json;
using Cmux.Desktop;

internal static class SessionRegressionScenario
{
    internal static async Task Run(ShellLaunch launch, List<string> passed, string? only = null)
    {
        if (only is not (null or "startup" or "url")) throw new ArgumentException("Unknown session regression case.");
        var directory = Directory.CreateTempSubdirectory("cmux-session-regression-").FullName;
        try
        {
            if (only is null or "startup") await StartupFailure(launch, directory, passed);
            if (only is null or "url") await PendingBrowser(launch, directory, passed);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task StartupFailure(ShellLaunch launch, string directory, List<string> passed)
    {
        var path = Path.Combine(directory, "startup.json");
        Seed(path, new(1, 7, 0, [new("Saved workspace", 0, new("terminal", Directory: directory))]));
        var before = File.ReadAllBytes(path);
        var refusedProfile = Path.Combine(directory, "must-not-open-profile");
        var previous = Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER");
        using var window = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        try
        {
            try
            {
                Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", refusedProfile);
                window.Show();
                try { await window.Started.WaitAsync(TimeSpan.FromSeconds(30)); throw new Exception("Startup did not reach the intended profile refusal."); }
                catch (InvalidOperationException error) when (error.Message == new Strings()["ProfileOverride"]) { }
            }
            finally { Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", previous); }
            Require(!window.Workspaces.Any() && !Directory.Exists(refusedProfile), "The startup failure fixture allocated work or opened the refused profile.");
            var closed = Closed(window);
            window.Close();
            await closed.WaitAsync(TimeSpan.FromSeconds(30));
            Require(before.SequenceEqual(File.ReadAllBytes(path)), "Closing after failed initialization overwrote the previous saved layout; remaining workspaces=" + Read(path).Workspaces.Length);
            Pass("session-failed-initialization-preserves-last-save", passed);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", previous);
            if (!window.IsDisposed) { await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30)); window.Close(); }
        }
    }

    private static async Task PendingBrowser(ShellLaunch launch, string directory, List<string> passed)
    {
        await using var fixture = new BrowserFixture();
        var target = fixture.Url("slow");
        var path = Path.Combine(directory, "url.json");
        Seed(path, new(1, 1, 0, [new("Browser workspace", 0, new("browser", Url: target))]));
        using var window = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            await fixture.Requested("slow").WaitAsync(TimeSpan.FromSeconds(30));
            var browser = (BrowserPane)window.Current!.Active!;
            Require(browser.Core.Source == "about:blank" && browser.AddressText == target, "The browser was not in an uncommitted restored navigation.");
            await window.ClickAction("SaveSession");
            var checkpoint = Read(path).Workspaces[0].Layout.Url;
            var profile = window.ProfilePath;
            var browserProfile = window.BrowserProfilePath!;
            var closed = Closed(window);
            window.Close();
            await closed.WaitAsync(TimeSpan.FromSeconds(30));
            var afterClose = Read(path).Workspaces[0].Layout.Url;
            Require(!Directory.Exists(profile) && !Directory.Exists(browserProfile), "Pending browser teardown leaked owned profiles.");
            Require(checkpoint == target && afterClose == target, "Restored pending URL was replaced during successful save/close: checkpoint=" + checkpoint + ", close=" + afterClose);
            Pass("session-pending-browser-restore-keeps-saved-address", passed);
        }
        finally { if (!window.IsDisposed) { await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30)); window.Close(); } }
    }

    private static void Seed(string path, SessionSnapshot snapshot)
    {
        using var store = new SessionStore(path);
        store.Open();
        store.Save(snapshot);
    }
    private static SessionSnapshot Read(string path) => JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))!;
    private static Task Closed(Form window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.FormClosed += (_, _) => closed.TrySetResult();
        return closed.Task;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string name, List<string> passed) { passed.Add(name); Console.WriteLine("passed: " + name); }
}

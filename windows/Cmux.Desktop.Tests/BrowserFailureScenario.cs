using System.Diagnostics;
using System.Runtime.InteropServices;
using Cmux.Desktop;
using Microsoft.Web.WebView2.Core;

internal static class BrowserFailureScenario
{
    internal static async Task Run(DesktopWindow window, List<string> passed, List<Exception> uiErrors)
    {
        var failures = new List<string>();
        window.SelectWorkspace(0);
        var terminal = (TerminalPane)window.Current!.Active!;
        window.Controls.OfType<ToolStrip>().First().Focus();
        var opening = window.ClickAction("OpenBrowser");
        var browser = window.Current.Panes.OfType<BrowserPane>().Single();
        terminal.FocusPane();
        await terminal.Core.ExecuteScriptAsync("document.activeElement.blur();window.cmux.focus()");
        await Program.Until(() => Task.FromResult(window.Current.Active == terminal), "Terminal could not be selected during browser startup.");
        Require(!opening.IsCompleted, "The selection regression did not exercise pending creation.");
        await opening.WaitAsync(TimeSpan.FromSeconds(30));
        if (window.Current.Active != terminal) failures.Add("Browser completion overrode newer terminal selection.");
        else Pass("browser-startup-preserves-newer-selection", passed);

        if (window.Workspaces.Count() < 2) await window.ClickAction("NewWorkspace");
        window.SelectWorkspace(0);
        browser.FocusPane();
        await Program.Until(() => Task.FromResult(window.Current!.Active == browser), "Browser was not selected for the crash fixture.");
        var core = browser.Core;
        var browserPid = core.BrowserProcessId;
        Require(browserPid != terminal.Core.BrowserProcessId
            && core.Environment.UserDataFolder != terminal.Core.Environment.UserDataFolder,
            "Refusing to crash a browser that is not isolated from the terminal.");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.ProcessFailed += (_, e) => { if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) failed.TrySetResult(); };
        using (var ownedProcess = Process.GetProcessById((int)browserPid))
        {
            var crash = core.CallDevToolsProtocolMethodAsync("Browser.crash", "{}");
            _ = crash.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await ownedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        try
        {
            window.SelectWorkspace(1);
            window.SelectWorkspace(0);
            window.ClientSize = new(window.ClientSize.Width + 20, window.ClientSize.Height + 20);
            browser.FocusPane();
            await Task.Yield();
            if (uiErrors.Count > 0) throw new InvalidOperationException("Browser failure reached native UI dispatch: " + uiErrors[0].Message);
        }
        catch (Exception error) { failures.Add("Browser failure escaped the pane: " + error.Message); }
        await window.ClickAction("ClosePane");
        if (failures.Count == 0)
        {
            await Program.Command(terminal, "Write-Output ('CRASH_' + 'SURVIVED')");
            await Program.Contains(terminal, "CRASH_SURVIVED");
            await window.ClickAction("OpenBrowser");
            Require(window.Current!.Active is BrowserPane, "Browser could not be recreated after failure.");
            await window.ClickAction("ClosePane");
            Pass("browser-process-failure-isolated-from-terminal", passed);
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string name, List<string> passed) { passed.Add(name); Console.WriteLine("passed: " + name); }
}

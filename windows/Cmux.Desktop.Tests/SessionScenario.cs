using System.Diagnostics;
using System.Text.Json;
using Cmux.Desktop;

internal static class SessionScenario
{
    internal static async Task Run(ShellLaunch launch, List<string> passed, string? artifacts)
    {
        var directory = Directory.CreateTempSubdirectory("cmux-session-ui-").FullName;
        var originalDirectory = Directory.CreateDirectory(Path.Combine(directory, "日本語 folder 😀")).FullName;
        var fallbackDirectory = Directory.CreateDirectory(Path.Combine(directory, "fallback")).FullName;
        var path = Path.Combine(directory, "state", "session.json");
        await using var fixture = new BrowserFixture();
        using var first = new DesktopWindow(launch with { Directory = originalDirectory }, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        using var second = new DesktopWindow(launch with { Directory = fallbackDirectory }, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        var ownedProcesses = new List<int>();
        var ownedProfiles = new List<string>();
        FileStream? blocker = null;
        try
        {
            first.Show();
            await first.Started.WaitAsync(TimeSpan.FromSeconds(30));
            var terminal = (TerminalPane)first.Current!.Active!;
            await Program.Command(terminal, "Write-Output ('DO_NOT_' + 'PERSIST_OUTPUT')");
            await Program.Contains(terminal, "DO_NOT_PERSIST_OUTPUT");
            await first.ClickAction("OpenBrowser");
            var browser = (BrowserPane)first.Current.Active!;
            browser.NavigateFromAddress(fixture.Url("one"));
            await Program.Until(async () => await browser.Core.ExecuteScriptAsync("document.querySelector('#page')?.textContent") == "\"/one\"", "Initial saved page did not load.");
            terminal.FocusPane();
            await first.ClickAction("SplitBelow");
            browser.FocusPane();
            var workspace = first.Current;
            var root = (SplitContainer)workspace.Controls[0];
            var below = (SplitContainer)root.Panel1.Controls[0];
            root.SplitterDistance = (int)((root.Width - root.SplitterWidth) * .63);
            below.SplitterDistance = (int)((below.Height - below.SplitterWidth) * .37);
            var rootRatio = workspace.SplitRatio(root);
            var belowRatio = workspace.SplitRatio(below);
            await first.ClickAction("NewWorkspace");
            await first.ClickAction("SaveSession");
            var saved = File.ReadAllText(path);
            Require(!saved.Contains("DO_NOT_PERSIST_OUTPUT") && !saved.Contains("CMUX_TEST_READY") && !saved.Contains("Executable"), "Output or launch command was persisted.");
            var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(saved)!;
            Require(snapshot.Workspaces.Length == 2 && snapshot.SelectedWorkspace == 1 && snapshot.Workspaces[0].ActivePane == 2, "Saved model lost order/selection.");
            Pass("session-save-toolbar-captures-mixed-layout", passed);

            await first.ClickAction("NewWorkspace");
            blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await first.ClickAction("SaveSession");
            Require(first.LastError == new Strings()["SessionSaveFailed"] && File.ReadAllText(path) == saved, "Save error was silent or damaged the existing file.");
            var cancel = OwnedWindows.ChooseSaveFailure(first.Handle, 2);
            first.Close();
            await cancel.WaitAsync(TimeSpan.FromSeconds(30));
            await Program.Until(() => Task.FromResult(first.IsActionEnabled("NewWorkspace")), "Cancel did not return to the running app.");
            await Program.Command(terminal, "Write-Output ('CANCEL_' + 'SURVIVED')");
            await Program.Contains(terminal, "CANCEL_SURVIVED");
            Require(File.ReadAllText(path) == saved, "Cancelled close overwrote the saved layout.");
            Pass("session-failed-save-and-cancel-preserve-live-work", passed);

            Track(first, ownedProcesses, ownedProfiles);
            var retry = OwnedWindows.ChooseSaveFailure(first.Handle, 6, () => { blocker.Dispose(); blocker = null; });
            var closedFirst = Closed(first);
            first.Close();
            await retry.WaitAsync(TimeSpan.FromSeconds(30));
            await closedFirst.WaitAsync(TimeSpan.FromSeconds(30));
            Require(JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))!.Workspaces.Length == 3, "Retry did not save the new workspace before quitting.");
            VerifyGone(ownedProcesses, ownedProfiles);
            Pass("session-close-retry-saves-before-owned-teardown", passed);

            second.Show();
            await second.Started.WaitAsync(TimeSpan.FromSeconds(30));
            Require(second.Workspaces.Count() == 3 && second.Current == second.Workspaces.Last(), "Startup lost workspace order or selection.");
            var restoredTerminal = (TerminalPane)second.Current!.Active!;
            await Program.Command(restoredTerminal, "Write-Output ('RESTORED_CWD_' + (Get-Location).Path)");
            await Program.Contains(restoredTerminal, "RESTORED_CWD_" + originalDirectory);
            var hidden = second.CaptureSession().Workspaces[0].Layout;
            Require(Math.Abs(hidden.Ratio!.Value - rootRatio) < .01 && Math.Abs(hidden.First!.Ratio!.Value - belowRatio) < .01, "Hidden restored split ratios were lost before first selection.");
            second.SelectWorkspace(0);
            var restored = second.Current!;
            Require(restored.Active is BrowserPane && restored.Panes.Count == 3, "The mixed pane kinds or active pane were not restored.");
            var restoredRoot = (SplitContainer)restored.Controls[0];
            var restoredBelow = (SplitContainer)restoredRoot.Panel1.Controls[0];
            Require(restoredRoot.Orientation == Orientation.Vertical && restoredBelow.Orientation == Orientation.Horizontal, "Split orientation was not restored.");
            Require(Math.Abs(restored.SplitRatio(restoredRoot) - rootRatio) < .03 && Math.Abs(restored.SplitRatio(restoredBelow) - belowRatio) < .03, "Visible split proportions changed on restore.");
            Pass("session-startup-restores-selection-cwd-and-nested-splits", passed);
            var restoredBrowser = (BrowserPane)restored.Active!;
            await Program.Until(async () => await restoredBrowser.Core.ExecuteScriptAsync("document.querySelector('#page')?.textContent") == "\"/one\"", "Saved browser address did not reload.");
            Require(restoredBrowser.SessionAddress == fixture.Url("one") && second.BrowserProfilePath != first.BrowserProfilePath, "Browser address or private profile lifetime was wrong.");
            foreach (var pane in restored.Panes.OfType<TerminalPane>())
            {
                await Program.Command(pane, "Write-Output ('RESTORED_PANE_' + 'INTERACTIVE')");
                await Program.Contains(pane, "RESTORED_PANE_INTERACTIVE");
            }
            restoredBrowser.FocusPane();
            Pass("session-restored-terminals-remain-interactive", passed);
            if (artifacts is not null)
            {
                Directory.CreateDirectory(artifacts);
                OwnedWindows.Capture(second.Handle, Path.Combine(artifacts, "session-restored.png"));
            }
            Pass("session-browser-address-reopens-in-fresh-profile", passed);

            var beforeNo = File.ReadAllText(path);
            await second.ClickAction("NewWorkspace");
            blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Track(second, ownedProcesses, ownedProfiles);
            var no = OwnedWindows.ChooseSaveFailure(second.Handle, 7);
            var closedSecond = Closed(second);
            second.Close();
            await no.WaitAsync(TimeSpan.FromSeconds(30));
            await closedSecond.WaitAsync(TimeSpan.FromSeconds(30));
            blocker.Dispose(); blocker = null;
            Require(File.ReadAllText(path) == beforeNo, "Quit without saving replaced the previous layout.");
            VerifyGone(ownedProcesses, ownedProfiles);
            Pass("session-explicit-quit-without-saving-keeps-last-file", passed);

            var earlyPath = Path.Combine(directory, "early.json");
            using (var early = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(earlyPath)) { ShowInTaskbar = false })
            {
                var closed = Closed(early);
                var tracked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                early.Shown += async (_, _) =>
                {
                    Require(!early.Started.IsCompleted, "The early-close fixture did not reach pending startup.");
                    early.Close();
                    await early.Started;
                    Track(early, ownedProcesses, ownedProfiles);
                    tracked.TrySetResult();
                };
                early.Show();
                await tracked.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await closed.WaitAsync(TimeSpan.FromSeconds(30));
                VerifyGone(ownedProcesses, ownedProfiles);
                Require(JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(earlyPath))!.Workspaces.Length == 1 && !Directory.Exists(early.ProfilePath), "Closing during startup lost the initialized layout or leaked its profile.");
            }
            Pass("session-close-during-startup-awaits-save-and-teardown", passed);

            var future = File.ReadAllText(path).Replace("\"Version\": 1", "\"Version\": 99");
            File.WriteAllText(path, future);
            using (var guarded = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false })
            {
                guarded.Show();
                await guarded.Started.WaitAsync(TimeSpan.FromSeconds(30));
                Track(guarded, ownedProcesses, ownedProfiles);
                Require(!guarded.IsActionEnabled("SaveSession") && guarded.SessionStatus == new Strings()["SessionUnavailable"], "Unsafe saved data was not visibly protected.");
                var closed = Closed(guarded);
                guarded.Close();
                await closed.WaitAsync(TimeSpan.FromSeconds(30));
                VerifyGone(ownedProcesses, ownedProfiles);
                Require(File.ReadAllText(path) == future, "Closing a fallback window overwrote future-version data.");
            }
            Pass("session-startup-warning-protects-unsupported-snapshot", passed);
        }
        finally
        {
            blocker?.Dispose();
            foreach (var window in new[] { first, second })
                if (!window.IsDisposed) { await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30)); window.Close(); }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task Closed(Form form)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        form.FormClosed += (_, _) => closed.TrySetResult();
        return closed.Task;
    }
    private static void Track(DesktopWindow window, List<int> processes, List<string> profiles)
    {
        processes.AddRange(window.Workspaces.SelectMany(w => w.Panes).OfType<TerminalPane>().Select(t => t.ProcessId));
        profiles.Add(window.ProfilePath);
        if (window.BrowserProfilePath is { } browser) profiles.Add(browser);
    }
    private static void VerifyGone(List<int> processes, List<string> profiles)
    {
        foreach (var id in processes)
            try { using var process = Process.GetProcessById(id); Require(process.HasExited, "A saved session shell survived close."); }
            catch (ArgumentException) { }
        foreach (var profile in profiles) Require(!Directory.Exists(profile), "A saved session profile survived close.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string name, List<string> passed) { passed.Add(name); Console.WriteLine("passed: " + name); }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cmux.Desktop;
using Cmux.Terminal;

internal static class Program
{
    private static int exitCode = 1;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (GitOutputFixture.RunProcessMode(args) is { } fixtureExit) return fixtureExit;
        if (args is ["--blocked-input"])
        {
            Console.WriteLine("BLOCKED_READY");
            Thread.Sleep(TimeSpan.FromMinutes(2)); // Child fixture deliberately never consumes its input.
            return 0;
        }
        if (args is ["--packaging-and-locales"]) return PackagingAndLocales();
        if (args is ["--unicode-input-wire"])
        {
            try { UnicodeInputScenario.WireContract([]); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args is ["--session-store"])
        {
            try { SessionStoreScenario.Run([]); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        var pasteOnly = args is ["--paste-limit"];
        var renameRegression = args is ["--rename-regression"];
        var renameOnly = args.Length > 0 && args[0] == "--rename-only";
        var gitRegression = args is ["--git-regression"];
        var gitOnly = args.Length > 0 && args[0] == "--git-only";
        var gitOutput = args is ["--git-output-regression"];
        var gitRender = args is ["--git-render-regression"];
        var gitWorker = args is ["--git-worker-regression"];
        var orderRegression = args is ["--workspace-order-regression"];
        var orderOnly = args.Length > 0 && args[0] == "--workspace-order-only";
        var detachRegression = args is ["--pane-detach-regression"];
        var detachOnly = args.Length > 0 && args[0] == "--pane-detach-only";
        var closeTerminal = args is ["--pane-close-terminal-regression"];
        var closeBrowser = args is ["--pane-close-browser-regression"];
        var detachOverflow = args is ["--pane-detach-overflow-regression"];
        var asyncFocus = args is ["--pane-async-focus-regression"];
        var unicodeInput = args is ["--unicode-input-regression"];
        var unicodeF3 = args is ["--unicode-input-f3"];
        var unicodeOnly = args.Length > 0 && args[0] == "--unicode-input-only";
        var searchOnly = args.Length > 0 && args[0] == "--search-only";
        var searchClipboard = args.Length > 0 && args[0] == "--search-clipboard";
        var imeOnly = args.Length > 0 && args[0] is "--ime-only" or "--ime-enter" or "--ime-escape" or "--ime-arabic";
        if (args.ElementAtOrDefault(0) == "--ime-arabic") CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-EG");
        var cwdRegression = args is ["--cwd-regression"];
        var cwdOnly = args.Length > 0 && args[0] == "--cwd-only";
        var cwdShell = args.Length > 0 && args[0] == "--cwd-shell";
        var cwdFlood = args is ["--cwd-flood"];
        var cwdCase = args is ["--cwd-case"];
        var cwdOracle = args is ["--cwd-oracle"];
        var browserOnly = args.Length > 0 && args[0] == "--browser-only";
        var browserFailures = args is ["--browser-failures"];
        var sessionOnly = args.Length > 0 && args[0] == "--session-only";
        var sessionRegressions = args.Length > 0 && args[0] == "--session-regressions";
        var uiErrors = new List<Exception>();
        Application.ThreadException += (_, e) => uiErrors.Add(e.Exception);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var directory = Directory.CreateTempSubdirectory("cmux-desktop-test-").FullName;
        var history = Path.Combine(directory, "history.txt").Replace("'", "''");
        var shell = ShellLaunch.Default() with
        {
            Arguments = ["-NoLogo", "-NoProfile", "-NoExit", "-Command", $"Set-PSReadLineOption -HistorySaveStyle SaveNothing -HistorySavePath '{history}'; function global:prompt {{ 'CMUX_TEST_READY> ' }}"],
            Directory = directory,
        };
        var clipboard = new MemoryClipboard();
        using var window = new DesktopWindow(shell, clipboard) { ShowInTaskbar = false };
        window.Shown += async (_, _) =>
        {
            var ownedProcesses = new List<Process>();
            var profile = "";
            var passed = new List<string>();
            try
            {
                await window.Started.WaitAsync(Deadline);
                profile = window.ProfilePath;
                var first = (TerminalPane)window.Current!.Active!;
                Track(ownedProcesses, first.ProcessId);
                if (unicodeInput || unicodeOnly || unicodeF3)
                {
                    if (unicodeOnly) await UnicodeInputScenario.Run(shell, passed, args.ElementAtOrDefault(1));
                    else if (unicodeF3) await UnicodeInputScenario.FunctionKeyCompatibility(shell, passed);
                    else await UnicodeInputScenario.Regression(shell, passed);
                    Require(uiErrors.Count == 0 && window.LastError is null, "Unicode input reached an unexpected native error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (asyncFocus)
                {
                    await PaneDetachScenario.AsyncFocusRegression(shell, passed, null);
                    Require(uiErrors.Count == 0 && window.LastError is null, "Async focus reached an unexpected native error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (detachOverflow)
                {
                    await PaneDetachScenario.OverflowRegression(shell, passed, null);
                    Require(uiErrors.Count == 0 && window.LastError is null, "The visible overflow action reached an unexpected UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (closeTerminal || closeBrowser)
                {
                    await PaneDetachScenario.CloseRegression(shell, closeBrowser, passed);
                    Require(uiErrors.Count == 0 && window.LastError is null, "Closing the native pane reached an unexpected UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (detachOnly || detachRegression)
                {
                    if (detachOnly) await PaneDetachScenario.Run(shell, passed, args.ElementAtOrDefault(1));
                    else await PaneDetachScenario.Regression(shell, passed);
                    Require(uiErrors.Count == 0 && window.LastError is null, "Pane detachment reached a native UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (orderOnly)
                {
                    await WorkspaceOrderScenario.Run(shell, passed, args.ElementAtOrDefault(1));
                    Require(uiErrors.Count == 0 && window.LastError is null, "Workspace ordering reached a native UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (orderRegression)
                {
                    await WorkspaceOrderScenario.Regression(shell, passed);
                    Require(uiErrors.Count == 0 && window.LastError is null, "Workspace ordering reached a native UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (gitOutput || gitRender || gitWorker)
                {
                    if (gitOutput) await GitStatusScenario.OutputBoundary(passed);
                    else if (gitWorker) await GitStatusScenario.WorkerFailure(shell, passed);
                    else await GitStatusScenario.Regression(shell, passed, literalPixels: true);
                    Require(uiErrors.Count == 0 && window.LastError is null, "Git failure handling reached a native UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (gitOnly)
                {
                    await GitStatusScenario.Run(shell, passed, args.ElementAtOrDefault(1));
                    Require(uiErrors.Count == 0 && window.LastError is null, "Git status reached a native UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (gitRegression)
                {
                    await GitStatusScenario.Regression(shell, passed);
                    Require(uiErrors.Count == 0 && window.LastError is null, "Git status reached a native UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (renameOnly)
                {
                    await WorkspaceRenameScenario.Run(shell, passed, args.ElementAtOrDefault(1));
                    Require(uiErrors.Count == 0, "Workspace rename reached native UI dispatch with an exception.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (renameRegression)
                {
                    await WorkspaceRenameScenario.Regression(window, passed);
                    Require(uiErrors.Count == 0 && window.LastError is null, "Workspace rename reached a native UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (imeOnly)
                {
                    await ImeScenario.Run(window, passed, args[0] switch { "--ime-only" => null, "--ime-arabic" => "--ime-keys", _ => args[0] }, args.ElementAtOrDefault(1));
                    if (args[0] == "--ime-only") await ImeScenario.Fallback(shell, passed, args.ElementAtOrDefault(1));
                    Require(uiErrors.Count == 0 && window.LastError is null, "IME input reached a native UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    Require(!Directory.Exists(profile), "IME fixture profile was not removed.");
                    exitCode = 0;
                    return;
                }
                if (cwdOracle)
                {
                    await WorkingDirectoryScenario.OracleBoundary(shell, passed);
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (cwdCase)
                {
                    await WorkingDirectoryScenario.ReportedCase(shell, passed);
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (cwdFlood)
                {
                    await WorkingDirectoryScenario.Flood(shell, passed);
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (cwdOnly || cwdShell)
                {
                    await WorkingDirectoryScenario.Run(shell, passed, cwdOnly && args.Length > 1 ? args[1] : null, cwdShell ? args.ElementAtOrDefault(1) ?? throw new ArgumentException("Select a CWD shell.") : null);
                    Require(uiErrors.Count == 0 && window.LastError is null, "Directory tracking reached a native UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (cwdRegression)
                {
                    await WorkingDirectoryScenario.Regression(shell, passed);
                    Require(uiErrors.Count == 0 && window.LastError is null, "Directory regression reached a native UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (searchClipboard)
                {
                    await TerminalSearchScenario.RunClipboard(window, clipboard, passed, args.Length > 1 ? args[1] : null);
                    Require(uiErrors.Count == 0 && window.LastError is null, "Search clipboard reported a native UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (searchOnly)
                {
                    await TerminalSearchScenario.Run(window, clipboard, passed, args.Length > 1 ? args[1] : null);
                    Require(uiErrors.Count == 0 && window.LastError is null, "Search reported a native UI error.");
                    await window.Shutdown().WaitAsync(Deadline);
                    Require(!Directory.Exists(profile), "Search fixture profile was not removed.");
                    exitCode = 0;
                    return;
                }
                if (sessionRegressions)
                {
                    await Until(async () => (await Text(first)).TrimEnd().EndsWith("CMUX_TEST_READY>", StringComparison.Ordinal), "The regression fixture shell did not finish startup.");
                    await SessionRegressionScenario.Run(shell, passed, args.Length > 1 ? args[1] : null);
                    Require(uiErrors.Count == 0, "A regression reached native UI dispatch with an exception.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (sessionOnly)
                {
                    SessionStoreScenario.Run(passed);
                    await SessionScenario.Run(shell, passed, args.Length > 1 ? args[1] : null);
                    await SessionRegressionScenario.Run(shell, passed);
                    Require(uiErrors.Count == 0, "Session operations reached native UI dispatch with an exception.");
                    await window.Shutdown().WaitAsync(Deadline);
                    exitCode = 0;
                    return;
                }
                if (browserFailures)
                {
                    await BrowserFailureScenario.Run(window, passed, uiErrors);
                    await window.Shutdown().WaitAsync(Deadline);
                    Require(!Directory.Exists(profile) && window.BrowserProfilePath is { } ownBrowser && !Directory.Exists(ownBrowser), "Failure fixture profiles were not removed.");
                    exitCode = 0;
                    return;
                }
                if (browserOnly)
                {
                    await window.ClickAction("NewWorkspace");
                    Track(ownedProcesses, ((TerminalPane)window.Current!.Active!).ProcessId);
                    await BrowserScenario.Run(window, passed, args.Length > 1 ? args[1] : null);
                    await window.Shutdown().WaitAsync(Deadline);
                    Require(!Directory.Exists(profile), "Application profile was not removed.");
                    Require(window.BrowserProfilePath is { } privateBrowser && !Directory.Exists(privateBrowser), "Browser profile was not removed.");
                    exitCode = 0;
                    return;
                }
                if (pasteOnly)
                {
                    await PasteLimit(window, clipboard, ownedProcesses);
                    await window.Shutdown().WaitAsync(Deadline);
                    Require(!Directory.Exists(profile), "Application profile was not removed.");
                    exitCode = 0;
                    return;
                }
                await Command(first, "Write-Output ('日本語😀' + '_ready')");
                await Contains(first, "日本語😀_ready");
                Pass("interactive-powershell-unicode", passed);

                await Command(first, "[Console]::Write(([char]27)+'[31m'+('RED_'+'MARK')+([char]27)+'[0m')");
                await Contains(first, "RED_MARK");
                var color = await first.Core.ExecuteScriptAsync("window.cmux.foreground('RED_MARK')");
                Require(color == "1", "SGR color was not rendered as red: " + color);
                Pass("vt-color-rendering", passed);

                var oldSize = await Size(first);
                await window.ClickAction("SplitRight");
                Require(window.Current.Panes.Count == 2, "Split button did not create a second terminal.");
                var second = (TerminalPane)window.Current.Active!;
                Track(ownedProcesses, second.ProcessId);
                Require(window.Current.Active == second, "Selection precondition failed.");
                await Mouse(first, "mousePressed", 80, 80, 1);
                await Mouse(first, "mouseReleased", 80, 80, 0);
                await Until(() => Task.FromResult(window.Current.Active == first), "Clicking the inactive pane did not select it.");
                await SelectText(first, "RED_MARK");
                await window.ClickAction("Copy");
                Require(clipboard.Text == "RED_MARK", "Copy did not use the selected pane and its terminal selection.");
                Pass("mouse-selection-and-copy-action", passed);
                await Until(async () => (await Size(first)).Cols < oldSize.Cols, "Original terminal did not resize after split.");
                var size = await Size(first);
                await Command(first, "Write-Output ('SIZE_' + [Console]::WindowWidth + ':' + [Console]::WindowHeight)");
                await Contains(first, $"SIZE_{size.Cols}:{size.Rows}");
                await Command(second, "Write-Output ('SECOND_' + 'PANE')");
                await Contains(second, "SECOND_PANE");
                Require(!(await Text(second)).Contains("日本語😀_ready"), "Terminal outputs were mixed.");
                await second.Core.ExecuteScriptAsync("window.cmux.focus()");
                await Until(() => Task.FromResult(window.Current.Active == second), "Focus did not select the second pane.");
                var secondProcess = ownedProcesses.Single(process => process.Id == second.ProcessId);
                await window.ClickAction("ClosePane");
                await secondProcess.WaitForExitAsync().WaitAsync(Deadline);
                Require(window.Current.Panes.Count == 1, "Closing a split did not collapse it.");
                await Contains(first, "日本語😀_ready");
                Pass("split-resize-isolation-close", passed);

                await window.ClickAction("SplitBelow");
                var below = (TerminalPane)window.Current.Active!;
                Track(ownedProcesses, below.ProcessId);
                Require(window.Current.Controls[0] is SplitContainer { Orientation: Orientation.Horizontal }, "Split below used the wrong layout.");
                await Command(below, "Write-Output ('BELOW_' + 'PANE')");
                await Contains(below, "BELOW_PANE");
                await window.ClickAction("ClosePane");
                Pass("split-below", passed);

                await window.ClickAction("NewWorkspace");
                var other = (TerminalPane)window.Current!.Active!;
                Track(ownedProcesses, other.ProcessId);
                clipboard.Text = "Write-Output ('PASTE_' + 'WORKSPACE')\r";
                await window.ClickAction("Paste");
                await Contains(other, "PASTE_WORKSPACE");
                window.SelectWorkspace(0);
                await Contains(first, "日本語😀_ready");
                Require(!(await Text(first)).Contains("PASTE_WORKSPACE"), "Workspace outputs were mixed.");
                window.SelectWorkspace(1);
                await Contains(other, "PASTE_WORKSPACE");
                await window.ClickAction("CloseWorkspace");
                Require(window.Workspaces.Count() == 1, "Workspace close failed.");
                Pass("workspace-switch-and-paste-action", passed);

                var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                first.Core.NavigationCompleted += (_, _) => navigation.TrySetResult();
                first.Core.Navigate("https://example.invalid/");
                await navigation.Task.WaitAsync(Deadline);
                Require(first.Core.Source == TerminalPane.Page, "Terminal escaped its fixed local origin.");
                await Command(first, "Write-Output ('<script>' + 'window.pwned=1</script>')");
                await Contains(first, "<script>window.pwned=1</script>");
                Require(await first.Core.ExecuteScriptAsync("typeof window.pwned") == "\"undefined\"", "Terminal output executed JavaScript.");
                Pass("navigation-and-output-boundary", passed);

                await window.ClickAction("NewWorkspace");
                var noisy = (TerminalPane)window.Current!.Active!;
                Track(ownedProcesses, noisy.ProcessId);
                await Command(noisy, "while ($true) { [Console]::Write('LOAD_') }");
                await Contains(noisy, "LOAD_LOAD_");
                foreach (var type in new[] { "keyDown", "keyUp" })
                    await noisy.Core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key = "c", code = "KeyC", modifiers = 2, windowsVirtualKeyCode = 67, nativeVirtualKeyCode = 67 }));
                await Until(async () => (await Text(noisy)).TrimEnd().EndsWith("CMUX_TEST_READY>", StringComparison.Ordinal), "Ctrl+C did not return the running shell to its prompt.");
                Pass("ctrl-c-interrupts-shell-output", passed);
                await Command(noisy, "while ($true) { [Console]::Write('CLOSE_') }");
                await Contains(noisy, "CLOSE_CLOSE_");
                await window.ClickAction("CloseWorkspace").WaitAsync(Deadline);
                await Command(first, "Write-Output ('AFTER_' + 'CLOSE')");
                await Contains(first, "AFTER_CLOSE");
                Pass("close-during-output-preserves-other-terminal", passed);

                for (var index = 1; index < 10; index++)
                {
                    await window.ClickAction("NewWorkspace");
                    var pane = (TerminalPane)window.Current!.Active!;
                    Track(ownedProcesses, pane.ProcessId);
                    await Command(pane, $"Write-Output ('LIVE_' + '{index}')");
                    await Contains(pane, $"LIVE_{index}");
                }
                Require(window.Workspaces.Count() == 10, "Ten live terminals were not retained.");
                foreach (var workspace in window.Workspaces)
                    foreach (var pane in workspace.Panes.Cast<TerminalPane>())
                    {
                        var process = ownedProcesses.Single(owned => owned.Id == pane.ProcessId);
                        Require(!process.WaitForExit(0), "A workspace shell exited while switching.");
                    }
                window.SelectWorkspace(0);
                await Contains(first, "AFTER_CLOSE");
                Pass("ten-live-desktop-terminals", passed);

                var blockedReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var blockedOutput = new StringBuilder();
                using (var blocked = ConPtySession.Start(Environment.ProcessPath!, ["--blocked-input"], directory, 80, 24, text =>
                {
                    blockedOutput.Append(text);
                    if (blockedOutput.ToString().Contains("BLOCKED_READY")) blockedReady.TrySetResult();
                }))
                {
                    await blockedReady.Task.WaitAsync(Deadline);
                    Track(ownedProcesses, blocked.ProcessId);
                    using var cancelInput = new CancellationTokenSource();
                    var write = blocked.WriteAsync(new string('x', 4_000_000), cancelInput.Token);
                    Require(!write.IsCompleted, "The no-reader fixture did not produce a pending pipe write.");
                    cancelInput.Cancel();
                    try { await write.WaitAsync(Deadline); throw new InvalidOperationException("Pending input ignored cancellation."); }
                    catch (OperationCanceledException) when (cancelInput.IsCancellationRequested) { }
                }
                Pass("pending-native-input-cancels-before-close", passed);

                await PasteLimit(window, clipboard, ownedProcesses);
                Pass("bracketed-paste-content-limit", passed);

                Require(PackagingAndLocales() == 0, "Application localization or distribution notices failed.");
                Pass("localized-ui-actions", passed);
                await BrowserFailureScenario.Run(window, passed, uiErrors);
                await BrowserScenario.Run(window, passed, args.Length > 0 ? args[0] : null);
                SessionStoreScenario.Run(passed);
                await SessionScenario.Run(shell, passed, args.Length > 0 ? args[0] : null);
                await SessionRegressionScenario.Run(shell, passed);
                await WorkingDirectoryScenario.Run(shell, passed, args.Length > 0 ? args[0] : null);
                await UnicodeInputScenario.Run(shell, passed, args.ElementAtOrDefault(0));
                await WorkspaceRenameScenario.Run(shell, passed, args.ElementAtOrDefault(0));
                await WorkspaceOrderScenario.Run(shell, passed, args.ElementAtOrDefault(0));
                await PaneDetachScenario.Run(shell, passed, args.ElementAtOrDefault(0));
                await GitStatusScenario.Run(shell, passed, args.ElementAtOrDefault(0));
                Require(window.LastError is null, "The product reported an error: " + window.LastError);
                Require(uiErrors.Count == 0, "An exception reached native UI dispatch.");
                if (args.Length > 0)
                {
                    Directory.CreateDirectory(args[0]);
                    using var image = File.Create(Path.Combine(args[0], "terminal.png"));
                    await first.Core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, image);
                }
                await window.ClickAction("NewWorkspace");
                await ImeScenario.Run(window, passed, artifacts: args.ElementAtOrDefault(0));
                await ImeScenario.Fallback(shell, passed, args.ElementAtOrDefault(0));
                await TerminalSearchScenario.Run(window, clipboard, passed, args.Length > 0 ? args[0] : null);
                await window.Shutdown().WaitAsync(Deadline);
                Require(!Directory.Exists(profile), "Application profile was not removed.");
                Require(window.BrowserProfilePath is { } browserProfile && !Directory.Exists(browserProfile), "Browser profile was not removed.");
                foreach (var process in ownedProcesses)
                    Require(process.WaitForExit(0), "The original owned shell is still running.");
                Pass("owned-process-and-profile-teardown", passed);
                Console.WriteLine(JsonSerializer.Serialize(new { os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(), tests = passed }));
                exitCode = 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine("product-error: " + window.LastError);
                if (window.Current?.Active is TerminalPane failedPane)
                    try { Console.Error.WriteLine("rendered: " + (await Text(failedPane))); } catch (Exception capture) { Console.Error.WriteLine(capture.Message); }
            }
            finally
            {
                try { await window.Shutdown().WaitAsync(Deadline); } catch (Exception error) { Console.Error.WriteLine("cleanup: " + error); }
                window.Close();
                foreach (var process in ownedProcesses) process.Dispose();
                try { Directory.Delete(directory, recursive: true); } catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
            }
        };
        Application.Run(window);
        return exitCode;
    }
    private static Process Track(List<Process> processes, int pid)
    {
        var process = Process.GetProcessById(pid);
        try { _ = process.SafeHandle; processes.Add(process); return process; }
        catch { process.Dispose(); throw; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task PasteLimit(DesktopWindow window, MemoryClipboard clipboard, List<Process> ownedProcesses)
    {
        await window.ClickAction("NewWorkspace");
        var pane = (TerminalPane)window.Current!.Active!;
        Track(ownedProcesses, pane.ProcessId);
        await Until(async () => (await Text(pane)).TrimEnd().EndsWith("CMUX_TEST_READY>", StringComparison.Ordinal), "Paste shell was not ready.");
        await pane.Core.ExecuteScriptAsync("window.pasteLength=0;term.onData(data=>window.pasteLength=data.length);term.write('\\x1b[?2004h');");
        await Until(async () => await pane.Core.ExecuteScriptAsync("term.modes.bracketedPasteMode") == "true", "Bracketed paste was not enabled.");
        const string marker = "PASTE_BUDGET_MARK";
        clipboard.Text = new string(' ', 32768 - marker.Length) + marker;
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pane.Failed += message => failure.TrySetResult(message);
        await window.ClickAction("Paste");
        using var forwardingCancellation = new CancellationTokenSource();
        var forwarded = Until(async () => (await Text(pane)).Contains(marker), "Clipboard input did not reach the shell.", forwardingCancellation.Token);
        if (await Task.WhenAny(forwarded, failure.Task) == failure.Task)
        {
            forwardingCancellation.Cancel();
            try { await forwarded; } catch (OperationCanceledException) { }
            throw new InvalidOperationException("Allowed clipboard content was rejected: " + await failure.Task);
        }
        await forwarded;
        Require(await pane.Core.ExecuteScriptAsync("window.pasteLength") == "32780", "The test did not exercise bracketed-paste framing.");
        Require(window.LastError is null, "Paste reported a product error.");
        await window.ClickAction("CloseWorkspace");
    }
    private static int PackagingAndLocales()
    {
        var failures = new List<string>();
        try
        {
            var macao = new Strings(CultureInfo.GetCultureInfo("zh-MO"));
            var traditional = new Strings(CultureInfo.GetCultureInfo("zh-Hant"));
            Require(macao["NewWorkspace"] == traditional["NewWorkspace"], "zh-MO did not select Traditional Chinese.");
        }
        catch (Exception error) { failures.Add(error.Message); }
        var previousCulture = CultureInfo.CurrentUICulture;
        var previousOverride = Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER");
        var unused = Path.Combine(Path.GetTempPath(), "cmux-profile-refusal-" + Guid.NewGuid().ToString("N"));
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja");
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", unused);
            try { WebViewProfile.Create().GetAwaiter().GetResult(); throw new Exception("Profile override was accepted."); }
            catch (InvalidOperationException error)
            {
                Require(error.Message == new Strings()["ProfileOverride"], "Profile refusal bypassed the Japanese UI catalog: " + error.Message);
                Require(!Directory.Exists(unused), "Refused profile was opened.");
            }
        }
        catch (Exception error) { failures.Add(error.Message); }
        finally
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", previousOverride);
            CultureInfo.CurrentUICulture = previousCulture;
        }
        try
        {
            var notice = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Microsoft.Web.WebView2.LICENSE.txt"));
            Require(notice.Contains("Microsoft") && notice.Contains("Redistribution and use"), "WebView2 distribution notice is incomplete.");
        }
        catch (Exception error) { failures.Add(error.Message); }
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream("Cmux.Desktop.Strings.json")!;
        var catalogs = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)!;
        foreach (var locale in catalogs.Keys)
        {
            var strings = new Strings(CultureInfo.GetCultureInfo(locale));
            foreach (var key in catalogs["en"].Keys)
                if (!catalogs[locale].ContainsKey(key) || string.IsNullOrWhiteSpace(strings[key])) failures.Add("Missing localized key " + locale + ":" + key);
            if (!strings.Format("Workspace", 7).Contains('7')) failures.Add("Missing workspace placeholder: " + locale);
        }
        foreach (var failure in failures) Console.Error.WriteLine("regression: " + failure);
        return failures.Count == 0 ? 0 : 1;
    }
    private static void Pass(string name, List<string> passed) { passed.Add(name); Console.WriteLine("passed: " + name); }
    internal static async Task<string> Text(TerminalPane pane) => JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("window.cmux.text()"))!;
    private static Task Mouse(TerminalPane pane, string type, double x, double y, int buttons) =>
        pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = "left", buttons, clickCount = 1 }));
    private static async Task SelectText(TerminalPane pane, string marker)
    {
        // Inspect pinned xterm renderer cell geometry; selection itself uses real mouse events.
        var position = await pane.Core.ExecuteScriptAsync("(()=>{const marker=" + JsonSerializer.Serialize(marker) + ";const b=term.buffer.active;let row=-1;for(let i=b.viewportY;i<b.viewportY+term.rows;i++)if(b.getLine(i)?.translateToString(true)===marker){row=i-b.viewportY;break;}const r=term.element.querySelector('.xterm-screen').getBoundingClientRect(),c=term._core._renderService.dimensions.css.cell;return {row,x:r.x+1,y:r.y+(row+.5)*c.height,end:r.x+marker.length*c.width};})()");
        using var json = JsonDocument.Parse(position);
        var p = json.RootElement;
        Require(p.GetProperty("row").GetInt32() >= 0, "Selection marker is not visible.");
        var x = p.GetProperty("x").GetDouble();
        var y = p.GetProperty("y").GetDouble();
        var end = p.GetProperty("end").GetDouble();
        await Mouse(pane, "mousePressed", x, y, 1);
        await Mouse(pane, "mouseMoved", end, y, 1);
        await Mouse(pane, "mouseReleased", end, y, 0);
        await Until(async () => JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("window.cmux.selection()")) == marker, "Mouse selection did not select the marker.");
    }
    private static async Task<(int Cols, int Rows)> Size(TerminalPane pane)
    {
        using var json = JsonDocument.Parse(await pane.Core.ExecuteScriptAsync("window.cmux.size()"));
        return (json.RootElement.GetProperty("cols").GetInt32(), json.RootElement.GetProperty("rows").GetInt32());
    }
    internal static Task Contains(TerminalPane pane, string marker) => Until(async () => (await Text(pane)).Contains(marker), "Missing rendered output: " + marker);
    internal static async Task Until(Func<Task<bool>> check, string failure, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Deadline) { cancellationToken.ThrowIfCancellationRequested(); if (await check()) return; await Task.Delay(50, cancellationToken); }
        throw new TimeoutException(failure);
    }
    internal static async Task Command(TerminalPane pane, string text)
    {
        await Until(async () => (await Text(pane)).TrimEnd().EndsWith("CMUX_TEST_READY>", StringComparison.Ordinal), "PowerShell did not finish startup or return to its prompt.");
        pane.FocusTerminal();
        await pane.Core.ExecuteScriptAsync("window.cmux.focus()");
        await pane.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text }));
        foreach (var type in new[] { "keyDown", "keyUp" })
            await pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, text = type == "keyDown" ? "\r" : "" }));
    }
}

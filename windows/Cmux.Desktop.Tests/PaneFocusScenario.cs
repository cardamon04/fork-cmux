using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cmux.Desktop;

internal static class PaneFocusScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed)
    {
        var errors = new List<Exception>();
        foreach (var shell in new[] { "pwsh", "windows-powershell" })
        {
            var launch = shell == "pwsh" ? source : source with
            {
                Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            };
            try { await Integration(launch, shell, passed); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException("Pane focus scenarios failed.", errors);
        foreach (var locale in new[] { "en", "ja", "de", "fr", "ar", "es", "zh-Hant", "zh-Hans", "ko" })
        {
            var strings = new Strings(CultureInfo.GetCultureInfo(locale));
            Require(!string.IsNullOrWhiteSpace(strings["FocusPreviousPane"]) && !string.IsNullOrWhiteSpace(strings["FocusNextPane"])
                && strings["FocusPreviousPane"] != strings["FocusNextPane"], "Missing distinct pane focus labels: " + locale);
        }
        Pass(passed, "nine-locale-actions");
    }

    private static async Task Integration(ShellLaunch source, string shell, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-pane-focus-").FullName;
        var path = Path.Combine(root, "layout.json");
        var firstDirectory = Directory.CreateDirectory(Path.Combine(root, "first_日本語")).FullName;
        var clipboard = new MemoryClipboard { Text = "private pane focus sentinel" };
        await using var fixture = new BrowserFixture();
        using var window = new DesktopWindow(source with { Directory = root }, clipboard, new SessionStore(path)) { ShowInTaskbar = false };
        var processes = new Dictionary<TerminalPane, Process>();
        var profiles = new HashSet<string>();
        var phase = "setup";
        void Track(TerminalPane pane) { var process = Process.GetProcessById(pane.ProcessId); _ = process.SafeHandle; processes.Add(pane, process); }
        try
        {
            window.Show(); await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profiles.Add(window.ProfilePath);
            var workspace = window.Current!;
            var first = (TerminalPane)workspace.Active!; Track(first);
            await Program.Contains(first, "CMUX_TEST_READY>");
            await window.ClickAction("OpenBrowser");
            var browser = (BrowserPane)workspace.Active!; profiles.Add(window.BrowserProfilePath!);
            browser.NavigateFromAddress(fixture.Url("pane-focus"));
            await Program.Until(async () => await browser.Core.ExecuteScriptAsync("document.querySelector('#page')?.textContent") == "\"/pane-focus\"", "The owned focus browser did not load.");
            first.FocusTerminal();
            await window.ClickAction("SplitBelow");
            var middle = (TerminalPane)workspace.Active!; Track(middle);
            await Program.Contains(middle, "CMUX_TEST_READY>");
            Require(workspace.Panes.SequenceEqual(new WorkspacePane[] { first, browser, middle }), "The fixture does not distinguish creation order from split-tree order.");
            var identities = workspace.Panes.ToDictionary(pane => pane, pane => pane.Core);
            var nonce = Guid.NewGuid().ToString("N");
            await browser.Core.ExecuteScriptAsync("window.paneFocusState=" + JsonSerializer.Serialize(nonce));
            await Program.Command(first, "Set-Location -LiteralPath '" + firstDirectory.Replace("'", "''", StringComparison.Ordinal) + "'");
            await Program.Until(() => Task.FromResult(first.WorkingDirectory.Equals(firstDirectory, StringComparison.OrdinalIgnoreCase)), "The owned CWD precondition was not reported.");
            first.FocusTerminal();
            var toolbar = window.Controls.OfType<ToolStrip>().First(strip => strip is not StatusStrip);
            var previous = toolbar.Items["FocusPreviousPane"];
            var next = toolbar.Items["FocusNextPane"];
            phase = "native-actions";
            Console.WriteLine("PANE_FOCUS_PRECONDITION " + JsonSerializer.Serialize(new
            {
                shell, terminalsReady = true, browserReady = true, panes = workspace.Panes.Count,
                creationOrderDiffers = true, previousAction = previous is not null, nextAction = next is not null,
                previousEnabled = previous?.Enabled, nextEnabled = next?.Enabled,
            }));
            Require(previous is ToolStripButton { Enabled: true } && next is ToolStripButton { Enabled: true }, "Native Previous/Next pane actions are missing.");
            Pass(passed, "native-actions-" + shell);

            async Task Step(string action, WorkspacePane expected)
            {
                // Exercise the existing host-toolbar focus handoff as well as the model.
                Require(toolbar.Focus() && toolbar.ContainsFocus, "The owned toolbar did not acquire focus.");
                await window.ClickAction(action);
                await Program.Until(() => Task.FromResult(window.Current == workspace && workspace.Active == expected && expected.ContainsFocus), "Pane cycling did not focus the expected live pane.");
                Require(window.IsActionEnabled("Find") == (expected is TerminalPane)
                    && window.IsActionEnabled("SelectAllOutput") == (expected is TerminalPane), "Pane cycling left stale terminal action state.");
                var cwd = window.Controls.OfType<StatusStrip>().Single().Items["WorkingDirectory"]!.Text ?? "";
                Require(expected is TerminalPane terminal ? cwd.Contains(terminal.WorkingDirectory, StringComparison.OrdinalIgnoreCase) : cwd.Length == 0,
                    "Pane cycling left the selected pane's CWD status stale.");
            }
            phase = "next-tree-wrap";
            await Step("FocusNextPane", middle); await Step("FocusNextPane", browser); await Step("FocusNextPane", first);
            Pass(passed, "next-tree-order-wrap-" + shell);
            phase = "previous-tree-wrap";
            await Step("FocusPreviousPane", browser); await Step("FocusPreviousPane", middle); await Step("FocusPreviousPane", first);
            Pass(passed, "previous-tree-order-wrap-" + shell);
            await Step("FocusPreviousPane", browser);
            Require(await browser.Core.ExecuteScriptAsync("document.hasFocus() && window.paneFocusState === " + JsonSerializer.Serialize(nonce)) == "true",
                "Browser focus did not reach the existing page or lost document state.");
            Require(identities.All(pair => ReferenceEquals(pair.Key.Core, pair.Value)), "Cycling replaced a live renderer.");
            Pass(passed, "browser-page-focus-and-state-" + shell);

            phase = "pending-input";
            await Step("FocusNextPane", first);
            await window.ClickAction("SelectAllOutput");
            var selection = await Selection(first); Require(selection.Length > 0, "The focus fixture did not start with retained output selected.");
            await Step("FocusNextPane", middle); await Step("FocusNextPane", browser); await Step("FocusNextPane", first);
            Require(await Selection(first) == selection, "Pane cycling cleared retained output selection.");
            var output = "FOCUS_PENDING_日本語Ω_" + nonce;
            var command = "Write-Output ('FOCUS_'+'PENDING_日本語Ω_" + nonce + "')";
            await first.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = command }));
            await Program.Until(async () => (await LogicalText(first)).Contains(command, StringComparison.Ordinal), "The owned unfinished input was not echoed.");
            await Step("FocusNextPane", middle); await Step("FocusNextPane", browser); await Step("FocusNextPane", first);
            Require(!(await LogicalText(first)).Contains(output, StringComparison.Ordinal) && !(await LogicalText(middle)).Contains(nonce, StringComparison.Ordinal)
                && clipboard.Text == "private pane focus sentinel", "Cycling submitted input, sent it to a neighbor or wrote the clipboard.");
            foreach (var type in new[] { "keyDown", "keyUp" })
                await first.Core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, text = type == "keyDown" ? "\r" : "" }));
            await Program.Until(async () => (await LogicalText(first)).Contains(output, StringComparison.Ordinal), "Pane cycling lost unfinished Unicode input.");
            Pass(passed, "selection-and-pending-input-" + shell);

            phase = "workspace-isolation";
            await window.ClickAction("NewWorkspace");
            var other = window.Current!; var otherPane = (TerminalPane)other.Active!; Track(otherPane);
            await Program.Contains(otherPane, "CMUX_TEST_READY>");
            Require(!window.IsActionEnabled("FocusPreviousPane") && !window.IsActionEnabled("FocusNextPane"), "A single-pane workspace enabled pane cycling.");
            await window.ClickAction("FocusNextPane"); await window.ClickAction("FocusPreviousPane");
            Require(window.Current == other && other.Active == otherPane && workspace.Active == first, "A disabled cycle changed workspace selection.");
            window.SelectWorkspace(0);
            await Step("FocusNextPane", middle); await Step("FocusPreviousPane", first);
            Require(other.Active == otherPane && other.Panes.Count == 1, "Cycling crossed workspace ownership.");
            Pass(passed, "workspace-isolation-and-single-pane-" + shell);

            phase = "close-split-order";
            middle.FocusTerminal(); await window.ClickAction("ClosePane");
            Require(middle.IsDisposed && processes[middle].WaitForExit(0), "The closed focus fixture pane remained live.");
            first.FocusTerminal(); await Step("FocusNextPane", browser); await Step("FocusNextPane", first);
            await window.ClickAction("SplitBelow");
            var replacement = (TerminalPane)workspace.Active!; Track(replacement);
            await Program.Contains(replacement, "CMUX_TEST_READY>");
            first.FocusTerminal(); await Step("FocusNextPane", replacement); await Step("FocusNextPane", browser);
            Pass(passed, "close-and-split-refresh-order-" + shell);

            phase = "move-order";
            await window.ClickAction("MovePaneToNewWorkspace");
            var browserWorkspace = window.Current!;
            Require(browserWorkspace != workspace && browserWorkspace.Active == browser && browserWorkspace.Panes.Count == 1
                && !window.IsActionEnabled("FocusNextPane") && !window.IsActionEnabled("FocusPreviousPane"), "Moving the live browser did not refresh cycle availability.");
            Require(ReferenceEquals(browser.Core, identities[browser]) && await browser.Core.ExecuteScriptAsync("window.paneFocusState") == JsonSerializer.Serialize(nonce), "Moving/cycling replaced browser document state.");
            window.SelectWorkspace(0); first.FocusTerminal();
            await Step("FocusNextPane", replacement); await Step("FocusNextPane", first);
            Require(workspace.Panes.Count == 2 && other.Active == otherPane, "Cycling retained a moved pane or changed a neighboring workspace.");
            Pass(passed, "move-refresh-order-" + shell);

            phase = "save-selection";
            await Step("FocusNextPane", replacement);
            await window.ClickAction("SaveSession");
            var savedText = File.ReadAllText(path); var saved = JsonSerializer.Deserialize<SessionSnapshot>(savedText)!;
            saved.Validate();
            Require(saved.SelectedWorkspace == 0 && saved.Workspaces[0].ActivePane == 1 && window.CaptureSession().Workspaces[0].ActivePane == 1
                && !savedText.Contains(nonce, StringComparison.Ordinal), "Cycling selection was not saved in the existing ActivePane field or expanded saved content.");
            Require(window.LastError is null && ReferenceEquals(first.Core, identities[first]), "Pane cycling restarted a terminal or reported a product error.");
            Pass(passed, "active-pane-save-roundtrip-" + shell);
        }
        catch (Exception error)
        {
            Console.WriteLine("PANE_FOCUS_FAILURE " + JsonSerializer.Serialize(new { shell, phase, type = error.GetType().Name, message = error.Message }));
            throw;
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(45));
            foreach (var process in processes.Values) { Require(process.WaitForExit(0), "An owned focus shell survived shutdown."); process.Dispose(); }
            foreach (var profile in profiles) Require(!Directory.Exists(profile), "An owned focus profile survived shutdown.");
            var full = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(full)!.Equals(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("cmux-pane-focus-", StringComparison.Ordinal), "Refusing unowned focus fixture cleanup.");
            Directory.Delete(full, recursive: true);
        }
        Pass(passed, "owned-shutdown-" + shell);
    }
    private static async Task<string> Selection(TerminalPane pane) => JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("window.cmux.selection()"))!;
    private static async Task<string> LogicalText(TerminalPane pane) => JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("(() => { const b=term.buffer.active; let text=''; for(let i=0;i<b.length;i++){ const line=b.getLine(i); if(i&&!line.isWrapped)text+='\\n'; text+=line.translateToString(true); } return text; })()"))!;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pass(List<string> passed, string name) { var value = "pane-focus-" + name; passed.Add(value); Console.WriteLine("passed: " + value); }
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cmux.Desktop;

internal static class TerminalSelectAllScenario
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
        if (errors.Count != 0) throw new AggregateException("Terminal select-all scenarios failed.", errors);
        foreach (var locale in new[] { "en", "ja", "de", "fr", "ar", "es", "zh-Hant", "zh-Hans", "ko" })
            Require(!string.IsNullOrWhiteSpace(new Strings(CultureInfo.GetCultureInfo(locale))["SelectAllOutput"]), "Missing select-all label: " + locale);
        Pass(passed, "nine-locale-action");
    }

    private static async Task Integration(ShellLaunch source, string shell, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-select-all-").FullName;
        var clipboard = new MemoryClipboard { Text = "private clipboard sentinel" };
        using var window = new DesktopWindow(source with { Directory = root }, clipboard) { ShowInTaskbar = false };
        var processes = new List<Process>();
        var profiles = new List<string>();
        var selectionErrors = new List<Exception>();
        void Track(TerminalPane pane) { var process = Process.GetProcessById(pane.ProcessId); _ = process.SafeHandle; processes.Add(process); }
        var phase = "native-action";
        TerminalPane? observed = null;
        try
        {
            window.Show(); await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profiles.Add(window.ProfilePath);
            var first = (TerminalPane)window.Current!.Active!; Track(first);
            observed = first;
            await Program.Contains(first, "CMUX_TEST_READY>");
            var toolbar = window.Controls.OfType<ToolStrip>().First(strip => strip is not StatusStrip);
            var action = toolbar.Items["SelectAllOutput"];
            Console.WriteLine("SELECT_ALL_PRECONDITION " + JsonSerializer.Serialize(new
            {
                shell, rendererSource = first.Core.Source, first.ProcessId,
                readyPrompt = (await Program.Text(first)).Contains("CMUX_TEST_READY>"), nativeAction = action is not null, enabled = action?.Enabled,
            }));
            Require(action is ToolStripButton { Enabled: true }, "The native selected-terminal Select all output action is missing.");
            await window.ClickAction("SplitRight");
            var neighbor = (TerminalPane)window.Current!.Active!; Track(neighbor);
            await Program.Contains(neighbor, "CMUX_TEST_READY>");
            first.FocusTerminal();
            var nonce = Guid.NewGuid().ToString("N");
            var begin = "SELECT_BEGIN_" + nonce; var end = "SELECT_END_" + nonce;
            var unicode = "SELECT_日本語Ω_" + nonce;
            await Program.Command(first, "Write-Output ('SELECT_BEGIN_'+'" + nonce + "'); 1..70 | ForEach-Object { Write-Output ('selection_line_'+$_) }; Write-Output ('SELECT_'+'日本語Ω_" + nonce + "'); Write-Output ('SELECT_END_'+'" + nonce + "')");
            await Program.Contains(first, end);
            await Prompt(first);
            Require(await first.Core.ExecuteScriptAsync("term.buffer.active.baseY > 0") == "true", "The fixture did not put output into retained scrollback.");
            var firstWorkspace = window.Current!;
            var identity = first.Core;

            phase = "retained-output";
            await window.ClickAction("SelectAllOutput");
            var selected = await Selection(first);
            Require(selected.Contains(begin) && selected.Contains(end) && Compact(selected).Contains(unicode)
                && selected.Contains("selection_line_1") && selected.Contains("selection_line_70"), "Select all omitted retained or Unicode output.");
            Require(await Selection(neighbor) == "" && window.Current == firstWorkspace && firstWorkspace.Active == first,
                "Select all changed a neighboring selection or the selected pane.");
            Pass(passed, "retained-output-and-pane-isolation-" + shell);
            Require(clipboard.Text == "private clipboard sentinel", "Select all wrote the clipboard without Copy.");
            await window.ClickAction("Copy");
            Require(clipboard.Text == selected, "Explicit Copy did not use the selected output.");
            Pass(passed, "copy-is-explicit-" + shell);

            phase = "find-ownership";
            var originalRows = JsonSerializer.Deserialize<int>(await first.Core.ExecuteScriptAsync("term.rows"));
            await window.ClickAction("Find");
            await Program.Until(async () => await first.Core.ExecuteScriptAsync("!document.getElementById('find-bar').hidden && term.rows < " + originalRows) == "true", "Find did not occupy a settled row in the fixture.");
            var findRows = JsonSerializer.Deserialize<int>(await first.Core.ExecuteScriptAsync("term.rows"));
            await Query(first, begin);
            await Program.Until(async () => await Selection(first) == begin, "The decorated search did not select the unique output marker.");
            clipboard.Text = "private find-copy sentinel";
            await window.ClickAction("SelectAllOutput");
            await Program.Until(async () => await first.Core.ExecuteScriptAsync("document.getElementById('find-bar').hidden && term.rows > " + findRows) == "true", "Dismissal did not restore the terminal rows.");
            await window.ClickAction("Copy");
            Check(clipboard.Text.Contains(begin) && clipboard.Text.Contains(end) && await Selection(first) == clipboard.Text,
                "find-query-ownership", "A settled Find dismissal lost the new selection or Copy reused old clipboard text.");

            phase = "addon-refresh";
            await window.ClickAction("Find");
            await Query(first, begin);
            await Program.Until(async () => await Selection(first) == begin, "The timer fixture did not establish a decorated search.");
            await first.Core.ExecuteScriptAsync("""
                (() => {
                  const state = window.selectAllClock = {set: window.setTimeout, clear: window.clearTimeout, fit: fit.fit, pending: new Map(), next: -1};
                  window.setTimeout = (callback, delay, ...args) => {
                    if (delay !== 200) return Reflect.apply(state.set, window, [callback, delay, ...args]);
                    const id = state.next--; state.pending.set(id, () => callback(...args)); return id;
                  };
                  window.clearTimeout = id => { if (!state.pending.delete(id)) Reflect.apply(state.clear, window, [id]); };
                  // Hold geometry constant to isolate an already queued addon callback.
                  fit.fit = () => {};
                })()
                """);
            try
            {
                first.FocusTerminal();
                var refresh = "SELECT_REFRESH_" + nonce;
                await Program.Command(first, "Write-Output ('SELECT_'+'REFRESH_" + nonce + "')");
                await Program.Contains(first, refresh); await Prompt(first);
                await Program.Until(async () => await first.Core.ExecuteScriptAsync("window.selectAllClock.pending.size > 0") == "true", "No pinned search-addon refresh was queued.");
                clipboard.Text = "private addon-copy sentinel";
                await window.ClickAction("SelectAllOutput");
                await first.Core.ExecuteScriptAsync("(() => { const s=window.selectAllClock; const callbacks=[...s.pending.values()]; s.pending.clear(); for (const callback of callbacks) callback(); })()");
                await window.ClickAction("Copy");
                Check(clipboard.Text.Contains(begin) && clipboard.Text.Contains(refresh) && await Selection(first) == clipboard.Text,
                    "queued-search-refresh-cancelled", "An owned delayed search callback cleared the output selection after dismissal.");
            }
            finally
            {
                await first.Core.ExecuteScriptAsync("(() => { const s=window.selectAllClock; if (!s) return; window.setTimeout=s.set; window.clearTimeout=s.clear; fit.fit=s.fit; s.pending.clear(); delete window.selectAllClock; fit.fit(); })()");
            }

            phase = "hidden-find-events";
            await window.ClickAction("SelectAllOutput");
            clipboard.Text = "private hidden-find sentinel";
            await first.Core.ExecuteScriptAsync("(() => { const q=document.getElementById('find-query'); q.dispatchEvent(new CompositionEvent('compositionstart',{data:'漢'})); q.dispatchEvent(new CompositionEvent('compositionend',{data:'字'})); q.dispatchEvent(new Event('input',{bubbles:true})); })()");
            await window.ClickAction("Copy");
            Check(clipboard.Text.Contains(begin) && clipboard.Text.Contains(end) && await Selection(first) == clipboard.Text,
                "hidden-find-events-ignore-selection", "A late hidden Find event cleared the terminal selection.");

            phase = "pending-input";
            var output = "SELECT_PENDING_日本語Ω_" + nonce;
            var command = "Write-Output ('SELECT_'+'PENDING_日本語Ω_" + nonce + "')";
            await first.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = command }));
            await Program.Until(async () => Compact(await Program.Text(first)).Contains(Compact(command)), "The owned shell did not display its unfinished input.");
            await window.ClickAction("SelectAllOutput");
            await window.ClickAction("Copy");
            Require(!(await Program.Text(first)).Contains(output), "Select all submitted unfinished shell input.");
            foreach (var type in new[] { "keyDown", "keyUp" })
                await first.Core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
                {
                    type, key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, text = type == "keyDown" ? "\r" : "",
                }));
            await Program.Contains(first, output); await Prompt(first);
            Require(ReferenceEquals(first.Core, identity), "Select all restarted the selected renderer.");
            Pass(passed, "pending-unicode-input-preserved-" + shell);

            phase = "workspace-browser";
            await window.ClickAction("SelectAllOutput");
            var priorSelection = await Selection(first);
            Require(priorSelection.Contains(begin) && priorSelection.Contains(output), "Workspace isolation did not start with a real nonempty terminal selection.");
            await window.ClickAction("NewWorkspace");
            var other = (TerminalPane)window.Current!.Active!; Track(other);
            await Program.Contains(other, "CMUX_TEST_READY>");
            await window.ClickAction("SelectAllOutput");
            Require(await Selection(other) != "" && await Selection(first) == priorSelection, "Workspace selection leaked across renderers.");
            await window.ClickAction("OpenBrowser");
            profiles.Add(window.BrowserProfilePath!);
            Require(!window.IsActionEnabled("SelectAllOutput"), "Terminal select all is enabled for a browser.");
            var before = clipboard.Text; await window.ClickAction("SelectAllOutput");
            Require(clipboard.Text == before && await Selection(first) == priorSelection, "A disabled browser action affected terminal state.");
            await window.ClickAction("ClosePane");
            Require(window.IsActionEnabled("SelectAllOutput"), "Returning to a terminal did not enable select all.");
            Require(window.LastError is null, "Select all reported a product error: " + window.LastError);
            Pass(passed, "workspace-browser-isolation-" + shell);

            void Check(bool condition, string name, string message)
            {
                if (condition) { Pass(passed, name + "-" + shell); return; }
                selectionErrors.Add(new InvalidOperationException(message));
                Console.WriteLine("SELECT_ALL_CHECK_FAILURE " + JsonSerializer.Serialize(new { shell, phase, name, message }));
            }
        }
        catch (Exception error)
        {
            Console.WriteLine("SELECT_ALL_FAILURE " + JsonSerializer.Serialize(new { shell, phase, error = error.Message, type = error.GetType().Name }));
            try
            {
                if (observed is { IsDisposed: false })
                    Console.WriteLine("SELECT_ALL_DIAGNOSTIC " + JsonSerializer.Serialize(new
                    {
                        shell, phase, state = JsonSerializer.Deserialize<JsonElement>(await observed.Core.ExecuteScriptAsync("({active:document.activeElement?.id, terminalFocused:document.activeElement===term.textarea, rows:term.rows, cols:term.cols, selectionLength:term.getSelection().length, text:window.cmux.text().slice(-8000)})")),
                    }));
            }
            catch (Exception diagnostic) { Console.WriteLine("SELECT_ALL_DIAGNOSTIC_UNAVAILABLE " + JsonSerializer.Serialize(new { shell, type = diagnostic.GetType().Name })); }
            throw;
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(45));
            foreach (var process in processes) { Require(process.WaitForExit(0), "An owned select-all shell survived shutdown."); process.Dispose(); }
            foreach (var profile in profiles) Require(!Directory.Exists(profile), "An owned select-all profile survived shutdown.");
            var full = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(full)!.Equals(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("cmux-select-all-", StringComparison.Ordinal), "Refusing unowned select-all fixture cleanup.");
            Directory.Delete(full, recursive: true);
        }
        Pass(passed, "owned-shutdown-" + shell);
        if (selectionErrors.Count != 0) throw new AggregateException("Native Find/select-all checks failed.", selectionErrors);
    }
    private static Task Prompt(TerminalPane pane) => Program.Until(async () => (await Program.Text(pane)).TrimEnd().EndsWith("CMUX_TEST_READY>", StringComparison.Ordinal), "The select-all shell did not finish its prompt.");
    private static async Task<string> Selection(TerminalPane pane) => JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("window.cmux.selection()"))!;
    private static Task Query(TerminalPane pane, string value) => pane.Core.ExecuteScriptAsync("(() => { const q=document.getElementById('find-query'); q.value=" + JsonSerializer.Serialize(value) + "; q.focus(); q.select(); q.dispatchEvent(new Event('input',{bubbles:true})); })()");
    private static string Compact(string value) => value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pass(List<string> passed, string name) { var value = "terminal-select-all-" + name; passed.Add(value); Console.WriteLine("passed: " + value); }
}

using System.Text.Json;
using System.Diagnostics;
using Cmux.Desktop;

internal static class TerminalSearchScenario
{
    internal static async Task Run(DesktopWindow window, MemoryClipboard clipboard, List<string> passed, string? artifacts = null)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Pass(string name) { passed.Add(name); Console.WriteLine("passed: " + name); }
        var first = (TerminalPane)window.Current!.Active!;
        await RunClipboard(window, clipboard, passed);
        using var firstProcess = Process.GetProcessById(first.ProcessId);
        _ = firstProcess.SafeHandle;
        await NavigationCopy(window, clipboard, passed);
        // Emit separate words so the echoed command cannot supply the match.
        await Program.Command(first, "Write-Output ('FIND_'+'Alpha'); 1..70 | ForEach-Object { Write-Output ('scroll_'+$_) }; Write-Output ('FIND_'+'Alpha'); Write-Output ('find_'+'alpha')");
        await Program.Contains(first, "scroll_70");
        Require(window.Controls.OfType<ToolStrip>().Any(t => t.Items["Find"] is ToolStripButton), "The terminal find action is missing.");
        await window.ClickAction("Find");
        await Program.Until(async () => await first.Core.ExecuteScriptAsync("document.activeElement.id") == "\"find-query\"", "Find did not focus its input.");
        await Query(first, "FIND_Alpha");
        await Results(first, 3);
        Require(await first.Core.ExecuteScriptAsync("term.buffer.active.viewportY < term.buffer.active.baseY") == "true", "Find did not scroll to an older match.");
        var start = await first.Core.ExecuteScriptAsync("JSON.stringify(term.getSelectionPosition())");
        await Click(first, "find-next");
        await Program.Until(async () => await first.Core.ExecuteScriptAsync("JSON.stringify(term.getSelectionPosition())") != start, "Next did not move the selection.");
        await Click(first, "find-previous");
        await Program.Until(async () => await first.Core.ExecuteScriptAsync("JSON.stringify(term.getSelectionPosition())") == start, "Previous did not return to the prior match.");
        await first.Core.ExecuteScriptAsync("document.getElementById('find-query').focus()");
        await Key(first, "Enter", 13);
        await Program.Until(async () => await first.Core.ExecuteScriptAsync("JSON.stringify(term.getSelectionPosition())") != start, "Enter did not move to the next match.");
        await Key(first, "Enter", 13, shift: true);
        await Program.Until(async () => await first.Core.ExecuteScriptAsync("JSON.stringify(term.getSelectionPosition())") == start, "Shift+Enter did not return to the previous match.");
        await Click(first, "find-case");
        await Results(first, 2);
        await window.ClickAction("Copy"); // Exercises the real selected-match copy path with a private clipboard.
        Require(clipboard.Text == "FIND_Alpha", "Copy did not return the selected search match.");
        Pass("terminal-find-scrollback-navigation-and-case");

        await Query(first, "absent_needle_4e917");
        await Results(first, 0);
        Require(await first.Core.ExecuteScriptAsync("window.cmux.selection()") == "\"\"", "An absent query left a stale selection.");
        await Query(first, "");
        await Program.Until(async () => await first.Core.ExecuteScriptAsync("document.getElementById('find-next').disabled") == "true", "An empty query left navigation enabled.");
        Pass("terminal-find-empty-and-missing-clear-selection");

        await Click(first, "find-close");
        await Program.Command(first, "Write-Output ('検索😀'+'一致'); Write-Output ('literal['+'.*]'); [Console]::WriteLine((' ' * ([Console]::WindowWidth - 4)) + ('WRAP_'+'NEEDLE'))");
        await Program.Contains(first, "検索😀一致");
        await window.ClickAction("Find");
        await Query(first, "検索😀一致");
        await Results(first, 1);
        Require(JsonSerializer.Deserialize<string>(await first.Core.ExecuteScriptAsync("window.cmux.selection()")) == "検索😀一致", "Unicode search selected different text.");
        await Query(first, "literal[.*]");
        await Results(first, 1);
        Require(await first.Core.ExecuteScriptAsync("window.cmux.selection()") == "\"literal[.*]\"", "Literal search treated regex punctuation as a pattern.");
        await Query(first, "WRAP_NEEDLE");
        await Results(first, 1);
        Require(await first.Core.ExecuteScriptAsync("window.cmux.selection().replaceAll('\\n','')") == "\"WRAP_NEEDLE\"", "Search did not select a match across a soft wrap.");
        // Query text stays in the find input; even command-like text never reaches PowerShell.
        await Query(first, "Write-Output ('SEARCH_INPUT_'+'LEAK')");
        await Results(first, 0);
        Require(!(await Program.Text(first)).Contains("SEARCH_INPUT_"), "Find input reached the shell.");
        Pass("terminal-find-unicode-literal-and-input-isolation");

        await Query(first, "FIND_Alpha");
        await Results(first, 2);
        await Program.Command(first, "Write-Output ('FIND_'+'Alpha')");
        await Results(first, 3);
        Pass("terminal-find-updates-after-new-output");

        await Query(first, "FIND_Alpha");
        await Results(first, 3);
        var firstWorkspace = window.Current!;
        await window.ClickAction("NewWorkspace");
        var other = (TerminalPane)window.Current!.Active!;
        using var otherProcess = Process.GetProcessById(other.ProcessId);
        _ = otherProcess.SafeHandle;
        await Program.Command(other, "Write-Output ('OTHER_'+'FIND')");
        await Program.Contains(other, "OTHER_FIND");
        await window.ClickAction("Find");
        await Query(other, "FIND_Alpha");
        await Results(other, 0);
        window.SelectWorkspace(Array.IndexOf(window.Workspaces.ToArray(), firstWorkspace));
        Require(await first.Core.ExecuteScriptAsync("document.getElementById('find-query').value") == "\"FIND_Alpha\"", "Workspace switching lost the pane's search text.");
        await Results(first, 3);
        await window.ClickAction("OpenBrowser");
        Require(!window.IsActionEnabled("Find"), "Terminal find was enabled for a browser pane.");
        await window.ClickAction("ClosePane");
        Require(window.IsActionEnabled("Find"), "Returning to a terminal did not enable find.");
        Pass("terminal-find-pane-workspace-and-browser-isolation");

        if (artifacts is not null)
        {
            Directory.CreateDirectory(artifacts);
            await using var image = File.Create(Path.Combine(artifacts, "terminal-search.png"));
            await first.Core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, image);
        }
        // Escape is local to the find control; it dismisses and returns shell focus.
        await window.ClickAction("Find");
        await Key(first, "Escape", 27);
        await Program.Until(async () => await first.Core.ExecuteScriptAsync("document.getElementById('find-bar').hidden && document.activeElement === term.textarea") == "true", "Escape did not close find and return terminal focus.");
        Require(await first.Core.ExecuteScriptAsync("window.cmux.selection()") == "\"\"", "Closing find kept its selection.");
        await Program.Command(first, "Write-Output ('AFTER_'+'FIND')");
        await Program.Contains(first, "AFTER_FIND");
        // Pending debounce work must not select after close or reach a disposed renderer.
        await window.ClickAction("Find");
        await Query(first, "FIND");
        await Click(first, "find-close");
        await Program.Until(async () => await first.Core.ExecuteScriptAsync("document.getElementById('find-bar').hidden && window.cmux.selection() === ''") == "true", "Close did not cancel the pending query.");
        await window.ClickAction("Find");
        await Query(first, "pending_close");
        await window.ClickAction("CloseWorkspace");
        Require(firstProcess.WaitForExit(0), "The searched workspace's original shell survived close.");
        Require(window.Current?.Active == other, "Closing a searched workspace selected the wrong surviving pane.");
        await Program.Command(other, "Write-Output ('SEARCH_CLOSE_'+'SURVIVOR')");
        await Program.Contains(other, "SEARCH_CLOSE_SURVIVOR");
        Pass("terminal-find-dismiss-and-close-preserve-input");

        await Click(other, "find-close");
        await Program.Command(other, "1..1100 | ForEach-Object { Write-Output ('FIND_LIMIT_'+'MARK') }; Write-Output ('LIMIT_'+'DONE')");
        await Program.Contains(other, "LIMIT_DONE");
        await window.ClickAction("Find");
        await Query(other, "FIND_LIMIT_MARK");
        await Program.Until(async () => await other.Core.ExecuteScriptAsync("document.getElementById('find-results').textContent === '≥ '+new Intl.NumberFormat(document.documentElement.lang).format(1000)") == "true", "The capped count was presented as an exact total.");
        await Click(other, "find-next");
        Require(await other.Core.ExecuteScriptAsync("window.cmux.selection()") == "\"FIND_LIMIT_MARK\"", "Search stopped navigating when highlights reached their cap.");
        await Click(other, "find-close");
        await window.ClickAction("CloseWorkspace");
        Require(otherProcess.WaitForExit(0), "The highlight-cap fixture's original shell survived close.");
        Pass("terminal-find-highlight-cap-remains-navigable");
    }

    internal static async Task RunClipboard(DesktopWindow window, MemoryClipboard clipboard, List<string> passed, string? only = null)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var pane = (TerminalPane)window.Current!.Active!;
        await Program.Command(pane, "Write-Output ('CLIPBOARD_'+'READY')");
        await Program.Contains(pane, "CLIPBOARD_READY");
        await window.ClickAction("Find");
        if (only is null or "paste")
        {
            await Query(pane, "");
            clipboard.Text = "Write-Output ('PASTE_FIND_'+'EXECUTED')\r";
            await window.ClickAction("Paste");
            await Program.Until(async () => JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("document.getElementById('find-query').value"))!.Length > 0
                || (await Program.Text(pane)).Contains("PASTE_FIND_"), "Paste reached neither find input nor shell.");
            Require(!(await Program.Text(pane)).Contains("PASTE_FIND_"), "Toolbar Paste sent search clipboard text to PowerShell.");
            Require(JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("document.getElementById('find-query').value")) == clipboard.Text.Replace("\r", ""), "Toolbar Paste did not insert into the search input.");
            await Query(pane, "12345");
            await pane.Core.ExecuteScriptAsync("document.getElementById('find-query').setSelectionRange(1,3)");
            clipboard.Text = new string('a', 400);
            await window.ClickAction("Paste");
            await Program.Until(async () => JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("document.getElementById('find-query').value")) == "1" + new string('a', 253) + "45", "Search paste did not preserve selection replacement and the 256-unit limit.");
            passed.Add("terminal-find-toolbar-paste-stays-in-search");
            Console.WriteLine("passed: " + passed[^1]);
        }
        if (only is null or "copy")
        {
            await Query(pane, "CLIPBOARD_READY");
            await Results(pane, 1);
            await pane.Core.ExecuteScriptAsync("(()=>{const q=document.getElementById('find-query');q.setSelectionRange(q.value.length,q.value.length);})()");
            var ownsInput = await pane.Core.ExecuteScriptAsync("(()=>{const q=document.getElementById('find-query');return document.activeElement===q&&q.selectionStart===q.selectionEnd;})()") == "true";
            var outputSelection = JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("window.cmux.selection()"));
            Require(ownsInput && outputSelection == "CLIPBOARD_READY", "The collapsed-input Copy fixture did not establish separate input ownership and a real output match selection.");
            const string previousClipboard = "PRIVATE_CLIPBOARD_MUST_STAY_UNCHANGED";
            clipboard.Text = previousClipboard;
            await window.ClickAction("Copy");
            Console.WriteLine("SEARCH_COPY_OWNER " + JsonSerializer.Serialize(new { ownsInput, outputSelection, expected = previousClipboard, actual = clipboard.Text }));
            Require(clipboard.Text == previousClipboard, "Copy with an unselected search input copied terminal output instead of preserving the clipboard.");
            passed.Add("terminal-find-empty-input-selection-copy-preserves-clipboard-owner");
            Console.WriteLine("passed: " + passed[^1]);

            pane.FocusTerminal();
            await pane.Core.ExecuteScriptAsync("window.cmux.focus()");
            await Program.Until(async () => await pane.Core.ExecuteScriptAsync("document.activeElement===document.querySelector('.xterm-helper-textarea')") == "true", "The owned terminal textarea did not receive focus before terminal Copy.");
            clipboard.Text = previousClipboard;
            await window.ClickAction("Copy");
            Require(clipboard.Text == "CLIPBOARD_READY", "Returning focus to the terminal did not restore Copy of the selected output.");
            passed.Add("terminal-find-copy-focus-return-restores-output-selection");
            Console.WriteLine("passed: " + passed[^1]);

            await Query(pane, "COPY_QUERY_PATTERN");
            await Results(pane, 0);
            await pane.Core.ExecuteScriptAsync("document.getElementById('find-query').setSelectionRange(5,10)");
            clipboard.Text = "previous private clipboard";
            await window.ClickAction("Copy");
            Require(clipboard.Text == "QUERY", "Toolbar Copy ignored the selected search input text.");
            passed.Add("terminal-find-toolbar-copies-search-input-selection");
            Console.WriteLine("passed: " + passed[^1]);
        }
        await Click(pane, "find-close");
    }

    private static async Task NavigationCopy(DesktopWindow window, MemoryClipboard clipboard, List<string> passed)
    {
        var pane = (TerminalPane)window.Current!.Active!;
        await window.ClickAction("Find");
        await Query(pane, "clipboard_ready");
        await Results(pane, 1);
        await pane.Core.ExecuteScriptAsync("document.getElementById('find-query').setSelectionRange(0,5)");
        await Click(pane, "find-next");
        await Program.Until(async () => await pane.Core.ExecuteScriptAsync("document.activeElement.id") == "\"find-next\"", "The owned find navigation button did not receive focus.");
        var owner = JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("document.activeElement.id"));
        var latentQuerySelection = JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("(()=>{const q=document.getElementById('find-query');return q.value.slice(q.selectionStart,q.selectionEnd);})()"));
        var outputSelection = JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("window.cmux.selection()"));
        if (owner != "find-next" || latentQuerySelection != "clipb" || outputSelection != "CLIPBOARD_READY")
            throw new InvalidOperationException("The navigation Copy fixture did not establish separate focused control, latent query selection and actual case-distinct match.");
        clipboard.Text = "PRIVATE_NAVIGATION_CLIPBOARD";
        await window.ClickAction("Copy");
        Console.WriteLine("SEARCH_COPY_NAV " + JsonSerializer.Serialize(new { owner, latentQuerySelection, outputSelection, expected = "CLIPBOARD_READY", actual = clipboard.Text }));
        if (clipboard.Text != "CLIPBOARD_READY")
            throw new InvalidOperationException("Copy from a focused find navigation button used the unfocused query selection instead of the selected output match.");
        passed.Add("terminal-find-navigation-copy-ignores-unfocused-query-selection");
        Console.WriteLine("passed: " + passed[^1]);
        await Click(pane, "find-close");
    }

    private static async Task Query(TerminalPane pane, string text)
    {
        await pane.Core.ExecuteScriptAsync("(()=>{const q=document.getElementById('find-query');q.focus();q.select();})()");
        await pane.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text }));
        if (text.Length == 0) await Key(pane, "Backspace", 8);
    }

    private static async Task Click(TerminalPane pane, string id)
    {
        using var bounds = JsonDocument.Parse(await pane.Core.ExecuteScriptAsync("(()=>{const r=document.getElementById(" + JsonSerializer.Serialize(id) + ").getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()"));
        foreach (var type in new[] { "mousePressed", "mouseReleased" })
            await pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x = bounds.RootElement.GetProperty("x").GetDouble(), y = bounds.RootElement.GetProperty("y").GetDouble(), button = "left", buttons = type == "mousePressed" ? 1 : 0, clickCount = 1 }));
    }
    internal static Task Results(TerminalPane pane, int count) => Program.Until(async () => count == 0
        ? JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("document.getElementById('find-results').textContent")) == new Strings()["FindMissing"]
        : await pane.Core.ExecuteScriptAsync("document.getElementById('find-results').textContent.endsWith(' / '+new Intl.NumberFormat(document.documentElement.lang).format(" + count + "))") == "true", "Find did not produce " + count + " matches.");
    private static async Task Key(TerminalPane pane, string key, int code, bool shift = false)
    {
        foreach (var type in new[] { "keyDown", "keyUp" })
            await pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key, modifiers = shift ? 8 : 0, windowsVirtualKeyCode = code, nativeVirtualKeyCode = code }));
    }
}

using System.Text.Json;
using Cmux.Desktop;

internal static class TerminalSearchScenario
{
    internal static async Task Run(DesktopWindow window, List<string> passed, string? artifacts = null)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Pass(string name) { passed.Add(name); Console.WriteLine("passed: " + name); }
        var first = (TerminalPane)window.Current!.Active!;
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
        await Click(first, "find-case");
        await Results(first, 2);
        await window.ClickAction("Copy"); // Exercises the real selected-match copy path with a private clipboard.
        Pass("terminal-find-scrollback-navigation-and-case");

        await Query(first, "absent_needle_4e917");
        await Results(first, 0);
        Require(await first.Core.ExecuteScriptAsync("window.cmux.selection()") == "\"\"", "An absent query left a stale selection.");
        await Query(first, "");
        await Program.Until(async () => await first.Core.ExecuteScriptAsync("document.getElementById('find-next').disabled") == "true", "An empty query left navigation enabled.");
        Pass("terminal-find-empty-and-missing-clear-selection");

        await Click(first, "find-close");
        await Program.Command(first, "Write-Output ('検索😀'+'一致'); Write-Output ('literal['+'.*]')");
        await Program.Contains(first, "検索😀一致");
        await window.ClickAction("Find");
        await Query(first, "検索😀一致");
        await Results(first, 1);
        Require(JsonSerializer.Deserialize<string>(await first.Core.ExecuteScriptAsync("window.cmux.selection()")) == "検索😀一致", "Unicode search selected different text.");
        await Query(first, "literal[.*]");
        await Results(first, 1);
        Require(await first.Core.ExecuteScriptAsync("window.cmux.selection()") == "\"literal[.*]\"", "Literal search treated regex punctuation as a pattern.");
        // Query text stays in the find input; even command-like text never reaches PowerShell.
        await Query(first, "Write-Output ('SEARCH_INPUT_'+'LEAK')");
        await Results(first, 0);
        Require(!(await Program.Text(first)).Contains("SEARCH_INPUT_"), "Find input reached the shell.");
        Pass("terminal-find-unicode-literal-and-input-isolation");

        await Query(first, "FIND_Alpha");
        await Results(first, 2);
        var firstWorkspace = window.Current!;
        await window.ClickAction("NewWorkspace");
        var other = (TerminalPane)window.Current!.Active!;
        await Program.Command(other, "Write-Output ('OTHER_'+'FIND')");
        await Program.Contains(other, "OTHER_FIND");
        await window.ClickAction("Find");
        await Query(other, "FIND_Alpha");
        await Results(other, 0);
        window.SelectWorkspace(Array.IndexOf(window.Workspaces.ToArray(), firstWorkspace));
        Require(await first.Core.ExecuteScriptAsync("document.getElementById('find-query').value") == "\"FIND_Alpha\"", "Workspace switching lost the pane's search text.");
        await Results(first, 2);
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
        Require(window.Current?.Active == other, "Closing a searched workspace selected the wrong surviving pane.");
        await Program.Command(other, "Write-Output ('SEARCH_CLOSE_'+'SURVIVOR')");
        await Program.Contains(other, "SEARCH_CLOSE_SURVIVOR");
        Pass("terminal-find-dismiss-and-close-preserve-input");
    }

    private static async Task Query(TerminalPane pane, string text)
    {
        await pane.Core.ExecuteScriptAsync("(()=>{const q=document.getElementById('find-query');q.focus();q.select();})()");
        await pane.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text }));
        if (text.Length == 0) await Key(pane, "Backspace", 8);
    }

    private static Task Click(TerminalPane pane, string id) => pane.Core.ExecuteScriptAsync("document.getElementById(" + JsonSerializer.Serialize(id) + ").click()");
    private static Task Results(TerminalPane pane, int count) => Program.Until(async () => count == 0
        ? JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("document.getElementById('find-results').textContent")) == new Strings()["FindMissing"]
        : await pane.Core.ExecuteScriptAsync("document.getElementById('find-results').textContent.endsWith(' / '+new Intl.NumberFormat(document.documentElement.lang).format(" + count + "))") == "true", "Find did not produce " + count + " matches.");
    private static async Task Key(TerminalPane pane, string key, int code)
    {
        foreach (var type in new[] { "keyDown", "keyUp" })
            await pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key, windowsVirtualKeyCode = code, nativeVirtualKeyCode = code }));
    }
}

using System.Text.Json;
using Cmux.Desktop;

internal static class ImeScenario
{
    internal static async Task Run(DesktopWindow window, List<string> passed, string? only = null)
    {
        var pane = (TerminalPane)window.Current!.Active!;
        await Program.Command(pane, "Write-Output ('IME_'+'検索'); Write-Output ('between_'+'matches'); Write-Output ('IME_'+'検索')");
        await Program.Contains(pane, "between_matches");
        await window.ClickAction("Find");
        await pane.Core.ExecuteScriptAsync("document.getElementById('find-query').focus()");
        await pane.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = "IME_検索" }));
        await Program.Until(async () => await pane.Core.ExecuteScriptAsync("document.getElementById('find-results').textContent.endsWith(' / 2')") == "true", "IME key fixture did not establish two search matches.");
        await pane.Core.ExecuteScriptAsync("window.imeKeys=[];window.addEventListener('keydown',e=>window.imeKeys.push({key:e.key,keyCode:e.keyCode,isComposing:e.isComposing,prevented:e.defaultPrevented}))");
        if (only is null or "--ime-enter")
        {
            var before = await Position(pane);
            await Key(pane, "Enter", 229);
            Console.WriteLine("IME_ENTER_TRACE " + await pane.Core.ExecuteScriptAsync("window.imeKeys"));
            Require(await Position(pane) == before, "IME-processed Enter navigated to another search match.");
            Require(await pane.Core.ExecuteScriptAsync("document.activeElement.id") == "\"find-query\"", "IME-processed Enter left search input.");
            await Key(pane, "Enter", 13);
            await Program.Until(async () => await Position(pane) != before, "Ordinary Enter stopped navigating after an IME key.");
            await Key(pane, "Enter", 13, shift: true);
            await Program.Until(async () => await Position(pane) == before, "Ordinary Shift+Enter stopped navigating after an IME key.");
            passed.Add("ime-processed-enter-preserves-search-selection");
            Console.WriteLine("passed: " + passed[^1]);
        }
        if (only is null or "--ime-escape")
        {
            await Key(pane, "Escape", 229);
            Console.WriteLine("IME_ESCAPE_TRACE " + await pane.Core.ExecuteScriptAsync("window.imeKeys"));
            Require(await pane.Core.ExecuteScriptAsync("!document.getElementById('find-bar').hidden && document.activeElement.id==='find-query'") == "true", "IME-processed Escape dismissed search instead of leaving IME cancellation to the input.");
            await Key(pane, "Escape", 27);
            await Program.Until(async () => await pane.Core.ExecuteScriptAsync("document.getElementById('find-bar').hidden && document.activeElement===term.textarea") == "true", "Ordinary Escape stopped dismissing search after an IME key.");
            passed.Add("ime-processed-escape-preserves-search-focus");
            Console.WriteLine("passed: " + passed[^1]);
        }
        else await pane.Core.ExecuteScriptAsync("document.getElementById('find-close').click()");
    }

    private static Task<string> Position(TerminalPane pane) => pane.Core.ExecuteScriptAsync("JSON.stringify(term.getSelectionPosition())");

    private static async Task Key(TerminalPane pane, string key, int code, bool shift = false)
    {
        foreach (var type in new[] { "keyDown", "keyUp" })
            await pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key, code = key, windowsVirtualKeyCode = code, nativeVirtualKeyCode = code, modifiers = shift ? 8 : 0 }));
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

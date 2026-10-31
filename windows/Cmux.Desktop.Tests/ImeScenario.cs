using System.Text.Json;
using System.Text;
using System.Diagnostics;
using System.Globalization;
using Cmux.Desktop;

internal static class ImeScenario
{
    internal static async Task Run(DesktopWindow window, List<string> passed, string? only = null, string? artifacts = null, string shell = "pwsh")
    {
        void Pass(string name) { passed.Add(name + "-" + shell); Console.WriteLine("passed: " + passed[^1]); }
        var pane = (TerminalPane)window.Current!.Active!;
        await Program.Command(pane, "Write-Output ('IME_'+'検索'); Write-Output ('between_'+'matches'); Write-Output ('IME_'+'検索')");
        await Program.Contains(pane, "between_matches");
        await window.ClickAction("Find");
        await pane.Core.ExecuteScriptAsync("document.getElementById('find-query').focus()");
        await pane.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = "IME_検索" }));
        await TerminalSearchScenario.Results(pane, 2);
        await BoundaryKeys(pane, only, Pass);
        if (only is null)
        {
            await Composition(window, pane, Pass, artifacts, shell);
            await LocaleBoundary(window, pane, Pass, artifacts, shell);
        }
    }

    private static async Task BoundaryKeys(TerminalPane pane, string? only, Action<string> pass)
    {
        await pane.Core.ExecuteScriptAsync("window.imeKeys=[];window.addEventListener('keydown',e=>window.imeKeys.push({key:e.key,keyCode:e.keyCode,isComposing:e.isComposing,prevented:e.defaultPrevented}))");
        if (only is null or "--ime-enter" or "--ime-keys")
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
            pass("ime-processed-enter-preserves-search-selection");
        }
        if (only is null or "--ime-escape" or "--ime-keys")
        {
            await Key(pane, "Escape", 229);
            Console.WriteLine("IME_ESCAPE_TRACE " + await pane.Core.ExecuteScriptAsync("window.imeKeys"));
            Require(await pane.Core.ExecuteScriptAsync("!document.getElementById('find-bar').hidden && document.activeElement.id==='find-query'") == "true", "IME-processed Escape dismissed search instead of leaving IME cancellation to the input.");
            await Key(pane, "Escape", 27);
            await Program.Until(async () => await pane.Core.ExecuteScriptAsync("document.getElementById('find-bar').hidden && document.activeElement===term.textarea") == "true", "Ordinary Escape stopped dismissing search after an IME key.");
            pass("ime-processed-escape-preserves-search-focus");
        }
        else await pane.Core.ExecuteScriptAsync("document.getElementById('find-close').click()");
    }

    private static async Task LocaleBoundary(DesktopWindow window, TerminalPane pane, Action<string> pass, string? artifacts, string shell)
    {
        var previous = JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("document.documentElement.lang"))!;
        try
        {
            await ConfigureLocale(pane, "ar-EG");
            await window.ClickAction("Find");
            await pane.Core.ExecuteScriptAsync("(()=>{const q=document.getElementById('find-query');q.focus();q.select();})()");
            await pane.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = "IME_検索" }));
            await TerminalSearchScenario.Results(pane, 2);
            Console.WriteLine("IME_FIND_LOCALE " + await pane.Core.ExecuteScriptAsync("({locale:document.documentElement.lang,count:document.getElementById('find-results').textContent})"));
            await Capture(pane, artifacts, "ime-arabic-" + shell);
            await BoundaryKeys(pane, "--ime-keys", name => pass(name + "-ar-EG"));
        }
        finally { await ConfigureLocale(pane, previous); }
    }

    private static async Task ConfigureLocale(TerminalPane pane, string locale)
    {
        using var resource = typeof(DesktopWindow).Assembly.GetManifestResourceStream("Cmux.Desktop.Strings.json")!;
        var catalogs = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(resource)!;
        var localized = new Strings(CultureInfo.GetCultureInfo(locale));
        var values = catalogs["en"].Keys.ToDictionary(key => key, key => localized[key]);
        // Use the existing native configure message, without changing the OS locale.
        pane.Core.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "configure", locale, strings = values, cwdLimit = WorkingDirectoryReport.MaxUriCharacters }));
        await Program.Until(async () => JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("document.documentElement.lang")) == locale, "The renderer did not apply the fixture locale.");
    }

    internal static async Task Fallback(ShellLaunch launch, List<string> passed, string? artifacts = null)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        Require(File.Exists(executable), "The IME Windows PowerShell fixture is missing.");
        using var window = new DesktopWindow(launch with { Executable = executable }, new MemoryClipboard()) { ShowInTaskbar = false };
        Process? process = null;
        var profile = "";
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = window.ProfilePath;
            process = Process.GetProcessById(((TerminalPane)window.Current!.Active!).ProcessId);
            _ = process.SafeHandle;
            await Run(window, passed, artifacts: artifacts, shell: "windows-powershell");
            Require(window.LastError is null, "Windows PowerShell IME fixture reported a UI error.");
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            try { Require(process is null || process.WaitForExit(0), "The original IME fallback shell survived shutdown."); }
            finally { process?.Dispose(); }
            Require(profile.Length == 0 || !Directory.Exists(profile), "The IME fallback profile was not removed.");
        }
    }

    private static async Task Composition(DesktopWindow window, TerminalPane pane, Action<string> pass, string? artifacts, string shell)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var prompt = "IME_INPUT_" + nonce;
        var result = "IME_RESULT_" + nonce;
        await Program.Command(pane, "$answer=Read-Host '" + prompt + "'; [Console]::WriteLine('" + result + "|'+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($answer))+'|DONE')");
        await Program.Contains(pane, prompt + ":");
        await pane.Core.ExecuteScriptAsync("window.imeData=[];window.imeSubscription=term.onData(data=>window.imeData.push(data));window.imeComposition=[];term.textarea.addEventListener('compositionstart',e=>window.imeComposition.push({type:e.type,trusted:e.isTrusted}));term.textarea.addEventListener('compositionend',e=>window.imeComposition.push({type:e.type,trusted:e.isTrusted}));term.textarea.focus()");
        try
        {
            await SetComposition(pane, "に");
            await SetComposition(pane, "日本語😀");
            await Frame(pane);
            await Capture(pane, artifacts, "ime-terminal-" + shell);
            Require(await pane.Core.ExecuteScriptAsync("window.imeData.join('')") == "\"\"", "Uncommitted IME candidate text reached the shell.");
            var committed = "日本語😀";
            await pane.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = committed }));
            await Program.Until(async () => JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("window.imeData.join('')")) == committed, "IME commit was missing, duplicated or included an intermediate candidate.");
            await Key(pane, "Enter", 13);
            var expected = result + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(committed)) + "|DONE";
            await Program.Until(async () => (await Program.Text(pane)).Replace("\r", "").Replace("\n", "").Contains(expected, StringComparison.Ordinal), "The shell did not receive the exact single IME commit.");
            Console.WriteLine("IME_COMPOSITION_TRACE " + await pane.Core.ExecuteScriptAsync("window.imeComposition"));
            // CDP emulates composition; event trust varies with the commit path.
            // Require the lifecycle and verify the actual committed bytes above.
            Require(await pane.Core.ExecuteScriptAsync("window.imeComposition.filter(e=>e.type==='compositionstart').length===1 && window.imeComposition.filter(e=>e.type==='compositionend').length===1") == "true", "The IME fixture did not exercise Chromium's composition lifecycle.");
            pass("ime-chromium-terminal-commit-is-exactly-once");

            await Program.Until(async () => (await Program.Text(pane)).TrimEnd().EndsWith("CMUX_TEST_READY>", StringComparison.Ordinal), "The IME fixture did not return to its prompt.");
            await pane.Core.ExecuteScriptAsync("window.imeData=[];term.textarea.focus()");
            await SetComposition(pane, "取り消し");
            await SetComposition(pane, "");
            await Frame(pane);
            Require(await pane.Core.ExecuteScriptAsync("window.imeData.join('')") == "\"\"", "Cancelled IME text reached the shell.");
            await Program.Command(pane, "Write-Output ('IME_CANCEL_'+'SURVIVOR')");
            await Program.Contains(pane, "IME_CANCEL_SURVIVOR");
            pass("ime-chromium-terminal-cancel-keeps-shell-clean");

            await window.ClickAction("Find");
            await pane.Core.ExecuteScriptAsync("(()=>{const q=document.getElementById('find-query');q.focus();q.select();window.imeData=[];})()");
            await SetComposition(pane, "に");
            await SetComposition(pane, committed);
            await Key(pane, "Enter", 229);
            await Key(pane, "Escape", 229);
            Require(await pane.Core.ExecuteScriptAsync("!document.getElementById('find-bar').hidden && document.activeElement.id==='find-query' && document.getElementById('find-next').disabled") == "true", "An active search composition lost focus or enabled intermediate search.");
            await pane.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = committed }));
            await Program.Until(async () => JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("window.cmux.selection()")) == committed, "Search did not match the final committed Japanese text.");
            Require(JsonSerializer.Deserialize<string>(await pane.Core.ExecuteScriptAsync("document.getElementById('find-query').value")) == committed, "Search changed the committed IME query.");
            Require(await pane.Core.ExecuteScriptAsync("window.imeData.join('')") == "\"\"", "Search composition leaked into the shell input.");
            await Capture(pane, artifacts, "ime-search-" + shell);
            await Key(pane, "Escape", 27);
            pass("ime-chromium-search-commit-stays-local");
        }
        finally { await pane.Core.ExecuteScriptAsync("window.imeSubscription.dispose()"); }
    }

    private static Task<string> SetComposition(TerminalPane pane, string text) => pane.Core.CallDevToolsProtocolMethodAsync("Input.imeSetComposition", JsonSerializer.Serialize(new { text, selectionStart = text.Length, selectionEnd = text.Length }));

    private static Task<string> Frame(TerminalPane pane) => pane.Core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = "new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))", awaitPromise = true })).WaitAsync(TimeSpan.FromSeconds(30));

    private static async Task Capture(TerminalPane pane, string? artifacts, string name)
    {
        if (artifacts is null) return;
        Directory.CreateDirectory(artifacts);
        await using var image = File.Create(Path.Combine(artifacts, name + ".png"));
        await pane.Core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, image);
    }

    private static Task<string> Position(TerminalPane pane) => pane.Core.ExecuteScriptAsync("JSON.stringify(term.getSelectionPosition())");

    private static async Task Key(TerminalPane pane, string key, int code, bool shift = false)
    {
        foreach (var type in new[] { "keyDown", "keyUp" })
            await pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key, code = key, windowsVirtualKeyCode = code, nativeVirtualKeyCode = code, modifiers = shift ? 8 : 0 }));
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

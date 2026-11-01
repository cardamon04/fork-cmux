using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cmux.Desktop;
using Microsoft.Web.WebView2.Core;

internal static class TerminalFontSizeScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed, string? artifacts)
    {
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TerminalFontSize.Parser.cjs"));
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Assets"));
        using (var parser = Process.Start(start)!)
        {
            _ = parser.SafeHandle;
            var stdout = parser.StandardOutput.ReadToEndAsync();
            var stderr = parser.StandardError.ReadToEndAsync();
            try
            {
                await parser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(parser.ExitCode == 0, "The production font-size parser contract failed: " + await stderr);
                Console.WriteLine("FONT_SIZE_PARSER " + await stdout);
            }
            finally { if (!parser.HasExited) { parser.Kill(); await parser.WaitForExitAsync(); } }
        }
        Pass(passed, "terminal-font-size-pinned-renderer-contract");
        foreach (var locale in new[] { "en", "ja", "de", "fr", "ar", "es", "zh-Hant", "zh-Hans", "ko" })
        {
            var strings = new Strings(CultureInfo.GetCultureInfo(locale));
            var labels = Keys.Select(key => strings[key]).ToArray();
            Require(labels.All(label => !string.IsNullOrWhiteSpace(label)) && labels.Distinct().Count() == 3, "Font actions lack distinct localized labels: " + locale);
        }
        Pass(passed, "terminal-font-size-nine-locale-actions");
        await Integration(source, "pwsh", passed, artifacts);
        await Integration(source with { Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") }, "windows-powershell", passed, artifacts);
    }

    private static readonly string[] Keys = ["IncreaseFontSize", "DecreaseFontSize", "ResetFontSize"];
    private static async Task Integration(ShellLaunch source, string shell, List<string> passed, string? artifacts)
    {
        var root = Directory.CreateTempSubdirectory("cmux-font-size-").FullName;
        using var window = new DesktopWindow(source with { Directory = root }, new MemoryClipboard()) { ShowInTaskbar = false };
        var processes = new List<Process>();
        string? profile = null, browserProfile = null;
        void Track(TerminalPane pane) { var process = Process.GetProcessById(pane.ProcessId); _ = process.SafeHandle; processes.Add(process); }
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = window.ProfilePath;
            var first = (TerminalPane)window.Current!.Active!;
            Track(first);
            await Program.Contains(first, "CMUX_TEST_READY>");
            var toolbar = window.Controls.OfType<ToolStrip>().First(strip => strip is not StatusStrip);
            Require(Keys.All(key => toolbar.Items[key] is { Enabled: true }), "The selected terminal has no usable font-size actions.");
            await window.ClickAction("SplitRight");
            var second = (TerminalPane)window.Current!.Active!;
            Track(second);
            await Program.Contains(second, "CMUX_TEST_READY>");
            first.FocusTerminal();
            await Program.Command(first, "Write-Output ('FONT_' + 'BUFFER_日本語🍀')");
            await Program.Contains(first, "FONT_BUFFER_日本語🍀");
            var before = await Size(first);
            Require(before.Font == 14, "A new renderer did not use the existing default font size.");
            await window.ClickAction("IncreaseFontSize");
            await Program.Until(async () => (await Size(first)).Font == 15, "Increase did not reach the selected renderer.");
            await window.ClickAction("DecreaseFontSize");
            await Program.Until(async () => (await Size(first)).Font == 14, "Decrease did not reverse the selected renderer change.");
            for (var i = 0; i < 10; i++) await window.ClickAction("IncreaseFontSize");
            await Program.Until(async () => { var size = await Size(first); return size.Font == 24 && size.Cols < before.Cols && size.Rows < before.Rows; }, "Larger text did not refit the renderer grid.");
            var enlarged = await Size(first);
            await Program.Command(first, "Write-Output ('FONT_' + 'DIMS:' + $Host.UI.RawUI.WindowSize.Width + ':' + $Host.UI.RawUI.WindowSize.Height)");
            await Program.Contains(first, $"FONT_DIMS:{enlarged.Cols}:{enlarged.Rows}");
            await Program.Contains(first, "FONT_BUFFER_日本語🍀");
            Require(await first.Core.ExecuteScriptAsync("document.activeElement === term.textarea") == "true", "The shared font action did not restore the owned terminal input focus.");
            Console.WriteLine("FONT_SIZE_RESIZE " + JsonSerializer.Serialize(new { shell, before.Font, before.Cols, before.Rows, enlarged = new { enlarged.Font, enlarged.Cols, enlarged.Rows }, shellGrid = true }));
            Pass(passed, "terminal-font-size-renderer-and-conpty-resize-" + shell);

            Require((await Size(second)).Font == 14, "Selected-pane font size leaked into its neighbor.");
            await window.ClickAction("OpenBrowser");
            var browser = (BrowserPane)window.Current!.Active!;
            browserProfile = window.BrowserProfilePath;
            Require(Keys.All(key => !window.IsActionEnabled(key)), "Terminal font actions were enabled for a browser.");
            await window.ClickAction("IncreaseFontSize");
            Require((await Size(first)).Font == 24 && (await Size(second)).Font == 14, "A disabled browser action changed terminal state.");
            first.FocusTerminal();
            await window.ClickAction("MovePaneToNewWorkspace");
            Require(window.Current!.Active == first && (await Size(first)).Font == 24, "Moving a live pane replaced its font size or identity.");
            window.SelectWorkspace(0);
            second.FocusTerminal();
            Require((await Size(second)).Font == 14, "Workspace switching leaked the selected font size.");
            window.SelectWorkspace(1);
            first.FocusTerminal();
            Pass(passed, "terminal-font-size-pane-workspace-browser-isolation-" + shell);

            await Program.Until(async () => (await Program.Text(first)).TrimEnd().EndsWith("CMUX_TEST_READY>", StringComparison.Ordinal), "The zoomed private shell was not ready.");
            await first.Core.ExecuteScriptAsync("window.cmux.focus()");
            await first.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = "Write-Output ('FONT_' + 'PENDING_日本語🍀')" }));
            await window.ClickAction("ResetFontSize");
            await Program.Until(async () => (await Size(first)).Font == 14, "Reset did not restore the initial font size.");
            Require(await first.Core.ExecuteScriptAsync("document.activeElement === term.textarea") == "true", "Reset stole input focus.");
            foreach (var type in new[] { "keyDown", "keyUp" })
                await first.Core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, text = type == "keyDown" ? "\r" : "" }));
            await Program.Contains(first, "FONT_PENDING_日本語🍀");
            await window.ClickAction("ResetFontSize");
            Require((await Size(first)).Font == 14, "Repeated reset changed the default font size.");
            if (artifacts is not null)
            {
                Directory.CreateDirectory(artifacts);
                using var image = File.Create(Path.Combine(artifacts, "terminal-font-size-" + shell + ".png"));
                await first.Core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            Pass(passed, "terminal-font-size-reset-preserves-pending-unicode-input-" + shell);
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var process in processes) { Require(process.WaitForExit(0), "An original font-size fixture shell survived shutdown."); process.Dispose(); }
            foreach (var path in new[] { profile, browserProfile }.OfType<string>()) Require(!Directory.Exists(path), "The font-size fixture profile survived shutdown.");
            var full = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(full).StartsWith("cmux-font-size-", StringComparison.Ordinal), "Unexpected fixture cleanup path.");
            Directory.Delete(full, recursive: true);
        }
    }
    private static async Task<(int Font, int Cols, int Rows)> Size(TerminalPane pane)
    {
        using var data = JsonDocument.Parse(await pane.Core.ExecuteScriptAsync("({font: term.options.fontSize, ...window.cmux.size()})"));
        return (data.RootElement.GetProperty("font").GetInt32(), data.RootElement.GetProperty("cols").GetInt32(), data.RootElement.GetProperty("rows").GetInt32());
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pass(List<string> passed, string name) { passed.Add(name); Console.WriteLine("passed: " + name); }
}

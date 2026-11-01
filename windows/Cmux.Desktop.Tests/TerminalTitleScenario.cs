using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Cmux.Desktop;
using Cmux.Terminal;

internal static class TerminalTitleScenario
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Pass(List<string> passed, string name) { passed.Add(name); Console.WriteLine("passed: " + name); }

    // This selector is safe during unrelated desktop use: no Form/WebView,
    // global input, foreground/focus, OS clipboard or browser access.
    internal static async Task Headless(List<string> passed)
    {
        var state = new TerminalTitle();
        const string literal = " 日本語 & 🍀 <tag> {0} \"title\" ";
        Require(state.Value == "" && state.Update(literal) && state.Value == literal, "Live title did not preserve literal Unicode data.");
        Require(!state.Update(literal), "Duplicate title was republished.");
        foreach (var invalid in new string?[] { null, " ", "\0", "line\nline", "\u001b[31m", "\ud83c", "\udf40", new('x', 513) })
            Require(!state.Update(invalid) && state.Value == literal, "Invalid title replaced the previous live value.");
        var boundary = string.Concat(Enumerable.Repeat("🍀", 256));
        Require(state.Update(boundary) && state.Value == boundary, "The exact title limit split a supplementary character.");
        Require(state.Update("\ufeff") && state.Value == "\ufeff", "BOM was incorrectly classified as whitespace-only title data.");
        Require(state.Update("") && state.Value == "" && !state.Update(""), "Clearing title metadata did not reset idempotently.");
        Pass(passed, "terminal-title-bounded-literal-model-and-clear");
        foreach (var locale in new[] { "en", "ja", "de", "fr", "ar", "es", "zh-Hant", "zh-Hans", "ko" })
        {
            var strings = new Strings(CultureInfo.GetCultureInfo(locale));
            Require(TerminalTitle.Caption(strings, "") == strings["AppTitle"], "Empty title did not use the existing localized app caption.");
            Require(TerminalTitle.Caption(strings, literal) == literal + " — " + strings["AppTitle"], "A localized title format changed literal data.");
        }
        Pass(passed, "terminal-title-nine-locale-caption-format");
        await Node();
        Pass(passed, "terminal-title-real-parser-and-coalescing-contract");
        var source = ShellLaunch.Default();
        foreach (var shell in new[] { (Name: "pwsh", Executable: source.Executable), (Name: "windows-powershell", Executable: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) })
        {
            var root = Directory.CreateTempSubdirectory("cmux-title-core-").FullName;
            try
            {
                var expected = "ConPTY 日本語 & 🍀 " + shell.Name;
                var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(expected));
                var marker = "CMUX_TITLE_READY_" + Guid.NewGuid().ToString("N");
                var script = "$ErrorActionPreference='Stop'; if(-not $PSVersionTable.PSVersion.ToString().StartsWith('" + (shell.Name == "pwsh" ? "7." : "5.1")
                    + "')) { throw 'Wrong private title producer runtime' }; [Console]::Title=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + value + "')); [Console]::WriteLine('" + marker + "'); [Console]::ReadLine() | Out-Null";
                var output = new StringBuilder();
                var gate = new object();
                string Captured() { lock (gate) return output.ToString(); }
                using var session = ConPtySession.Start(shell.Executable, ["-NoLogo", "-NoProfile", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], root, 100, 30,
                    data => { lock (gate) { if (output.Length + data.Length > 65536) throw new InvalidDataException(); output.Append(data); } });
                try
                {
                    await Program.Until(() => Task.FromResult(Captured().Contains(marker, StringComparison.Ordinal)), "The owned background title producer did not become ready.");
                    var path = Path.Combine(root, "capture.json");
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { output = Captured(), expected }));
                    using var replay = JsonDocument.Parse(await Node("--replay", path));
                    var title = replay.RootElement.GetProperty("title").GetString();
                    var model = new TerminalTitle();
                    Require(model.Update(title) && model.Value == expected, "The actual background ConPTY/parser/native title chain changed text.");
                    Console.WriteLine("TERMINAL_TITLE_TRANSPORT " + JsonSerializer.Serialize(new { shell = shell.Name, expected, actual = model.Value, scope = "owned background ConPTY; no GUI or input devices" }));
                }
                finally
                {
                    try { session.Write("\r"); } // Only this private ConPTY input pipe.
                    catch (IOException) { } // A failed producer may already have closed it.
                    Require(session.WaitForExit(TimeSpan.FromSeconds(10)) == 0, "The original background title process did not exit successfully.");
                }
                Pass(passed, "terminal-title-background-conpty-parser-native-" + shell.Name);
            }
            finally { Delete(root, "cmux-title-core-"); }
        }
    }

    private static async Task<string> Node(params string[] extra)
    {
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TerminalTitle.Parser.cjs"));
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Assets"));
        foreach (var value in extra) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        _ = process.SafeHandle;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var stdout = await output;
            var stderr = await error;
            Require(process.ExitCode == 0, "The real pinned parser contract failed: " + stderr);
            return stdout;
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }

    // GUI entrypoint: run only on an isolated hosted runner while the local
    // desktop is reserved for the user, or after that restriction is lifted.
    internal static async Task Run(ShellLaunch source, List<string> passed, string? artifacts = null)
    {
        await Headless(passed);
        foreach (var shell in new[] { (Name: "pwsh", Executable: source.Executable), (Name: "windows-powershell", Executable: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) })
        {
            var root = Directory.CreateTempSubdirectory("cmux-title-gui-").FullName;
            var history = Path.Combine(root, "history.txt").Replace("'", "''", StringComparison.Ordinal);
            var launch = source with { Executable = shell.Executable, Directory = root,
                Arguments = ShellLaunch.PowerShellArguments("Set-PSReadLineOption -HistorySaveStyle SaveNothing -HistorySavePath '" + history + "'; function global:prompt { 'CMUX_TEST_READY> ' }", noProfile: true) };
            using var store = new SessionStore(Path.Combine(root, "layout.json"));
            using var window = new DesktopWindow(launch, new MemoryClipboard(), store) { ShowInTaskbar = false };
            Process? original = null;
            string? profile = null;
            try
            {
                window.Show();
                await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
                profile = window.ProfilePath;
                var pane = (TerminalPane)window.Current!.Active!;
                original = Process.GetProcessById(pane.ProcessId); _ = original.SafeHandle;
                await Program.Contains(pane, "CMUX_TEST_READY>");
                Require(window.Current.TryRename("manual 日本語 & name"), "Manual workspace title precondition failed.");
                await window.ClickAction("SaveSession");
                var saved = File.ReadAllBytes(Path.Combine(root, "layout.json"));
                var literal = "日本語 & 🍀 <tag> {0} " + shell.Name;
                var titleGate = Path.Combine(root, "selected-title.release");
                await SetTitle(pane, literal, titleGate);
                await window.ClickAction("Find");
                await pane.Core.ExecuteScriptAsync("document.getElementById('find-query').value='keep focus';document.getElementById('find-query').focus();document.getElementById('find-query').setSelectionRange(2,5)");
                Require(pane.Title != literal && !File.Exists(titleGate), "The selected title arrived before its causal release.");
                Require(JsonSerializer.Deserialize<bool>(await pane.Core.ExecuteScriptAsync("document.activeElement.id==='find-query'&&document.activeElement.selectionStart===2&&document.activeElement.selectionEnd===5")), "The find focus/selection precondition was not established.");
                await File.WriteAllTextAsync(titleGate, "release");
                await Program.Until(() => Task.FromResult(pane.Title == literal), "The real shell title did not reach its pane.");
                var expected = TerminalTitle.Caption(new Strings(), literal);
                Require(window.Text == expected && NativeCaption(window) == expected, "The selected title did not reach the actual native window caption.");
                Require(JsonSerializer.Deserialize<bool>(await pane.Core.ExecuteScriptAsync("document.activeElement.id==='find-query'&&document.activeElement.selectionStart===2&&document.activeElement.selectionEnd===5")), "Title arrival stole the existing find focus/selection.");
                Require(window.Current.Active == pane && window.Current.Title == "manual 日本語 & name", "Title arrival changed selected pane or manual workspace name.");
                Require(File.ReadAllBytes(Path.Combine(root, "layout.json")).SequenceEqual(saved), "Title arrival implicitly wrote session state.");
                await window.ClickAction("SaveSession");
                Require(File.ReadAllBytes(Path.Combine(root, "layout.json")).SequenceEqual(saved), "Saving live title expanded stored state or changed the manual name.");
                if (artifacts is not null)
                {
                    Directory.CreateDirectory(artifacts);
                    OwnedWindows.Capture(window.Handle, Path.Combine(artifacts, "terminal-title-" + shell.Name + ".png"));
                }
                Console.WriteLine("TERMINAL_TITLE_NATIVE " + JsonSerializer.Serialize(new { shell = shell.Name, expected, actual = NativeCaption(window), manualTitle = window.Current.Title, privateSessionUnchanged = true }));
                Pass(passed, "terminal-title-native-caption-focus-and-storage-" + shell.Name);

                await pane.Core.ExecuteScriptAsync("document.getElementById('find-close').click()");
                var backgroundGate = Path.Combine(root, "background-title.release");
                await SetTitle(pane, "background " + shell.Name, backgroundGate);
                await window.ClickAction("SplitRight");
                var second = (TerminalPane)window.Current!.Active!;
                await Program.Contains(second, "CMUX_TEST_READY>");
                Require(window.Current.Active == second && pane.Title == literal && !File.Exists(backgroundGate), "The background-title isolation precondition was not established.");
                await File.WriteAllTextAsync(backgroundGate, "release");
                await Program.Until(() => Task.FromResult(pane.Title == "background " + shell.Name), "The non-selected pane did not retain its own live title.");
                Require(window.Current.Active == second && window.Text == TerminalTitle.Caption(new Strings(), second.Title), "A background title changed selection or the selected caption.");
                await window.ClickAction("MovePaneToNewWorkspace");
                Require(window.Current!.Active == second && window.Text == TerminalTitle.Caption(new Strings(), second.Title), "Moving a live pane lost its selected caption.");
                window.SelectWorkspace(0);
                Require(window.Current!.Active == pane && window.Text == TerminalTitle.Caption(new Strings(), pane.Title), "Workspace selection did not recover the original live title.");
                await window.ClickAction("OpenBrowser");
                Require(window.Current.Active is BrowserPane && window.Text == new Strings()["AppTitle"], "Browser selection kept a stale terminal caption.");
                Pass(passed, "terminal-title-pane-workspace-move-and-browser-isolation-" + shell.Name);
            }
            finally
            {
                await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
                if (original is not null) { Require(original.WaitForExit(0), "The original title fixture shell survived shutdown."); original.Dispose(); }
                if (profile is not null) Require(!Directory.Exists(profile), "The title fixture profile survived shutdown.");
                window.Dispose(); store.Dispose();
                Delete(root, "cmux-title-gui-");
            }
        }
    }

    private static async Task SetTitle(TerminalPane pane, string value, string releasePath)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        var gate = releasePath.Replace("'", "''", StringComparison.Ordinal);
        await Program.Command(pane, "$titleDeadline=[DateTime]::UtcNow.AddSeconds(30); [IO.File]::WriteAllText('" + gate + ".ready','ready'); "
            + "while(-not [IO.File]::Exists('" + gate + "')) { if([DateTime]::UtcNow -gt $titleDeadline) { throw 'Private title gate timed out' }; Start-Sleep -Milliseconds 20 }; "
            + "[Console]::Title=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded + "'))");
        await Program.Until(() => Task.FromResult(File.Exists(releasePath + ".ready")), "The owned shell did not enter its private title gate.");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maximum);
    private static string NativeCaption(Form window) { var value = new StringBuilder(2048); GetWindowTextW(window.Handle, value, value.Capacity); return value.ToString(); }
    private static void Delete(string root, string prefix)
    {
        var full = Path.GetFullPath(root);
        Require(Path.GetDirectoryName(full) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            && Path.GetFileName(full).StartsWith(prefix, StringComparison.Ordinal), "Unowned title fixture cleanup path.");
        Directory.Delete(full, true);
    }
}

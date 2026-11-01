using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cmux.Desktop;
using static Program;

internal static class UnicodeInputScenario
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pass(List<string> passed, string name)
    {
        passed.Add(name);
        Console.WriteLine("passed: " + name);
    }

    internal static async Task Regression(ShellLaunch source, List<string> passed, string? artifacts = null)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        await using var fixture = new Fixture(source, executable);
        await fixture.Start();
        var pane = fixture.Pane;
        var version = await fixture.Frame("$PSVersionTable.PSVersion.ToString()");
        Require(version.StartsWith("5.1", StringComparison.Ordinal), "The Unicode regression requires Windows PowerShell 5.1.");
        var module = await fixture.Frame("(Get-Module PSReadLine).Version.ToString()");
        var mode = await fixture.Frame("$env:PSREADLINE_VTINPUT");
        var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "changed 日本語 & 🍀")).FullName;
        var command = "Set-Location -LiteralPath '" + target.Replace("'", "''", StringComparison.Ordinal) + "'";
        await pane.Core.ExecuteScriptAsync("window.__unicodePackets=[];window.__unicodeObserver=term.onData(data=>window.__unicodePackets.push(data));");
        await fixture.Command(command);
        var packets = JsonSerializer.Deserialize<string[]>(await pane.Core.ExecuteScriptAsync("window.__unicodePackets"))!;
        await pane.Core.ExecuteScriptAsync("window.__unicodeObserver.dispose()");
        var history = await fixture.Frame("(Get-History -Count 1).CommandLine");
        var actual = await fixture.Frame("(Get-Location).ProviderPath");
        Console.WriteLine("UNICODE_INPUT_TRACE " + JsonSerializer.Serialize(new { version, module, mode, command, packets, history, expected = target, actual, reported = pane.WorkingDirectory }));
        Require(history == command, "Windows PowerShell prompt input lost or changed a supplementary Unicode character before engine history.");
        Require(actual == target, "The interactive shell did not enter the exact supplementary-character directory.");
        await Until(() => Task.FromResult(pane.WorkingDirectory == target), "The exact Unicode directory did not reach the selected pane.");
        if (artifacts is not null)
        {
            Directory.CreateDirectory(artifacts);
            using var bitmap = new Bitmap(fixture.Window.Width, fixture.Window.Height);
            fixture.Window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, fixture.Window.Size));
            bitmap.Save(Path.Combine(artifacts, "powershell51-unicode-input.png"));
        }
        Pass(passed, "unicode-input-windows-powershell-raw-supplementary-cwd");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("cmux-unicode-input-").FullName;
        internal DesktopWindow Window { get; }
        internal TerminalPane Pane => (TerminalPane)Window.Current!.Active!;
        private Process? process;
        private string? profile;
        private int prompt = 1;

        internal Fixture(ShellLaunch source, string executable)
        {
            var history = Path.Combine(Root, "history.txt").Replace("'", "''", StringComparison.Ordinal);
            var initialization = "$env:PSREADLINE_VTINPUT=$null; Set-PSReadLineOption -HistorySaveStyle SaveNothing -HistorySavePath '" + history
                + "'; $global:__cmuxUnicodePrompt=0; function global:prompt { $global:__cmuxUnicodePrompt++; 'CMUX_UNICODE_'+$global:__cmuxUnicodePrompt.ToString('D6')+'> ' }";
            var launch = source with { Executable = executable, Arguments = ShellLaunch.PowerShellArguments(initialization, noProfile: true), Directory = Root };
            Window = new DesktopWindow(launch, new MemoryClipboard()) { ShowInTaskbar = false };
        }

        internal async Task Start()
        {
            Window.Show();
            await Window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = Window.ProfilePath;
            process = Process.GetProcessById(Pane.ProcessId);
            _ = process.SafeHandle;
            await Ready();
        }

        private string Prompt => "CMUX_UNICODE_" + prompt.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ">";
        private Task Ready() => Until(async () => (await Text(Pane)).TrimEnd().EndsWith(Prompt, StringComparison.Ordinal), "The exact Unicode fixture prompt did not complete.");

        internal async Task Command(string command)
        {
            await Ready();
            Pane.FocusTerminal();
            await Pane.Core.ExecuteScriptAsync("window.cmux.focus()");
            await Pane.Core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = command }));
            foreach (var type in new[] { "keyDown", "keyUp" })
                await Pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, text = type == "keyDown" ? "\r" : "" }));
            prompt++;
            await Ready();
        }

        internal async Task<string> Frame(string expression)
        {
            var prefix = "UNICODE_FRAME_" + Guid.NewGuid().ToString("N") + "|";
            var suffix = "|UNICODE_END";
            await Command("Write-Output ('" + prefix + "'+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string](" + expression + ")))+'" + suffix + "')");
            string? decoded = null;
            await Until(async () =>
            {
                var text = (await Text(Pane)).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
                var matches = Regex.Matches(text, Regex.Escape(prefix) + "([A-Za-z0-9+/]*={0,2})" + Regex.Escape(suffix));
                if (matches.Count == 0) return false;
                decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(matches[^1].Groups[1].Value));
                return true;
            }, "The Unicode fixture did not return a complete value frame.");
            return decoded!;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                var original = Window.Current?.Active;
                await Window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
                if (process is not null) Require(process.WaitForExit(5000), "The original Unicode fixture process survived shutdown.");
                Require(original is null || original.IsDisposed && !original.IsHandleCreated, "The original Unicode fixture control survived shutdown.");
                if (profile is not null) Require(!Directory.Exists(profile), "The Unicode fixture profile survived shutdown.");
            }
            finally { Window.Dispose(); process?.Dispose(); }
            var full = Path.GetFullPath(Root);
            Require(Path.GetDirectoryName(full) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                && Path.GetFileName(full).StartsWith("cmux-unicode-input-", StringComparison.Ordinal), "Unowned Unicode fixture cleanup path.");
            Directory.Delete(full, true);
        }
    }
}

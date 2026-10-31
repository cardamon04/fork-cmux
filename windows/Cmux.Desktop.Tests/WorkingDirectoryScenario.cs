using System.Diagnostics;
using System.Text.Json;
using Cmux.Desktop;

internal static class WorkingDirectoryScenario
{
    internal static async Task Run(ShellLaunch launch, List<string> passed, string? artifacts = null, string? only = null)
    {
        if (only is not (null or "pwsh" or "windows-powershell")) throw new ArgumentException("Unknown CWD shell case.");
        if (only is null) { await ValidateBoundaries(launch, passed); await Regression(launch, passed); await Flood(launch, passed); await ReportedCase(launch, passed); }
        if (only is null or "pwsh") await Integration(launch, "pwsh", passed, artifacts);
        if (only is null or "windows-powershell")
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            Require(File.Exists(executable), "The Windows PowerShell fallback is missing.");
            await Integration(launch with { Executable = executable }, "windows-powershell", passed, artifacts);
        }
    }

    private static async Task Integration(ShellLaunch source, string name, List<string> passed, string? artifacts)
    {
        var directory = Directory.CreateTempSubdirectory("cmux-cwd-" + name + "-").FullName;
        var firstTarget = Directory.CreateDirectory(Path.Combine(directory, "日本語 # % ' ; $(synthetic)")).FullName;
        var secondTarget = Directory.CreateDirectory(Path.Combine(directory, "other synthetic directory")).FullName;
        var path = Path.Combine(directory, "session.json");
        var initialization = source.Arguments[^1] + "; Set-StrictMode -Version Latest; function global:Write-Error { throw 'SHADOW_SHOULD_NOT_RUN' }; function global:prompt { $global:cmuxObservedSuccess=$global:?; $global:cmuxObservedExit=Microsoft.PowerShell.Utility\\Get-Variable LASTEXITCODE -Scope Global -ValueOnly -ErrorAction Ignore; 'CMUX_TEST_READY> ' }";
        var launch = source with { Directory = directory, Arguments = ShellLaunch.PowerShellArguments(initialization, noProfile: true) };
        using var window = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        using var restored = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        var processes = new List<Process>();
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            var first = (TerminalPane)window.Current!.Active!;
            await Change(first, firstTarget);
            Require(Status(window).Contains(firstTarget), "The selected terminal's directory was not displayed.");
            Pass("cwd-" + name + "-automatic-cd-and-status", passed);
            await window.ClickAction("SplitRight");
            var second = (TerminalPane)window.Current.Active!;
            await Program.Command(second, "Write-Output ('SPLIT_CWD_'+(Get-Location).ProviderPath)");
            await ActualDirectory(second, firstTarget, "SPLIT_CWD_");
            await Change(first, secondTarget);
            Require(Status(window).Contains(secondTarget) && second.WorkingDirectory == firstTarget, "Directory state crossed terminal panes.");
            await window.ClickAction("OpenBrowser");
            Require(Status(window) == "", "A browser displayed terminal directory state.");
            await window.ClickAction("ClosePane");
            first.FocusPane();
            Require(Status(window).Contains(secondTarget), "Returning to the terminal lost its directory display.");
            Pass("cwd-" + name + "-split-and-pane-isolation", passed);

            await Program.Command(first, "$global:cmuxErrorBefore=$Error.Count; & $env:ComSpec /d /c 'exit 17'");
            await Program.Command(first, "Write-Output ('PROMPT_NATIVE_'+$global:cmuxObservedSuccess+'|'+$global:cmuxObservedExit+'|'+($Error.Count-$global:cmuxErrorBefore))");
            await Program.Contains(first, "PROMPT_NATIVE_False|17|0");
            await Program.Command(first, "Microsoft.PowerShell.Utility\\Write-Error 'SYNTHETIC_CWD_FAILURE' -ErrorAction Continue");
            await Program.Command(first, "Write-Output ('PROMPT_ERROR_'+$global:cmuxObservedSuccess+'|'+$global:cmuxObservedExit+'|'+($Error.Count-$global:cmuxErrorBefore))");
            await Program.Contains(first, "PROMPT_ERROR_False|17|1");
            await Program.Command(first, "& $env:ComSpec /d /c 'exit 0'");
            await Program.Command(first, "Write-Output ('PROMPT_SUCCESS_'+$global:cmuxObservedSuccess+'|'+$global:cmuxObservedExit)");
            await Program.Contains(first, "PROMPT_SUCCESS_True|0");
            Require(!(await Program.Text(first)).Contains("SHADOW_SHOULD_NOT_RUN"), "The integration invoked a shadowed helper.");
            Pass("cwd-" + name + "-preserves-prompt-exit-and-errors", passed);

            var oldUri = new Uri(firstTarget).AbsoluteUri.Replace("'", "''");
            var newUri = new Uri(secondTarget).AbsoluteUri.Replace("'", "''");
            await Program.Command(first, "$cmuxOld='" + oldUri + "'; $cmuxNew='" + newUri + "'; 1..100 | ForEach-Object { [Console]::Write(([char]27)+']7;'+$cmuxOld+([char]7)); [Console]::Write(([char]27)+']7;'+$cmuxNew+([char]7)) }; Write-Output ('CWD_BURST_'+'DONE')");
            await Program.Contains(first, "CWD_BURST_DONE");
            await first.CaptureWorkingDirectory();
            Require(first.WorkingDirectory == secondTarget, "A stale directory report replaced the latest valid directory.");
            await Program.Command(first, "[Console]::Write(([char]27)+']7;file://remote.invalid/C:/'+([char]7)); [Console]::Write(([char]27)+']7;file:///C:/missing-synthetic-cwd-5ec6c117'+([char]7)); Write-Output ('CWD_INVALID_'+'DONE')");
            await Program.Contains(first, "CWD_INVALID_DONE");
            await first.CaptureWorkingDirectory();
            Require(first.WorkingDirectory == secondTarget, "An invalid report replaced the confirmed local directory.");
            Pass("cwd-" + name + "-burst-and-invalid-report-isolation", passed);

            await Program.Command(first, "Write-Output ('SYNTHETIC_'+'DO_NOT_SAVE_OUTPUT')");
            await Program.Contains(first, "SYNTHETIC_DO_NOT_SAVE_OUTPUT");
            await window.ClickAction("SaveSession");
            var saved = File.ReadAllText(path);
            Require(!saved.Contains("SYNTHETIC_DO_NOT_SAVE_OUTPUT") && !saved.Contains("cmuxObserved") && !saved.Contains("History"), "CWD tracking expanded the save to output, commands or history.");
            foreach (var pane in window.Current.Panes.OfType<TerminalPane>())
            {
                var process = Process.GetProcessById(pane.ProcessId); _ = process.SafeHandle; processes.Add(process);
            }
            var closed = Closed(window);
            window.Close();
            await closed.WaitAsync(TimeSpan.FromSeconds(30));
            Require(processes.All(process => process.WaitForExit(0)), "An original integrated shell survived close.");
            restored.Show();
            await restored.Started.WaitAsync(TimeSpan.FromSeconds(30));
            var panes = restored.Current!.Panes.OfType<TerminalPane>().ToArray();
            Require(panes.Length == 2, "The integrated layout lost a terminal.");
            foreach (var pane in panes)
            {
                var expected = pane == panes[0] ? secondTarget : firstTarget;
                await Program.Command(pane, "Write-Output ('ACTUAL_RESTORED_'+(Get-Location).ProviderPath)");
                await ActualDirectory(pane, expected, "ACTUAL_RESTORED_");
                await Program.Until(() => Task.FromResult(pane.WorkingDirectory == expected), "The restored shell did not report its own directory.");
            }
            if (artifacts is not null)
            {
                Directory.CreateDirectory(artifacts);
                OwnedWindows.Capture(restored.Handle, Path.Combine(artifacts, name == "pwsh" ? "working-directory.png" : "working-directory-windows-powershell.png"));
            }
            Require(window.LastError is null && restored.LastError is null, "The integrated window reported an error.");
            Pass("cwd-" + name + "-save-and-reopen-actual-directory", passed);
        }
        finally
        {
            foreach (var item in new[] { window, restored })
                if (!item.IsDisposed) { await item.Shutdown().WaitAsync(TimeSpan.FromSeconds(30)); item.Close(); }
            foreach (var process in processes) process.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task Change(TerminalPane pane, string path)
    {
        await Program.Command(pane, "Set-Location -LiteralPath '" + path.Replace("'", "''") + "'");
        await Program.Until(() => Task.FromResult(pane.WorkingDirectory == path), "The integrated prompt did not update the working directory.");
    }
    private static string Status(DesktopWindow window) => window.Controls.OfType<StatusStrip>().Single().Items["WorkingDirectory"]!.Text ?? "";
    private static Task ContainsPath(TerminalPane pane, string marker) => Program.Until(async () =>
        (await Program.Text(pane)).Replace("\r", "").Replace("\n", "").Contains(marker), "Missing rendered logical path: " + marker);

    private static async Task ActualDirectory(TerminalPane pane, string expected, string visiblePrefix)
    {
        // The cwd oracle reads the actual provider path as UTF-8 bytes over an
        // ASCII envelope, independently of legacy console glyph width/encoding.
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(expected));
        await Program.Command(pane, "Write-Output ('CWD_UTF8_'+[Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes((Get-Location).ProviderPath))); Write-Output ('CWD_OUTPUT_CODEPAGE_'+[Console]::OutputEncoding.CodePage)");
        try { await ContainsPath(pane, "CWD_UTF8_" + encoded); }
        catch { Console.Error.WriteLine("cwd-value diagnostic: " + await Program.Text(pane)); throw; }
        var rendered = await Program.Text(pane);
        if (!rendered.Replace("\r", "").Replace("\n", "").Contains(visiblePrefix + expected))
            Console.WriteLine("coverage-gap: literal Unicode path display differs; UTF-8 cwd value verified. private rendered data: " + rendered);
    }

    private static async Task ValidateBoundaries(ShellLaunch launch, List<string> passed)
    {
        var directory = Directory.CreateTempSubdirectory("cmux-cwd-validation-").FullName;
        try
        {
            Require(WorkingDirectoryReport.Validate(new Uri(directory).AbsoluteUri) == directory, "A local URI was refused.");
            foreach (var value in new[] { "", directory, "https://localhost/", "file://remote.invalid/C:/", "file://user@localhost/C:/", "file:///C:/missing-synthetic-cwd-5ec6c117", new Uri(directory).AbsoluteUri + "?synthetic=value", new Uri(directory).AbsoluteUri + "#synthetic", "file:///C:/%00", "file:///C:/%ZZ", new string('a', 300001) })
                Require(WorkingDirectoryReport.Validate(value) is null, "An invalid or non-local report was accepted.");
            var target = Directory.CreateDirectory(Path.Combine(directory, "owned target")).FullName;
            Directory.CreateDirectory(Path.Combine(target, "child"));
            var link = Path.Combine(directory, "owned junction");
            var info = new ProcessStartInfo(launch.Executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null" }) info.ArgumentList.Add(argument);
            using (var process = Process.Start(info)!)
            {
                var errors = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEndAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
                catch { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); throw; }
                Require(process.ExitCode == 0, "The private junction fixture failed: " + await errors);
                await output;
            }
            try
            {
                Require(Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "The fixture did not create a real junction.");
                Require(WorkingDirectoryReport.Validate(new Uri(link).AbsoluteUri) is null, "A redirected directory was accepted.");
                Require(Directory.Exists(Path.Combine(link, "child")), "The redirected ancestor fixture does not exist.");
                Require(WorkingDirectoryReport.Validate(new Uri(Path.Combine(link, "child")).AbsoluteUri) is null, "A redirected ancestor was accepted.");
            }
            finally { Directory.Delete(link); }
            Pass("cwd-local-uri-and-metadata-boundaries", passed);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static async Task Regression(ShellLaunch launch, List<string> passed)
    {
        var directory = Directory.CreateTempSubdirectory("cmux-cwd-regression-").FullName;
        var target = Directory.CreateDirectory(Path.Combine(directory, "日本語 # % ' ; $(synthetic)")).FullName;
        var path = Path.Combine(directory, "session.json");
        using var first = new DesktopWindow(launch with { Directory = directory }, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        using var restored = new DesktopWindow(launch with { Directory = directory }, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false, ClientSize = new Size(640, 400) };
        try
        {
            first.Show();
            await first.Started.WaitAsync(TimeSpan.FromSeconds(30));
            var pane = (TerminalPane)first.Current!.Active!;
            using var owned = Process.GetProcessById(pane.ProcessId);
            _ = owned.SafeHandle;
            // OSC 7 is terminal data, not an application test-only setter.
            await Program.Command(pane, "Set-Location -LiteralPath '" + target.Replace("'", "''") + "'; [Console]::Write(([char]27)+']7;'+([uri](Get-Location).ProviderPath).AbsoluteUri+([char]7)); Write-Output ('CWD_REPORT_'+'READY'); Write-Output ('SYNTHETIC_OUTPUT_'+'NOT_SAVED')");
            await Program.Contains(pane, "SYNTHETIC_OUTPUT_NOT_SAVED");
            await first.ClickAction("SaveSession");
            var saved = File.ReadAllText(path);
            var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(saved)!;
            Require(snapshot.Workspaces[0].Layout.Directory == target, "Saving after a reported cd retained the launch directory.");
            Require(!saved.Contains("SYNTHETIC_OUTPUT") && !saved.Contains("CWD_REPORT") && !saved.Contains("Executable"), "Directory tracking expanded the saved data to shell output or commands.");
            var closed = Closed(first);
            first.Close();
            await closed.WaitAsync(TimeSpan.FromSeconds(30));
            Require(owned.WaitForExit(0) && !Directory.Exists(first.ProfilePath), "The original owned shell/profile survived close.");
            restored.Show();
            await restored.Started.WaitAsync(TimeSpan.FromSeconds(30));
            var reopened = (TerminalPane)restored.Current!.Active!;
            await Program.Command(reopened, "Write-Output ('REOPENED_CWD_'+(Get-Location).ProviderPath)");
            try { await ContainsPath(reopened, "REOPENED_CWD_" + target); }
            catch { Console.Error.WriteLine("cwd-regression rendered: " + await Program.Text(reopened)); throw; }
            Pass("cwd-report-save-close-and-reopen", passed);
        }
        finally
        {
            foreach (var window in new[] { first, restored })
                if (!window.IsDisposed) { await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30)); window.Close(); }
            Directory.Delete(directory, recursive: true);
        }
    }
    internal static async Task ReportedCase(ShellLaunch launch, List<string> passed)
    {
        var directory = Directory.CreateTempSubdirectory("cmux-cwd-case-").FullName;
        var lower = Directory.CreateDirectory(Path.Combine(directory, "casePair")).FullName;
        var upper = Directory.CreateDirectory(Path.Combine(directory, "CASEPAIR")).FullName;
        using var window = new DesktopWindow(launch with { Directory = directory }, new MemoryClipboard()) { ShowInTaskbar = false };
        try
        {
            window.Show(); await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            var pane = (TerminalPane)window.Current!.Active!;
            var sequence = 0;
            foreach (var target in new[] { lower, upper })
            {
                var marker = "CASE_REPORT_" + ++sequence + "_READY";
                await Program.Command(pane, "[Console]::Write(([char]27)+']7;'+'" + new Uri(target).AbsoluteUri.Replace("'", "''") + "'+([char]7)); Write-Output ('CASE_REPORT_'+'" + sequence + "_READY')");
                await Program.Contains(pane, marker);
                await Program.Until(async () => await pane.CaptureWorkingDirectory() == target, "A valid directory spelling was not captured.");
                Require(pane.WorkingDirectory == target, "A case-only CWD report did not update the model.");
                Require(Status(window).Contains(target, StringComparison.Ordinal), "The status display lost the reported directory spelling.");
            }
            Pass("cwd-preserves-reported-path-case", passed);
        }
        finally { await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30)); window.Close(); Directory.Delete(directory, recursive: true); }
    }

    internal static async Task Flood(ShellLaunch launch, List<string> passed)
    {
        var directory = Directory.CreateTempSubdirectory("cmux-cwd-flood-").FullName;
        var target = directory;
        for (var index = 0; index < 64; index++) target = Directory.CreateDirectory(Path.Combine(target, "d")).FullName;
        var path = Path.Combine(directory, "session.json");
        using var window = new DesktopWindow(launch with { Directory = directory }, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        Task? save = null;
        TerminalPane? pane = null;
        Process? owned = null;
        var completedWhileReporting = false;
        var savedDirectory = "";
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            pane = (TerminalPane)window.Current!.Active!;
            owned = Process.GetProcessById(pane.ProcessId); _ = owned.SafeHandle;
            await Program.Command(pane, "$cmuxFloodUri='" + new Uri(target).AbsoluteUri.Replace("'", "''") + "'; Write-Output ('CWD_FLOOD_'+'READY'); while ($true) { [Console]::Write(([char]27)+']7;'+$cmuxFloodUri+([char]7)) }");
            await Program.Contains(pane, "CWD_FLOOD_READY");
            save = window.ClickAction("SaveSession");
            try
            {
                await save.WaitAsync(TimeSpan.FromSeconds(5)); completedWhileReporting = true;
                savedDirectory = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))!.Workspaces[0].Layout.Directory!;
            }
            catch (TimeoutException) { }
        }
        finally
        {
            var interrupted = pane is null;
            try
            {
                if (pane is not null)
                {
                    foreach (var type in new[] { "keyDown", "keyUp" })
                        await pane.Core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key = "c", code = "KeyC", modifiers = 2, windowsVirtualKeyCode = 67, nativeVirtualKeyCode = 67 }));
                    await Program.Until(async () => (await Program.Text(pane)).TrimEnd().EndsWith("CMUX_TEST_READY>"), "The private report flood did not stop on Ctrl+C.");
                    interrupted = true;
                }
            }
            finally
            {
                try
                {
                    if (!interrupted && owned is { HasExited: false }) { owned.Kill(); await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
                    if (save is not null) await save.WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    try { await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30)); window.Close(); }
                    finally { owned?.Dispose(); Directory.Delete(directory, recursive: true); }
                }
            }
        }
        Require(completedWhileReporting, "Saving waited for an unending directory-report stream to become idle.");
        Require(savedDirectory == target, "The report stream's checkpoint saved the wrong directory.");
        Require(window.LastError is null, "The report flood caused a product error.");
        Pass("cwd-save-completes-during-continuous-reports", passed);
    }
    private static Task Closed(Form form)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        form.FormClosed += (_, _) => signal.TrySetResult();
        return signal.Task;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string name, List<string> passed) { passed.Add(name); Console.WriteLine("passed: " + name); }
}

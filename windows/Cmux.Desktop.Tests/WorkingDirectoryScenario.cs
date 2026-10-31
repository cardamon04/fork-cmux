using System.Diagnostics;
using System.Text.Json;
using Cmux.Desktop;

internal static class WorkingDirectoryScenario
{
    internal static async Task Regression(ShellLaunch launch, List<string> passed)
    {
        var directory = Directory.CreateTempSubdirectory("cmux-cwd-regression-").FullName;
        var target = Directory.CreateDirectory(Path.Combine(directory, "日本語 # % ' ; $(synthetic)")).FullName;
        var path = Path.Combine(directory, "session.json");
        using var first = new DesktopWindow(launch with { Directory = directory }, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        using var restored = new DesktopWindow(launch with { Directory = directory }, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
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
            await Program.Contains(reopened, "REOPENED_CWD_" + target);
            Pass("cwd-report-save-close-and-reopen", passed);
        }
        finally
        {
            foreach (var window in new[] { first, restored })
                if (!window.IsDisposed) { await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30)); window.Close(); }
            Directory.Delete(directory, recursive: true);
        }
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

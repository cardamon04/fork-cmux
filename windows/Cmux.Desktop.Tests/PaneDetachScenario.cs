using System.Diagnostics;
using Cmux.Desktop;

internal static class PaneDetachScenario
{
    internal static Task Run(ShellLaunch source, List<string> passed, string? artifacts) => Regression(source, passed);

    internal static async Task Regression(ShellLaunch source, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-pane-detach-").FullName;
        using var window = new DesktopWindow(source with { Directory = root }, new MemoryClipboard()) { ShowInTaskbar = false };
        var processes = new List<Process>();
        string? profile = null;
        void Track(TerminalPane pane)
        {
            var process = Process.GetProcessById(pane.ProcessId);
            _ = process.SafeHandle;
            processes.Add(process);
        }
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = window.ProfilePath;
            var original = window.Current!;
            var remaining = (TerminalPane)original.Active!;
            Track(remaining);
            await Program.Contains(remaining, "CMUX_TEST_READY>");
            await window.ClickAction("SplitRight");
            var moved = (TerminalPane)original.Active!;
            Track(moved);
            await Program.Contains(moved, "CMUX_TEST_READY>");
            await Program.Command(moved, "$global:cmuxDetachValue = 'DETACH_' + 'STATE'");
            var button = window.Controls.OfType<ToolStrip>().First(strip => strip is not StatusStrip).Items["MovePaneToNewWorkspace"] as ToolStripButton;
            if (button is not { Enabled: true }) throw new InvalidOperationException("A split pane has no usable move-to-new-workspace action.");
            button.PerformClick();
            await window.PendingAction;
            Require(window.LastError is null, "Detaching the native pane reported an error.");
            var destination = window.Current!;
            Require(window.Workspaces.Count() == 2 && destination != original, "Detaching did not select a new workspace.");
            Require(original.Panes.SequenceEqual(new[] { remaining }) && original.Active == remaining && original.Controls[0] == remaining,
                "The source split was not collapsed around its existing remaining pane.");
            Require(destination.Panes.SequenceEqual(new[] { moved }) && destination.Active == moved && destination.Controls[0] == moved,
                "The destination did not retain the original pane identity.");
            await Program.Command(moved, "Write-Output $global:cmuxDetachValue");
            await Program.Contains(moved, "DETACH_STATE");
            await window.ClickAction("SplitBelow");
            var added = (TerminalPane)destination.Active!;
            Track(added);
            await Program.Contains(added, "CMUX_TEST_READY>");
            moved.FocusPane();
            Require(window.Current == destination && destination.Active == moved, "Moved pane activation still belongs to its old workspace.");
            await Program.Command(remaining, "Write-Output ('DETACH_' + 'SOURCE_LIVE')");
            await Program.Contains(remaining, "DETACH_SOURCE_LIVE");
            Require(processes.All(process => !process.HasExited), "A live detach fixture shell was restarted or terminated.");
            passed.Add("pane-detach-native-terminal-identity-state-and-new-owner");
            Console.WriteLine("passed: " + passed[^1]);
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var process in processes) { Require(process.WaitForExit(0), "An original detach shell survived shutdown."); process.Dispose(); }
            if (profile is not null) Require(!Directory.Exists(profile), "The detach fixture profile survived shutdown.");
            var full = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("cmux-pane-detach-", StringComparison.Ordinal), "Refusing to delete a non-fixture directory.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

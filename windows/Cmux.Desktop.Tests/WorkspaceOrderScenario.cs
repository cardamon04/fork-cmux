using System.Diagnostics;
using Cmux.Desktop;

internal static class WorkspaceOrderScenario
{
    internal static async Task Regression(ShellLaunch source, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-workspace-order-").FullName;
        using var window = new DesktopWindow(source with { Directory = root }, new MemoryClipboard()) { ShowInTaskbar = false };
        var processes = new List<Process>();
        string? profile = null;
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = window.ProfilePath;
            for (var i = 0; i < 3; i++)
            {
                if (i > 0) await window.ClickAction("NewWorkspace");
                var pane = (TerminalPane)window.Current!.Active!;
                var process = Process.GetProcessById(pane.ProcessId);
                _ = process.SafeHandle;
                processes.Add(process);
                await Program.Contains(pane, "CMUX_TEST_READY>");
            }
            var before = window.Workspaces.ToArray();
            var selected = window.Current!;
            var active = selected.Active;
            var button = window.Controls.OfType<ToolStrip>().First(strip => strip is not StatusStrip).Items["MoveWorkspaceUp"] as ToolStripButton;
            Require(button is { Enabled: true }, "The selected workspace has no usable move-up action.");
            button.PerformClick();
            await window.PendingAction;
            Require(window.Workspaces.SequenceEqual(new[] { before[0], before[2], before[1] }), "Move up did not reorder the native sidebar workspace objects.");
            Require(window.Current == selected && selected.Active == active, "Reordering changed workspace/pane identity.");
            await Program.Command((TerminalPane)active!, "Write-Output ('ORDER_'+ 'LIVE')");
            await Program.Contains((TerminalPane)active!, "ORDER_LIVE");
            Pass("workspace-order-native-up-preserves-live-selection", passed);
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var process in processes) { Require(process.WaitForExit(0), "An original order fixture shell survived shutdown."); process.Dispose(); }
            if (profile is not null) Require(!Directory.Exists(profile), "The order fixture profile survived shutdown.");
            DeleteFixture(root);
        }
    }

    private static void DeleteFixture(string directory)
    {
        var full = Path.GetFullPath(directory);
        Require(Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("cmux-workspace-order-", StringComparison.Ordinal), "Refusing to remove a non-fixture directory.");
        Directory.Delete(full, recursive: true);
    }

    private static void Pass(string name, List<string> passed) { passed.Add(name); Console.WriteLine("passed: " + name); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

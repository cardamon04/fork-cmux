using System.Diagnostics;
using System.Text.Json;
using Cmux.Desktop;

internal static class PaneMoveDepthScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-pane-move-depth-").FullName;
        var path = Path.Combine(root, "layout.json");
        var deep = new LayoutSnapshot("browser", Url: "about:blank");
        for (var index = 0; index < 16; index++)
            // Keep both native branches measurable; halving sixteen times
            // reaches a pre-existing zero-size restore limitation.
            deep = new("split", Orientation: "horizontal", Ratio: .05,
                First: new("browser", Url: "about:blank"), Second: deep);
        var seed = new SessionSnapshot(1, 2, 0, [
            new("Source", 1, new("split", Orientation: "vertical", Ratio: .5,
                First: new("terminal", Directory: root), Second: new("terminal", Directory: root))),
            new("Depth boundary", 16, deep),
        ]);
        seed.Validate();
        using (var store = new SessionStore(path)) { _ = store.Open(); store.Save(seed); }
        using var window = new DesktopWindow(source with { Directory = root }, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        var processes = new List<Process>();
        var profiles = new List<string>();
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(120));
            profiles.Add(window.ProfilePath);
            if (window.BrowserProfilePath is { } browserProfile) profiles.Add(browserProfile);
            var workspaces = window.Workspaces.ToArray();
            var original = workspaces[0];
            var destination = workspaces[1];
            var moved = (TerminalPane)original.Active!;
            foreach (var pane in original.Panes.OfType<TerminalPane>())
            {
                var process = Process.GetProcessById(pane.ProcessId); _ = process.SafeHandle; processes.Add(process);
                await Program.Contains(pane, "CMUX_TEST_READY>");
            }
            var depth = 0;
            for (Control? control = destination.Active!.Parent; control is not null; control = control.Parent)
                if (control is SplitContainer) depth++;
            Require(depth == 16 && destination.Panes.Count == 17 && original.Panes.Count == 2,
                "The native depth-boundary fixture was not at the valid limit.");
            var before = JsonSerializer.Serialize(window.CaptureSession());
            window.CaptureSession().Validate();
            var sourceTree = original.Controls[0];
            var targetTree = destination.Controls[0];
            Console.WriteLine("PANE_MOVE_DEPTH_PRECONDITION " + JsonSerializer.Serialize(new { depth, destinationPanes = destination.Panes.Count, sourcePanes = original.Panes.Count, validBeforeMove = true }));
            var strip = window.Controls.OfType<ToolStrip>().Single(item => item is not StatusStrip);
            var menu = strip.Items.OfType<ToolStripDropDownButton>().Single(item => item.Name == "MovePaneToWorkspace");
            Require(menu.Enabled, "A live source split had no destination menu.");
            if (menu.IsOnOverflow) strip.OverflowButton.ShowDropDown();
            menu.ShowDropDown();
            Require(menu.DropDown.Visible, "The boundary destination menu did not open.");
            menu.DropDownItems.OfType<ToolStripMenuItem>().Single().PerformClick();
            await window.PendingAction;
            var after = window.CaptureSession();
            try { after.Validate(); }
            catch (InvalidDataException error)
            {
                throw new InvalidOperationException("Moving into the valid depth-16 destination created an unsaveable depth-17 layout.", error);
            }
            Require(JsonSerializer.Serialize(after) == before && window.Current == original && original.Active == moved
                && original.Controls[0] == sourceTree && destination.Controls[0] == targetTree
                && !sourceTree.IsDisposed && !targetTree.IsDisposed && processes.All(process => !process.WaitForExit(0)),
                "A depth-limited destination transfer changed the original live layout or selection.");
            menu.HideDropDown();
            strip.OverflowButton.HideDropDown();
            await Program.Command(moved, "Write-Output ('DEPTH_' + 'SOURCE_STILL_LIVE')");
            await Program.Contains(moved, "DEPTH_SOURCE_STILL_LIVE");
            await window.ClickAction("SaveSession");
            Require(window.LastError is null && JsonSerializer.Serialize(JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))) == before,
                "Refusing the boundary transfer did not retain a saveable session.");
            passed.Add("pane-move-depth-boundary-preserves-saveable-live-layout");
            Console.WriteLine("passed: pane-move-depth-boundary-preserves-saveable-live-layout");
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(120));
            foreach (var process in processes) { Require(process.WaitForExit(0), "An owned depth fixture shell survived shutdown."); process.Dispose(); }
            foreach (var profile in profiles) Require(!Directory.Exists(profile), "An owned depth fixture profile survived shutdown.");
            var full = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(full)!.Equals(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("cmux-pane-move-depth-", StringComparison.Ordinal), "Unexpected depth fixture cleanup path.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

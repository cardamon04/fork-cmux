using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Cmux.Desktop;

internal static class PaneCapacityScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed)
    {
        var failures = new List<Exception>();
        var fallback = source with { Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") };
        foreach (var (launch, shell) in new[] { (source, "pwsh"), (fallback, "windows-powershell") })
            foreach (var split in new[] { false, true })
                try
                {
                    await using var fixture = new Fixture(launch, split ? 255 : 256);
                    await fixture.Start();
                    if (split) await Splits(fixture, shell, passed);
                    else await Factories(fixture, shell, passed);
                }
                catch (Exception error)
                {
                    Console.WriteLine("PANE_CAPACITY_FAILURE " + JsonSerializer.Serialize(new { shell, split, error = error.Message }));
                    failures.Add(error);
                }
        if (failures.Count != 0) throw new AggregateException("Pane capacity regressions failed.", failures);
    }

    private static async Task Factories(Fixture fixture, string shell, List<string> passed)
    {
        var window = fixture.Window;
        var original = fixture.Terminal;
        var browser = fixture.Browser;
        var core = browser.Core;
        await Memory(original, "Factory");
        Require(await core.ExecuteScriptAsync("window.cmuxPaneCapacity=41") == "41", "The owned capacity browser is not initialized.");
        await fixture.Checkpoint();
        Precondition(fixture, shell, "factory", 256);
        Pass("factory-valid-checkpoint", shell, passed);
        var before = Serialize(window);
        await window.NewWorkspace(); fixture.Track();
        After(fixture, shell, "new-workspace");
        Require(Valid(window), "Creating a workspace after pane256 left an unsavable live session.");
        Require(Serialize(window) == before && window.Current == fixture.Source && fixture.Source.Active == original
            && fixture.Processes.Count == 1 && window.LastError is null && fixture.CheckpointUnchanged,
            "Refused workspace creation changed panes, selection, numbering or saved data.");
        Pass("factory-direct-workspace-refusal", shell, passed);

        var method = typeof(DesktopWindow).GetMethod("NewWorkspace", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(PaneKind)], null)!;
        await (Task)method.Invoke(window, [PaneKind.Browser])!;
        Require(Serialize(window) == before && window.LastError is null && fixture.CheckpointUnchanged,
            "Browser workspace creation bypassed the full pane budget.");
        Pass("factory-direct-browser-workspace-refusal", shell, passed);
        Require(!window.IsActionEnabled("NewWorkspace"), "The native New workspace action offers pane257.");
        await window.ClickAction("NewWorkspace");
        Require(Serialize(window) == before, "Disabled workspace creation mutated a full pane graph.");
        Pass("factory-native-workspace-disabled", shell, passed);

        var originalCore = original.Core;
        await MoveToExisting(window, fixture.Destination);
        Require(window.Current == fixture.Destination && fixture.Destination.Active == original && original.Core == originalCore
            && Count(window) == 256 && window.Workspaces.Count() == 2 && fixture.Processes.Count == 1,
            "A full pane budget blocked or restarted an existing-target transfer.");
        await CheckMemory(original, "Factory");
        Pass("factory-existing-target-transfer", shell, passed);
        Require(window.IsActionEnabled("MovePaneToNewWorkspace"), "A full pane budget disabled a resource-preserving new-workspace move.");
        await window.ClickAction("MovePaneToNewWorkspace");
        Require(window.Current!.Active == original && original.Core == originalCore && Count(window) == 256
            && window.Workspaces.Count() == 3 && window.CaptureSession().WorkspaceSequence == 3 && fixture.Processes.Count == 1
            && !window.IsActionEnabled("NewWorkspace") && await core.ExecuteScriptAsync("++window.cmuxPaneCapacity") == "42",
            "Moving to a new workspace consumed a pane budget or lost live resources.");
        await CheckMemory(original, "Factory");
        Pass("factory-new-workspace-transfer", shell, passed);
        await fixture.SaveCloseReadback();
        Pass("factory-valid-save-normal-close-store-readback", shell, passed);
    }

    private static async Task Splits(Fixture fixture, string shell, List<string> passed)
    {
        var window = fixture.Window;
        var original = fixture.Terminal;
        var browser = fixture.Browser;
        var browserCore = browser.Core;
        await Memory(original, "Original");
        Require(await browserCore.ExecuteScriptAsync("window.cmuxPaneCapacity=41") == "41", "The owned split browser is not initialized.");
        Require(Count(window) == 255 && window.IsActionEnabled("SplitRight"), "The private pane255 fixture cannot take its last valid split.");
        await window.ClickAction("SplitRight"); fixture.Track();
        var moved = (TerminalPane)fixture.Source.Active!;
        await Program.Contains(moved, "CMUX_TEST_READY>"); await Memory(moved, "Moved");
        await fixture.Checkpoint();
        Precondition(fixture, shell, "split", 256);
        Pass("split-valid-last-step", shell, passed);
        var movedCore = moved.Core;
        await Refused(() => window.Split(Orientation.Vertical), "vertical");
        await Refused(() => window.Split(Orientation.Horizontal), "horizontal");
        await Refused(window.OpenBrowser, "browser");
        var before = Serialize(window);
        foreach (var action in new[] { "SplitRight", "SplitBelow", "OpenBrowser", "NewWorkspace" })
        {
            Require(!window.IsActionEnabled(action), "A native addition action offers pane257: " + action);
            await window.ClickAction(action);
        }
        Require(Serialize(window) == before && fixture.CheckpointUnchanged, "Disabled additions mutated the full pane graph.");
        Pass("split-native-addition-disabled", shell, passed);

        Require(window.IsActionEnabled("MovePaneToNewWorkspace"), "The pane bound disabled a new-workspace transfer.");
        await window.ClickAction("MovePaneToNewWorkspace");
        var retiring = window.Current!;
        Require(retiring.Active == moved && moved.Core == movedCore && Count(window) == 256
            && window.CaptureSession().WorkspaceSequence == 3 && fixture.Processes.Count == 2,
            "Moving an existing pane to a new workspace consumed or restarted a pane.");
        await CheckMemory(moved, "Moved");
        Pass("split-live-new-workspace-transfer", shell, passed);
        await MoveToExisting(window, fixture.Destination);
        Require(retiring.IsDisposed && fixture.Destination.Active == moved && moved.Core == movedCore && Count(window) == 256
            && window.Workspaces.Count() == 2 && fixture.Processes.Values.All(process => !process.WaitForExit(0)),
            "The full pane bound blocked an existing-target transfer or lost its last source pane.");
        await CheckMemory(moved, "Moved");
        Pass("split-existing-target-transfer", shell, passed);

        await window.ClickAction("ClosePane");
        Require(moved.IsDisposed && fixture.Processes[moved].WaitForExit(0) && Count(window) == 255
            && window.IsActionEnabled("NewWorkspace") && window.IsActionEnabled("OpenBrowser") && !fixture.Processes[original].WaitForExit(0),
            "Closing a pane did not restore the creation budget or ended an unrelated shell.");
        Pass("split-close-releases-capacity", shell, passed);
        await window.ClickAction("NewWorkspace"); fixture.Track();
        var added = (TerminalPane)window.Current!.Active!;
        await Program.Contains(added, "CMUX_TEST_READY>");
        Require(Count(window) == 256 && window.CaptureSession().WorkspaceSequence == 4 && fixture.Processes.Count == 3
            && !window.IsActionEnabled("NewWorkspace"), "Native workspace creation did not consume exactly one free pane.");
        Pass("split-native-workspace-after-close", shell, passed);
        await window.ClickAction("CloseWorkspace");
        Require(added.IsDisposed && fixture.Processes[added].WaitForExit(0) && Count(window) == 255
            && window.Current == fixture.Destination && fixture.Destination.Active == browser && window.IsActionEnabled("OpenBrowser"),
            "Closing a sole-pane workspace did not restore browser split capacity.");
        await window.ClickAction("OpenBrowser");
        Require(Count(window) == 256 && fixture.Destination.Active is BrowserPane addedBrowser && addedBrowser != browser
            && window.CaptureSession().WorkspaceSequence == 4 && !window.IsActionEnabled("OpenBrowser"),
            "A browser split failed to consume exactly one free pane without changing workspace numbering.");
        Pass("split-browser-after-workspace-close", shell, passed);
        Require(await browserCore.ExecuteScriptAsync("++window.cmuxPaneCapacity") == "42", "The existing browser lost its live state.");
        window.SelectWorkspace(0); original.FocusPane(); await CheckMemory(original, "Original");
        Require(fixture.Source.Active == original && !fixture.Processes[original].WaitForExit(0), "The original terminal lost its live identity.");
        Pass("split-live-original-memory-and-browser-state", shell, passed);
        await fixture.SaveCloseReadback();
        Pass("split-valid-save-normal-close-store-readback", shell, passed);

        async Task Refused(Func<Task> action, string label)
        {
            var snapshot = Serialize(window);
            await action(); fixture.Track();
            After(fixture, shell, "split-" + label);
            Require(Valid(window), "A " + label + " split after pane256 left an unsavable live session.");
            Require(Serialize(window) == snapshot && window.Current == fixture.Source && fixture.Source.Active == moved
                && moved.Core == movedCore && fixture.Processes.Count == 2 && fixture.CheckpointUnchanged && window.LastError is null,
                "A refused " + label + " split changed the live graph, shell ownership or saved checkpoint.");
            Pass("split-direct-" + label + "-refusal", shell, passed);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = Directory.CreateTempSubdirectory("cmux-pane-capacity-").FullName;
        internal string Path => System.IO.Path.Combine(Root, "layout.json");
        internal readonly BrowserFixture Server = new();
        internal readonly DesktopWindow Window;
        internal readonly Dictionary<TerminalPane, Process> Processes = [];
        internal WorkspaceView Source => Window.Workspaces.First();
        internal WorkspaceView Destination => Window.Workspaces.Skip(1).First();
        internal TerminalPane Terminal = null!;
        internal BrowserPane Browser = null!;
        internal readonly int InitialPanes;
        private readonly List<BrowserPane> models = [];
        private readonly List<SplitContainer> modelSplits = [];
        private readonly HashSet<string> profiles = [];
        private byte[] checkpoint = [];
        internal bool CheckpointUnchanged => File.ReadAllBytes(Path).SequenceEqual(checkpoint);
        internal Fixture(ShellLaunch source, int panes)
        {
            InitialPanes = panes;
            var launch = source with { Directory = Root, Arguments = ShellLaunch.PowerShellArguments(source.Arguments[^1], noProfile: true) };
            var seed = new SessionSnapshot(1, 2, 0, [new("Capacity source", 0, new("terminal", Directory: Root)),
                new("Capacity destination", 0, new("browser", Url: Server.Url("pane-capacity-target")))]);
            seed.Validate();
            using (var store = new SessionStore(Path)) { _ = store.Open(); store.Save(seed); }
            Window = new(launch, new MemoryClipboard(), new SessionStore(Path)) { ShowInTaskbar = false };
        }
        internal async Task Start()
        {
            Window.Show(); await Window.Started.WaitAsync(TimeSpan.FromSeconds(45)); Track();
            Terminal = (TerminalPane)Source.Active!; Browser = (BrowserPane)Destination.Active!;
            await Program.Contains(Terminal, "CMUX_TEST_READY>");
            await Program.Until(async () => await Browser.Core.ExecuteScriptAsync("document.querySelector('#page')?.textContent") == "\"/pane-capacity-target\"", "The owned pane-capacity browser did not load.");
            AddModels(Source, Terminal, 127); AddModels(Destination, Browser, InitialPanes - 129);
            Window.CaptureSession().Validate();
            Window.SelectWorkspace(1); Window.SelectWorkspace(0); Terminal.FocusPane();
            Window.CaptureSession().Validate();
            Require(Count(Window) == InitialPanes && models.Count == InitialPanes - 2 && Processes.Count == 1,
                "The private native pane graph has the wrong count or started engines.");
            Require(modelSplits.All(split => (split.Orientation == Orientation.Vertical ? split.ClientSize.Width : split.ClientSize.Height) > split.SplitterWidth),
                "The owned model tree has no usable split extent.");
        }
        private void AddModels(WorkspaceView workspace, WorkspacePane existing, int count)
        {
            var leaves = new List<Control>();
            workspace.SuspendLayout();
            try
            {
                for (var index = 0; index < count; index++)
                {
                    var model = new BrowserPane(new Strings()); models.Add(model); workspace.Panes.Add(model); leaves.Add(model);
                }
                workspace.Controls.Remove(existing);
                var root = Wrapper(existing, Balanced(leaves));
                workspace.Controls.Add(root);
            }
            finally { workspace.ResumeLayout(performLayout: true); }
            Control Balanced(List<Control> nodes, int depth = 0)
            {
                if (nodes.Count == 1) return nodes[0];
                var half = nodes.Count / 2;
                return Wrapper(Balanced(nodes.GetRange(0, half), depth + 1), Balanced(nodes.GetRange(half, nodes.Count - half), depth + 1),
                    depth % 2 == 0 ? Orientation.Horizontal : Orientation.Vertical);
            }
            SplitContainer Wrapper(Control first, Control second, Orientation orientation = Orientation.Vertical)
            {
                var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = orientation, SplitterWidth = 5, Panel1MinSize = 0, Panel2MinSize = 0 };
                split.Panel1.Controls.Add(first); split.Panel2.Controls.Add(second); workspace.PendingRatios[split] = .5;
                modelSplits.Add(split);
                return split;
            }
        }
        internal void Track()
        {
            profiles.Add(Window.ProfilePath);
            if (Window.BrowserProfilePath is { } profile) profiles.Add(profile);
            foreach (var pane in Window.Workspaces.SelectMany(item => item.Panes).OfType<TerminalPane>())
            {
                if (Processes.ContainsKey(pane)) continue;
                var process = Process.GetProcessById(pane.ProcessId); _ = process.SafeHandle; Processes.Add(pane, process);
            }
        }
        internal async Task Checkpoint()
        {
            var snapshot = Window.CaptureSession(); snapshot.Validate();
            Require(Leaves(snapshot) == Count(Window), "The native pane count differs from the actual saved graph.");
            await Window.ClickAction("SaveSession");
            checkpoint = File.ReadAllBytes(Path);
            var stored = JsonSerializer.Deserialize<SessionSnapshot>(checkpoint)!; stored.Validate();
            Require(Window.LastError is null && Leaves(stored) == 256, "The valid pane256 checkpoint was not saved.");
        }
        internal async Task SaveCloseReadback()
        {
            var snapshot = Window.CaptureSession(); snapshot.Validate(); var expected = JsonSerializer.Serialize(snapshot);
            await Window.ClickAction("SaveSession");
            Require(Window.LastError is null && JsonSerializer.Serialize(JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(Path))) == expected,
                "The bounded pane graph did not save through the existing format.");
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Window.FormClosed += (_, _) => closed.TrySetResult(); Window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(45));
            Require(Processes.Values.All(process => process.WaitForExit(0)) && models.All(model => model.IsDisposed), "An owned shell or unstarted model survived normal close.");
            using var store = new SessionStore(Path);
            var stored = store.Open()!; stored.Validate();
            Require(stored.Version == 1 && Leaves(stored) == 256 && JsonSerializer.Serialize(stored) == expected,
                "Normal close/store readback lost the bounded pane graph.");
        }
        public async ValueTask DisposeAsync()
        {
            await Window.Shutdown().WaitAsync(TimeSpan.FromSeconds(45)); Window.Dispose();
            foreach (var process in Processes.Values) { Require(process.WaitForExit(0), "An owned pane-capacity shell survived shutdown."); process.Dispose(); }
            foreach (var profile in profiles) Require(!Directory.Exists(profile), "An owned pane-capacity profile survived shutdown.");
            await Server.DisposeAsync();
            var full = System.IO.Path.GetFullPath(Root);
            Require(System.IO.Path.GetDirectoryName(full)!.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && System.IO.Path.GetFileName(full).StartsWith("cmux-pane-capacity-", StringComparison.Ordinal), "Refusing to remove an unowned pane-capacity fixture.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static void Precondition(Fixture fixture, string shell, string action, int count) => Console.WriteLine("PANE_CAPACITY_PRECONDITION "
        + JsonSerializer.Serialize(new { shell, action, panes = Count(fixture.Window), valid = Valid(fixture.Window), startedTerminals = fixture.Processes.Count,
            startedBrowsers = 1, unstartedBrowserModels = fixture.InitialPanes - 2, expected = count }));
    private static void After(Fixture fixture, string shell, string action) => Console.WriteLine("PANE_CAPACITY_POST_ACTION "
        + JsonSerializer.Serialize(new { shell, action, panes = Count(fixture.Window), validAfter = Valid(fixture.Window) }));
    private static int Count(DesktopWindow window) => window.Workspaces.Sum(workspace => workspace.Panes.Count);
    private static int Leaves(SessionSnapshot snapshot) => snapshot.Workspaces.Sum(workspace => Visit(workspace.Layout));
    private static int Visit(LayoutSnapshot node) => node.Kind == "split" ? Visit(node.First!) + Visit(node.Second!) : 1;
    private static bool Valid(DesktopWindow window) { try { window.CaptureSession().Validate(); return true; } catch (InvalidDataException) { return false; } }
    private static string Serialize(DesktopWindow window) => JsonSerializer.Serialize(window.CaptureSession());
    private static async Task Memory(TerminalPane pane, string label)
    {
        await Program.Command(pane, "$global:cmuxPaneCapacity" + label + " = 'CAPACITY_' + '" + label + "'; Write-Output ('READY_' + '" + label + "')");
        await Program.Contains(pane, "READY_" + label);
    }
    private static async Task CheckMemory(TerminalPane pane, string label)
    {
        var token = Guid.NewGuid().ToString("N");
        await Program.Command(pane, "Write-Output ($global:cmuxPaneCapacity" + label + " + ':' + '" + token + "')");
        var expected = "CAPACITY_" + label + ":" + token;
        try { await Program.Contains(pane, expected); }
        catch
        {
            try
            {
                var renderer = await pane.Core.ExecuteScriptAsync("(()=>{const b=term.buffer.active,lines=[];for(let i=Math.max(0,b.length-60);i<b.length;i++){const line=b.getLine(i);lines.push({index:i,isWrapped:line?.isWrapped,text:line?.translateToString(true)});}return {cols:term.cols,rows:term.rows,cursorX:b.cursorX,cursorY:b.cursorY,baseY:b.baseY,lines};})()").WaitAsync(TimeSpan.FromSeconds(5));
                Console.WriteLine("PANE_CAPACITY_MEMORY_DIAGNOSTIC " + JsonSerializer.Serialize(new { label, expected, renderer = JsonSerializer.Deserialize<JsonElement>(renderer) }));
            }
            catch (Exception diagnostic) { Console.WriteLine("PANE_CAPACITY_MEMORY_DIAGNOSTIC_ERROR " + diagnostic.Message); }
            throw;
        }
    }
    private static async Task MoveToExisting(DesktopWindow window, WorkspaceView destination)
    {
        var strip = window.Controls.OfType<ToolStrip>().Single(item => item is not StatusStrip);
        var menu = strip.Items.OfType<ToolStripDropDownButton>().Single(item => item.Name == "MovePaneToWorkspace");
        Require(menu.Enabled, "The pane budget disabled existing-workspace destinations.");
        if (menu.IsOnOverflow) strip.OverflowButton.ShowDropDown();
        menu.ShowDropDown();
        var index = Array.IndexOf(window.Workspaces.ToArray(), destination);
        var label = new Strings().Format("WorkspaceDestination", index + 1, destination.Title);
        var row = menu.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.AccessibleName == label);
        Require(row.Enabled, "A full pane budget disabled an existing target.");
        row.PerformClick(); await window.PendingAction; Require(window.LastError is null, "The existing-target transfer reported an error.");
    }
    private static void Pass(string name, string shell, List<string> passed) { var value = "pane-capacity-" + shell + "-" + name; passed.Add(value); Console.WriteLine("passed: " + value); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

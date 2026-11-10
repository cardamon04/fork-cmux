using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Cmux.Desktop;

internal static class WorkspaceCapacityScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed)
    {
        var failures = new List<Exception>();
        var fallback = source with { Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") };
        foreach (var (launch, shell) in new[] { (source, "pwsh"), (fallback, "windows-powershell") })
            foreach (var sequence in new[] { false, true })
                try
                {
                    await using var fixture = new Fixture(launch, sequence);
                    await fixture.Start();
                    if (sequence) await Sequence(fixture, shell, passed);
                    else await Count(fixture, shell, passed);
                }
                catch (Exception error)
                {
                    Console.WriteLine("WORKSPACE_CAPACITY_FAILURE " + JsonSerializer.Serialize(new { shell, sequence, error = error.Message }));
                    failures.Add(error);
                }
        if (failures.Count != 0) throw new AggregateException("Workspace capacity regressions failed.", failures);
    }

    private static async Task Count(Fixture fixture, string shell, List<string> passed)
    {
        var window = fixture.Window;
        var initial = window.Workspaces.ToArray();
        var source = initial[0];
        var destination = initial[1];
        var original = (TerminalPane)source.Active!;
        var browser = (BrowserPane)destination.Active!;
        var core = browser.Core;
        Require(await core.ExecuteScriptAsync("window.cmuxCapacityPage=41") == "41", "The owned capacity browser was not initialized.");

        // Exercise the real native admission path and saved graph without
        // claiming that sixty-four browser engines ran. Only these sixty-two
        // additional model controls remain unstarted; they own no renderer.
        var sidebar = window.Controls.OfType<ListBox>().Single();
        var models = new List<WorkspaceView>();
        for (var index = 2; index < 64; index++)
        {
            var model = new WorkspaceView("Capacity model " + index) { Dock = DockStyle.Fill, Visible = false };
            var pane = new BrowserPane(new Strings());
            model.Panes.Add(pane); model.Active = pane; model.Controls.Add(pane);
            source.Parent!.Controls.Add(model); sidebar.Items.Add(model); models.Add(model);
        }
        window.SelectWorkspace(1); window.SelectWorkspace(0); original.FocusPane();
        var valid = window.CaptureSession(); valid.Validate();
        await window.ClickAction("SaveSession");
        var checkpoint = File.ReadAllBytes(fixture.Path);
        JsonSerializer.Deserialize<SessionSnapshot>(checkpoint)!.Validate();
        Require(window.LastError is null && valid.Workspaces.Length == 64 && valid.WorkspaceSequence == 64
            && fixture.Processes.Count == 1 && models.Count == 62, "The private sixty-four-workspace graph is not valid.");
        Console.WriteLine("WORKSPACE_CAPACITY_PRECONDITION " + JsonSerializer.Serialize(new { shell, count = 64, sequence = 64, valid = true, startedTerminals = 1, startedBrowsers = 1, unstartedBrowserModels = 62 }));
        Pass("count-valid-checkpoint", shell, passed);

        var before = Serialize(window);
        await window.NewWorkspace(); fixture.Track(window);
        var after = window.CaptureSession();
        var validAfter = IsValid(after);
        Console.WriteLine("WORKSPACE_CAPACITY_POST_ACTION " + JsonSerializer.Serialize(new { shell, count = after.Workspaces.Length, sequence = after.WorkspaceSequence, validAfter }));
        Require(validAfter, "Creating a sixty-fifth workspace left the live session outside the saved format.");
        Require(Serialize(window) == before && window.Current == source && source.Active == original && original.ContainsFocus
            && window.LastError is null && fixture.Processes.Count == 1 && File.ReadAllBytes(fixture.Path).SequenceEqual(checkpoint),
            "A refused workspace creation allocated a shell, changed selection/counter or saved implicitly.");
        Pass("count-direct-new-refusal", shell, passed);
        Require(!window.IsActionEnabled("NewWorkspace"), "The native New workspace action offers an unsavable sixty-fifth workspace.");
        await window.ClickAction("NewWorkspace");
        Require(Serialize(window) == before && fixture.Processes.Values.All(process => !process.WaitForExit(0)), "A disabled native creation mutated the full session.");
        Pass("count-native-new-disabled", shell, passed);

        await window.ClickAction("SplitRight"); fixture.Track(window);
        var moved = (TerminalPane)source.Active!;
        await Program.Contains(moved, "CMUX_TEST_READY>");
        await Program.Command(moved, "$global:cmuxCapacityMemory = 'CAPACITY_' + 'LIVE'; Write-Output ('CAPACITY_' + 'READY')");
        await Program.Contains(moved, "CAPACITY_READY");
        Require(source.Panes.Count == 2 && window.Workspaces.Count() == 64 && fixture.Processes.Count == 2, "Workspace capacity blocked an ordinary in-workspace split.");
        window.CaptureSession().Validate();
        Pass("count-split-still-allowed", shell, passed);
        before = Serialize(window);
        await DirectMoveToNew(window);
        Require(!window.IsActionEnabled("MovePaneToNewWorkspace"), "The native move action offers a sixty-fifth workspace.");
        await window.ClickAction("MovePaneToNewWorkspace");
        Require(Serialize(window) == before && window.Current == source && source.Active == moved && !moved.IsDisposed,
            "Moving to a new workspace bypassed the shared workspace capacity check.");
        Pass("count-direct-native-move-new-refusal", shell, passed);

        await CloseModel(); window.SelectWorkspace(0); moved.FocusPane();
        Require(window.IsActionEnabled("MovePaneToNewWorkspace"), "Closing a workspace did not restore live-move capacity.");
        var movedCore = moved.Core;
        await window.ClickAction("MovePaneToNewWorkspace");
        Require(window.Workspaces.Count() == 64 && window.CaptureSession().WorkspaceSequence == 65
            && window.Current!.Active == moved && moved.Core == movedCore && fixture.Processes.Count == 2,
            "Moving into newly available capacity restarted the shell or lost the counter.");
        await Program.Command(moved, "Write-Output $global:cmuxCapacityMemory"); await Program.Contains(moved, "CAPACITY_LIVE");
        Pass("count-close-releases-live-move-capacity", shell, passed);

        await CloseModel();
        Require(window.IsActionEnabled("NewWorkspace"), "Closing a workspace did not restore native creation capacity.");
        await window.ClickAction("NewWorkspace"); fixture.Track(window);
        var retiring = window.Current!;
        var added = (TerminalPane)retiring.Active!;
        await Program.Contains(added, "CMUX_TEST_READY>");
        Require(window.Workspaces.Count() == 64 && window.CaptureSession().WorkspaceSequence == 66
            && fixture.Processes.Count == 3 && !window.IsActionEnabled("NewWorkspace"), "Native creation did not consume exactly one free workspace.");
        Pass("count-native-new-after-close", shell, passed);
        var addedCore = added.Core;
        await MoveToExisting(window, destination);
        Require(window.Workspaces.Count() == 63 && window.Current == destination && destination.Active == added
            && retiring.IsDisposed && !added.IsDisposed && added.Core == addedCore && fixture.Processes.Values.All(process => !process.WaitForExit(0))
            && await core.ExecuteScriptAsync("++window.cmuxCapacityPage") == "42" && window.IsActionEnabled("NewWorkspace")
            && window.CaptureSession().WorkspaceSequence == 66, "A full session lost an allowed existing-target move or its newly freed capacity.");
        Pass("count-existing-target-at-full-capacity", shell, passed);

        var expected = Serialize(window);
        await window.ClickAction("SaveSession");
        Require(window.LastError is null && JsonSerializer.Serialize(JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(fixture.Path))) == expected,
            "The bounded workspace graph did not save through the existing schema.");
        await fixture.CloseNormally();
        using (var store = new SessionStore(fixture.Path))
        {
            var stored = store.Open()!; stored.Validate();
            Require(stored.Version == 1 && stored.Workspaces.Length == 63 && stored.WorkspaceSequence == 66
                && JsonSerializer.Serialize(stored) == expected, "Normal close/store readback lost the valid workspace boundary graph.");
        }
        Pass("count-valid-save-normal-close-store-readback", shell, passed);

        async Task CloseModel()
        {
            var model = models.Last(item => window.Workspaces.Contains(item));
            window.SelectWorkspace(Array.IndexOf(window.Workspaces.ToArray(), model));
            await window.ClickAction("CloseWorkspace");
            Require(model.IsDisposed && window.Workspaces.Count() == 63, "A model workspace did not release its native count slot.");
        }
    }

    private static async Task Sequence(Fixture fixture, string shell, List<string> passed)
    {
        var window = fixture.Window;
        var initial = window.Workspaces.ToArray();
        var original = (TerminalPane)initial[0].Active!;
        var destination = initial[1];
        await Program.Command(original, "$global:cmuxSequenceMemory = 'SEQUENCE_' + 'LIVE'; Write-Output ('SEQUENCE_' + 'READY')");
        await Program.Contains(original, "SEQUENCE_READY");
        Require(window.CaptureSession().WorkspaceSequence == int.MaxValue - 2 && window.IsActionEnabled("NewWorkspace"), "The private naming-boundary fixture cannot take its last valid step.");
        await window.ClickAction("NewWorkspace"); fixture.Track(window);
        var created = window.Current!;
        await Program.Contains((TerminalPane)created.Active!, "CMUX_TEST_READY>");
        var valid = window.CaptureSession(); valid.Validate();
        Require(valid.WorkspaceSequence == int.MaxValue - 1 && valid.Workspaces.Length == 3 && fixture.Processes.Count == 2,
            "The last valid workspace number was not created.");
        Console.WriteLine("WORKSPACE_SEQUENCE_PRECONDITION " + JsonSerializer.Serialize(new { shell, count = 3, sequence = valid.WorkspaceSequence, valid = true, startedTerminals = 2, startedBrowsers = 1 }));
        Pass("sequence-last-valid-step", shell, passed);
        var before = Serialize(window);
        await window.NewWorkspace(); fixture.Track(window);
        var after = window.CaptureSession();
        var validAfter = IsValid(after);
        Console.WriteLine("WORKSPACE_SEQUENCE_POST_ACTION " + JsonSerializer.Serialize(new { shell, count = after.Workspaces.Length, sequence = after.WorkspaceSequence, validAfter }));
        Require(validAfter, "Creating after the last workspace number left an unsavable naming counter.");
        Require(Serialize(window) == before && window.Current == created && fixture.Processes.Count == 2 && window.LastError is null,
            "A refused naming-boundary creation changed state or allocated a shell.");
        Pass("sequence-direct-new-refusal", shell, passed);
        Require(!window.IsActionEnabled("NewWorkspace"), "The native New workspace action offers an invalid naming counter.");
        await window.ClickAction("NewWorkspace");
        Require(Serialize(window) == before, "Disabled naming-boundary creation mutated the layout.");
        Pass("sequence-native-new-disabled", shell, passed);
        await window.ClickAction("OpenBrowser");
        Require(created.Panes.Count == 2 && created.Active is BrowserPane && window.CaptureSession().WorkspaceSequence == int.MaxValue - 1,
            "An exhausted workspace counter blocked a browser split or advanced on that split.");
        window.CaptureSession().Validate();
        Pass("sequence-browser-split-without-advance", shell, passed);
        before = Serialize(window);
        await DirectMoveToNew(window);
        Require(!window.IsActionEnabled("MovePaneToNewWorkspace"), "The native move action offers an invalid next workspace number.");
        await window.ClickAction("MovePaneToNewWorkspace");
        Require(Serialize(window) == before && !window.IsActionEnabled("NewWorkspace"), "A move bypassed the shared naming-boundary admission check.");
        Pass("sequence-direct-native-move-new-refusal", shell, passed);
        window.SelectWorkspace(0); original.FocusPane();
        await MoveToExisting(window, destination);
        Require(window.Workspaces.Count() == 2 && window.Current == destination && destination.Active == original
            && initial[0].IsDisposed && !window.IsActionEnabled("NewWorkspace") && fixture.Processes.Values.All(process => !process.WaitForExit(0)),
            "An exhausted counter blocked an existing-target move, reset numbering or ended a live shell.");
        await Program.Command(original, "Write-Output $global:cmuxSequenceMemory"); await Program.Contains(original, "SEQUENCE_LIVE");
        Pass("sequence-existing-target-allowed", shell, passed);

        valid = window.CaptureSession(); valid.Validate();
        await window.ClickAction("SaveSession");
        Require(window.LastError is null && !File.ReadAllText(fixture.Path).Contains("cmuxSequenceMemory", StringComparison.Ordinal), "Saving the naming boundary failed or added shell memory.");
        await fixture.CloseNormally();
        fixture.Reopened.Show(); await fixture.Reopened.Started.WaitAsync(TimeSpan.FromSeconds(45)); fixture.Track(fixture.Reopened);
        var restored = fixture.Reopened.CaptureSession(); restored.Validate();
        Require(restored.Version == 1 && restored.WorkspaceSequence == int.MaxValue - 1 && restored.Workspaces.Length == 2
            && restored.SelectedWorkspace == valid.SelectedWorkspace && !fixture.Reopened.IsActionEnabled("NewWorkspace")
            && restored.Workspaces.Select(item => item.ActivePane).SequenceEqual(valid.Workspaces.Select(item => item.ActivePane)),
            "Reopen lost valid numbering or re-enabled an unsavable new workspace.");
        Pass("sequence-valid-save-normal-close-reopen", shell, passed);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = Directory.CreateTempSubdirectory("cmux-workspace-capacity-").FullName;
        internal string Path => System.IO.Path.Combine(Root, "layout.json");
        internal readonly BrowserFixture Server = new();
        internal readonly DesktopWindow Window;
        internal readonly DesktopWindow Reopened;
        internal readonly Dictionary<TerminalPane, Process> Processes = [];
        private readonly HashSet<string> profiles = [];
        internal Fixture(ShellLaunch source, bool sequence)
        {
            var launch = source with { Directory = Root, Arguments = ShellLaunch.PowerShellArguments(source.Arguments[^1], noProfile: true) };
            var seed = new SessionSnapshot(1, sequence ? int.MaxValue - 2 : 64, 0, [
                new("Capacity terminal", 0, new("terminal", Directory: Root)),
                new("Capacity browser", 0, new("browser", Url: Server.Url("capacity-target"))),
            ]);
            seed.Validate();
            using (var store = new SessionStore(Path)) { _ = store.Open(); store.Save(seed); }
            Window = new(launch, new MemoryClipboard(), new SessionStore(Path)) { ShowInTaskbar = false };
            Reopened = new(launch, new MemoryClipboard(), new SessionStore(Path)) { ShowInTaskbar = false };
        }
        internal async Task Start()
        {
            Window.Show(); await Window.Started.WaitAsync(TimeSpan.FromSeconds(45)); Track(Window);
            var browser = Window.Workspaces.SelectMany(item => item.Panes).OfType<BrowserPane>().Single();
            await Program.Until(async () => await browser.Core.ExecuteScriptAsync("document.querySelector('#page')?.textContent") == "\"/capacity-target\"", "The owned capacity browser did not load its fixture.");
            foreach (var pane in Processes.Keys) await Program.Contains(pane, "CMUX_TEST_READY>");
        }
        internal void Track(DesktopWindow owner)
        {
            profiles.Add(owner.ProfilePath);
            if (owner.BrowserProfilePath is { } profile) profiles.Add(profile);
            foreach (var pane in owner.Workspaces.SelectMany(item => item.Panes).OfType<TerminalPane>())
            {
                if (Processes.ContainsKey(pane)) continue;
                var process = Process.GetProcessById(pane.ProcessId); _ = process.SafeHandle; Processes.Add(pane, process);
            }
        }
        internal async Task CloseNormally()
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Window.FormClosed += (_, _) => closed.TrySetResult(); Window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(45));
            Require(Processes.Values.All(process => process.WaitForExit(0)), "An owned capacity shell survived normal close.");
        }
        public async ValueTask DisposeAsync()
        {
            await Window.Shutdown().WaitAsync(TimeSpan.FromSeconds(45));
            await Reopened.Shutdown().WaitAsync(TimeSpan.FromSeconds(45));
            Window.Dispose(); Reopened.Dispose();
            foreach (var process in Processes.Values) { Require(process.WaitForExit(0), "An owned capacity shell survived shutdown."); process.Dispose(); }
            foreach (var profile in profiles) Require(!Directory.Exists(profile), "An owned capacity profile survived shutdown.");
            await Server.DisposeAsync();
            var full = System.IO.Path.GetFullPath(Root);
            Require(System.IO.Path.GetDirectoryName(full)!.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && System.IO.Path.GetFileName(full).StartsWith("cmux-workspace-capacity-", StringComparison.Ordinal), "Refusing to remove an unowned capacity fixture.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static async Task DirectMoveToNew(DesktopWindow window)
    {
        var method = typeof(DesktopWindow).GetMethod("MovePaneToNewWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(window, null)!;
    }
    private static async Task MoveToExisting(DesktopWindow window, WorkspaceView destination)
    {
        var strip = window.Controls.OfType<ToolStrip>().Single(item => item is not StatusStrip);
        var menu = strip.Items.OfType<ToolStripDropDownButton>().Single(item => item.Name == "MovePaneToWorkspace");
        Require(menu.Enabled, "A full creation budget disabled existing-workspace destinations.");
        if (menu.IsOnOverflow) strip.OverflowButton.ShowDropDown();
        menu.ShowDropDown();
        var index = Array.IndexOf(window.Workspaces.ToArray(), destination);
        var label = new Strings().Format("WorkspaceDestination", index + 1, destination.Title);
        var row = menu.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.AccessibleName == label);
        Require(row.Enabled, "An existing workspace destination was disabled by the creation budget.");
        row.PerformClick(); await window.PendingAction;
        Require(window.LastError is null, "The existing-workspace transfer reported an error.");
    }
    private static string Serialize(DesktopWindow window) => JsonSerializer.Serialize(window.CaptureSession());
    private static bool IsValid(SessionSnapshot snapshot) { try { snapshot.Validate(); return true; } catch (InvalidDataException) { return false; } }
    private static void Pass(string name, string shell, List<string> passed) { var value = "workspace-capacity-" + shell + "-" + name; passed.Add(value); Console.WriteLine("passed: " + value); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

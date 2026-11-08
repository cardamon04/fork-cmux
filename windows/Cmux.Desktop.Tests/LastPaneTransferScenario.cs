using System.Diagnostics;
using System.Text.Json;
using Cmux.Desktop;

internal static class LastPaneTransferScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed)
    {
        foreach (var browser in new[] { false, true }) await Live(source, "pwsh", browser, passed);
        var fallback = source with { Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") };
        foreach (var browser in new[] { false, true }) await Live(fallback, "windows-powershell", browser, passed);
    }

    private static async Task Live(ShellLaunch source, string shell, bool browser, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-last-pane-").FullName;
        var nextDirectory = Directory.CreateDirectory(Path.Combine(root, "移動後 & 🍀")).FullName;
        var path = Path.Combine(root, "layout.json");
        await using var server = new BrowserFixture();
        var launch = source with { Directory = root, Arguments = ShellLaunch.PowerShellArguments(source.Arguments[^1], noProfile: true) };
        const string name = "同じ名前 & 🍀";
        var seed = new SessionSnapshot(1, 3, 0, [
            new(name, 0, browser ? new("browser", Url: server.Url("last-source")) : new("terminal", Directory: root)),
            new(name, 1, new("split", Orientation: "vertical", Ratio: .45,
                First: new("browser", Url: server.Url("target-neighbor")), Second: new("terminal", Directory: root))),
            new("Other", 0, new("terminal", Directory: root)),
        ]);
        using (var store = new SessionStore(path)) { _ = store.Open(); store.Save(seed); }
        var initialFile = File.ReadAllBytes(path);
        using var window = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        using var restored = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        var processes = new Dictionary<TerminalPane, Process>();
        var profiles = new HashSet<string>();
        void Track(DesktopWindow owner)
        {
            profiles.Add(owner.ProfilePath);
            if (owner.BrowserProfilePath is { } profile) profiles.Add(profile);
            foreach (var pane in owner.Workspaces.SelectMany(workspace => workspace.Panes).OfType<TerminalPane>())
            {
                if (processes.ContainsKey(pane)) continue;
                var process = Process.GetProcessById(pane.ProcessId); _ = process.SafeHandle; processes.Add(pane, process);
            }
        }
        void Pass(string value)
        {
            var result = "last-pane-" + shell + "-" + (browser ? "browser-" : "terminal-") + value;
            passed.Add(result); Console.WriteLine("passed: " + result);
        }
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(45));
            Track(window);
            var workspaces = window.Workspaces.ToArray();
            var original = workspaces[0];
            var destination = workspaces[1];
            var other = workspaces[2];
            var moved = original.Active!;
            var core = moved is BrowserPane page ? page.Core : ((TerminalPane)moved).Core;
            var target = (TerminalPane)destination.Active!;
            var targetTree = destination.Controls[0];
            var targetParent = target.Parent!;
            foreach (var pane in processes.Keys) await Program.Contains(pane, "CMUX_TEST_READY>");
            if (browser)
            {
                await Program.Until(async () => await core.ExecuteScriptAsync("document.querySelector('#page')?.textContent") == "\"/last-source\"", "The sole browser did not load its owned fixture.");
                await core.ExecuteScriptAsync("window.cmuxLastPaneState=41");
            }
            else
            {
                await Program.Command((TerminalPane)moved, "$global:cmuxLastPane = 'LIVE_' + 'LAST_PANE'; Write-Output ('LAST_' + 'ASSIGN_READY')");
                await Program.Contains((TerminalPane)moved, "LAST_ASSIGN_READY");
            }
            var before = JsonSerializer.Serialize(window.CaptureSession());
            window.CaptureSession().Validate();
            Require(original.Panes.Count == 1 && original.Controls[0] == moved && destination.Panes.Count == 2, "The last-pane native fixture is not a valid sole source.");
            Console.WriteLine("LAST_PANE_PRECONDITION " + JsonSerializer.Serialize(new { browser, sourcePanes = original.Panes.Count, destinationPanes = destination.Panes.Count, validBeforeMove = true }));
            var menu = Open(window);
            var row = Row(window, menu, destination);
            Require(!Toolbar(window).Items["MovePaneToNewWorkspace"]!.Enabled, "A sole source offered a redundant new-workspace move.");
            Close(window, menu);
            Pass("native-single-source-menu");

            ControlEventHandler fail = (_, e) => { if (e.Control == moved) throw new InvalidOperationException("SYNTHETIC_LAST_PANE_ADOPTION_FAILURE"); };
            var installed = new List<Control>();
            ControlEventHandler install = (_, e) =>
            {
                if (e.Control is SplitContainer split) { split.Panel2.ControlAdded += fail; installed.Add(split.Panel2); }
            };
            targetParent.ControlAdded += install;
            try { row.PerformClick(); await window.PendingAction; }
            finally { targetParent.ControlAdded -= install; foreach (var control in installed) control.ControlAdded -= fail; }
            Require(window.LastError?.Contains("SYNTHETIC_LAST_PANE_ADOPTION_FAILURE", StringComparison.Ordinal) == true
                && window.Workspaces.SequenceEqual(workspaces) && window.Current == original && original.Active == moved
                && original.Controls[0] == moved && moved.Parent == original && moved.ContainsFocus
                && destination.Controls[0] == targetTree && !original.IsDisposed && !moved.IsDisposed && !targetTree.IsDisposed
                && JsonSerializer.Serialize(window.CaptureSession()) == before && processes.Values.All(process => !process.WaitForExit(0)),
                "A failed last-pane adoption lost a live source, destination, sidebar position or selection.");
            if (browser) Require(await core.ExecuteScriptAsync("++window.cmuxLastPaneState") == "42", "Rollback recreated the sole browser page.");
            var expectedError = window.LastError;
            Pass("native-adoption-rollback-keeps-source");

            // Keep the drawn row while its destination changes sidebar index.
            menu = Open(window); row = Row(window, menu, destination); Close(window, menu);
            window.SelectWorkspace(1); await window.ClickAction("MoveWorkspaceDown");
            window.SelectWorkspace(0); moved.FocusPane();
            row.PerformClick(); await window.PendingAction;
            Require(window.LastError == expectedError && window.Workspaces.SequenceEqual(new[] { other, destination }) && window.Current == destination
                && destination.Active == moved && destination.Panes.Contains(moved) && destination.Panes.Contains(target)
                && destination.Controls[0] == targetTree && targetParent.Controls[0] is SplitContainer wrapper
                && wrapper.Panel1.Controls[0] == target && wrapper.Panel2.Controls[0] == moved
                && original.IsDisposed && !original.IsHandleCreated && original.Panes.Count == 0 && original.Controls.Count == 0
                && !moved.IsDisposed && !target.IsDisposed && processes.Values.All(process => !process.WaitForExit(0)),
                "The committed last-pane move lost live state, used a stale index or retained an empty source.");
            if (browser)
            {
                Require(ReferenceEquals(((BrowserPane)moved).Core, core) && await core.ExecuteScriptAsync("++window.cmuxLastPaneState") == "43", "Moving the sole browser recreated its controller/page.");
                var address = moved.Controls.OfType<ToolStrip>().First(strip => strip is not StatusStrip).Items.OfType<ToolStripTextBox>().Single();
                Require(address.TextBox.ContainsFocus && address.TextBox.SelectionLength == address.Text.Length, "The sole browser move lost address focus/selection.");
            }
            else
            {
                await Program.Command((TerminalPane)moved, "Write-Output $global:cmuxLastPane");
                await Program.Contains((TerminalPane)moved, "LIVE_LAST_PANE");
                await Program.Command((TerminalPane)moved, "Set-Location -LiteralPath '" + Quote(nextDirectory) + "'");
                await Program.Until(() => Task.FromResult(((TerminalPane)moved).WorkingDirectory == nextDirectory), "The transferred sole terminal stopped reporting CWD.");
            }
            Pass("live-transfer-retires-only-empty-source");

            row.PerformClick(); await window.PendingAction;
            Require(window.Current == destination && window.Workspaces.Count() == 2 && destination.Active == moved, "A stale row revived the retired source or moved another pane.");
            await Program.Command(target, "Write-Output ('TARGET_' + 'STILL_LIVE')");
            await Program.Contains(target, "TARGET_STILL_LIVE");
            if (!browser)
            {
                var gate = Path.Combine(root, "bell-gate");
                var ready = Path.Combine(root, "bell-ready");
                await Program.Command((TerminalPane)moved, "[IO.File]::WriteAllText('" + Quote(ready) + "','ready'); while (!(Test-Path -LiteralPath '" + Quote(gate) + "')) { Start-Sleep -Milliseconds 10 }; [Console]::Write([char]7)");
                await Program.Until(() => Task.FromResult(File.Exists(ready)), "The last-pane BEL producer was not ready.");
                window.SelectWorkspace(0); File.WriteAllText(gate, "release");
                await Program.Until(() => Task.FromResult(((TerminalPane)moved).HasUnread), "A sole terminal BEL lost its current owner.");
                var sidebar = window.Controls.OfType<ListBox>().Single();
                Require(sidebar.Items.Count == 2 && sidebar.GetItemText(sidebar.Items[0]) == "Other"
                    && sidebar.GetItemText(sidebar.Items[1]) == new Strings().Format("UnreadWorkspace", name), "A sole terminal BEL marked the wrong/retired workspace.");
            }
            window.SelectWorkspace(1); moved.FocusPane();
            Pass("stale-source-and-current-owner-isolation");

            Require(File.ReadAllBytes(path).SequenceEqual(initialFile) && window.CaptureSession().WorkspaceSequence == 3, "A last-pane transfer saved implicitly or changed workspace numbering.");
            var expected = JsonSerializer.Serialize(window.CaptureSession());
            await window.ClickAction("SaveSession");
            Require(window.LastError == expectedError && JsonSerializer.Serialize(JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))) == expected
                && !File.ReadAllText(path).Contains("LIVE_LAST_PANE", StringComparison.Ordinal)
                && !File.ReadAllText(path).Contains("cmuxLastPaneState", StringComparison.Ordinal), "The last-pane save expanded schema or retained the empty source.");
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.FormClosed += (_, _) => closed.TrySetResult(); window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Require(processes.Values.All(process => process.WaitForExit(0)), "A transferred process survived normal close.");
            restored.Show(); await restored.Started.WaitAsync(TimeSpan.FromSeconds(45)); Track(restored);
            var reopened = restored.Workspaces.ToArray();
            Require(reopened.Length == 2 && restored.Current == reopened[1] && reopened[1].Active?.Kind == moved.Kind
                && restored.CaptureSession().WorkspaceSequence == 3 && reopened[1].Panes.Count == 3,
                "Reopen revived an empty source or lost moved selection/order.");
            if (browser) Require(((BrowserPane)reopened[1].Active!).SessionAddress == server.Url("last-source"), "Reopen lost the moved browser URL.");
            else Require(((TerminalPane)reopened[1].Active!).WorkingDirectory == nextDirectory, "Reopen lost the sole terminal CWD.");
            Pass("existing-schema-normal-close-and-reopen");
            restored.SelectWorkspace(0); await restored.ClickAction("CloseWorkspace");
            Require(restored.Workspaces.Count() == 1 && !Toolbar(restored).Items["MovePaneToWorkspace"]!.Enabled,
                "A workspace with no destination offered a last-pane transfer.");
            Pass("no-destination-boundary");
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(45));
            await restored.Shutdown().WaitAsync(TimeSpan.FromSeconds(45));
            foreach (var process in processes.Values) { Require(process.WaitForExit(0), "An owned last-pane fixture shell survived shutdown."); process.Dispose(); }
            foreach (var profile in profiles) Require(!Directory.Exists(profile), "An owned last-pane profile survived shutdown.");
            var full = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(full)!.Equals(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("cmux-last-pane-", StringComparison.Ordinal), "Refusing to remove an unowned last-pane fixture.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static ToolStrip Toolbar(DesktopWindow window) => window.Controls.OfType<ToolStrip>().Single(strip => strip is not StatusStrip);
    private static ToolStripDropDownButton Open(DesktopWindow window)
    {
        var strip = Toolbar(window);
        var menu = strip.Items.OfType<ToolStripDropDownButton>().Single(item => item.Name == "MovePaneToWorkspace");
        Require(menu.Enabled, "The native existing-workspace menu rejects a valid sole live pane.");
        if (menu.IsOnOverflow) strip.OverflowButton.ShowDropDown();
        menu.ShowDropDown(); Require(menu.DropDown.Visible, "The sole-pane destination menu did not open."); return menu;
    }
    private static ToolStripMenuItem Row(DesktopWindow window, ToolStripDropDownButton menu, WorkspaceView destination)
    {
        var index = Array.IndexOf(window.Workspaces.ToArray(), destination);
        var label = new Strings().Format("WorkspaceDestination", index + 1, destination.Title);
        return menu.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.AccessibleName == label);
    }
    private static void Close(DesktopWindow window, ToolStripDropDownButton menu) { menu.HideDropDown(); Toolbar(window).OverflowButton.HideDropDown(); }
    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

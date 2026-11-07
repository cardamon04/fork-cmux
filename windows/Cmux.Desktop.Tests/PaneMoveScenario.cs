using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cmux.Desktop;

internal static class PaneMoveScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed)
    {
        await Integration(source, "pwsh", passed);
        await Failures(source, "pwsh", passed);
        var fallback = source with { Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") };
        await Integration(fallback, "windows-powershell", passed);
        await Failures(fallback, "windows-powershell", passed);
        foreach (var locale in new[] { "en", "ja", "de", "fr", "ar", "es", "zh-Hant", "zh-Hans", "ko" })
        {
            var strings = new Strings(CultureInfo.GetCultureInfo(locale));
            Require(!string.IsNullOrWhiteSpace(strings["MovePaneToWorkspace"])
                && strings.Format("WorkspaceDestination", 12, "日本語 & 🍀").Contains("12", StringComparison.Ordinal)
                && strings.Format("WorkspaceDestination", 12, "日本語 & 🍀").Contains("日本語 & 🍀", StringComparison.Ordinal),
                "A pane destination translation or placeholder is missing: " + locale);
        }
        Pass("nine-locale-actions", "all", passed);
    }

    private static async Task Integration(ShellLaunch source, string shell, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-pane-move-").FullName;
        var directories = Enumerable.Range(0, 5).Select(index => Directory.CreateDirectory(Path.Combine(root, index + " 日本語 & 🍀")).FullName).ToArray();
        var path = Path.Combine(root, "layout.json");
        const string duplicate = "同じ名前 & 🍀";
        await using var fixture = new BrowserFixture();
        var seed = new SessionSnapshot(1, 3, 0, [
            new(duplicate, 1, new("split", Orientation: "horizontal", Ratio: .55,
                First: new("terminal", Directory: directories[0]),
                Second: new("split", Orientation: "vertical", Ratio: .58,
                    First: new("terminal", Directory: directories[1]), Second: new("browser", Url: fixture.Url("move-live"))))),
            new(duplicate, 0, new("terminal", Directory: directories[2])),
            new("Other & 最後", 0, new("terminal", Directory: directories[3])),
        ]);
        using (var store = new SessionStore(path)) { _ = store.Open(); store.Save(seed); }
        var originalFile = File.ReadAllBytes(path);
        var launch = source with { Directory = root, Arguments = ShellLaunch.PowerShellArguments(source.Arguments[^1], noProfile: true) };
        using var window = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        using var restored = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        var processes = new Dictionary<TerminalPane, Process>();
        var profiles = new HashSet<string>();
        void Track(DesktopWindow target)
        {
            profiles.Add(target.ProfilePath);
            if (target.BrowserProfilePath is { } browserProfile) profiles.Add(browserProfile);
            foreach (var terminal in target.Workspaces.SelectMany(workspace => workspace.Panes).OfType<TerminalPane>())
            {
                if (processes.ContainsKey(terminal)) continue;
                var process = Process.GetProcessById(terminal.ProcessId); _ = process.SafeHandle;
                processes.Add(terminal, process);
            }
        }
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            Track(window);
            var initial = window.Workspaces.ToArray();
            var sourceWorkspace = initial[0];
            var destination = initial[1];
            var remaining = (TerminalPane)sourceWorkspace.Panes[0];
            var moved = (TerminalPane)sourceWorkspace.Active!;
            var target = (TerminalPane)destination.Active!;
            var browser = sourceWorkspace.Panes.OfType<BrowserPane>().Single();
            foreach (var terminal in processes.Keys) await Program.Contains(terminal, "CMUX_TEST_READY>");
            await Program.Until(async () => await browser.Core.ExecuteScriptAsync("document.querySelector('#page')?.textContent") == "\"/move-live\"", "The move browser did not finish fixture navigation.");
            var core = browser.Core;
            await core.ExecuteScriptAsync("window.browserMoveState = 41");
            await Program.Command(moved, "$global:cmuxMoveMemory = 'MOVE_PRIVATE_' + 'MEMORY'");
            await Program.Contains(moved, "CMUX_TEST_READY>");
            var outer = (SplitContainer)sourceWorkspace.Controls[0];
            var removedSplit = (SplitContainer)outer.Panel2.Controls[0];
            var menu = Open(window);
            var rows = menu.DropDownItems.OfType<ToolStripMenuItem>().ToArray();
            var label = new Strings().Format("WorkspaceDestination", 2, duplicate);
            Require(rows.Length == 2 && rows[0].AccessibleName == label && rows[0].Text == label.Replace("&", "&&", StringComparison.Ordinal),
                "The native destination rows do not distinguish duplicate literal workspace names.");
            var drawn = rows[0];
            Close(window, menu);
            // Change sidebar positions while retaining the displayed row object.
            window.SelectWorkspace(1);
            await window.ClickAction("MoveWorkspaceDown");
            window.SelectWorkspace(0);
            moved.FocusPane();
            drawn.PerformClick();
            await window.PendingAction;
            Require(window.LastError is null && window.Current == destination && destination.Active == moved
                && initial[2].Panes.Count == 1 && window.Workspaces.SequenceEqual(new[] { sourceWorkspace, initial[2], destination }),
                "The captured destination row moved to a new sidebar index instead of its displayed workspace.");
            Require(sourceWorkspace.Controls[0] == outer && outer.Panel1.Controls[0] == remaining && outer.Panel2.Controls[0] == browser
                && removedSplit.IsDisposed && sourceWorkspace.Active == remaining && sourceWorkspace.Panes.SequenceEqual(new WorkspacePane[] { remaining, browser }),
                "Moving a live pane lost a source sibling, split identity or selection.");
            Require(destination.Controls[0] is SplitContainer split && split.Orientation == Orientation.Vertical
                && split.Panel1.Controls[0] == target && split.Panel2.Controls[0] == moved
                && destination.Panes.SequenceEqual(new[] { target, moved }) && !moved.IsDisposed,
                "The existing destination did not adopt the original live pane beside its selected pane.");
            Pass("native-literal-menu-and-captured-target", shell, passed);
            await Program.Command(moved, "Write-Output $global:cmuxMoveMemory");
            await Program.Contains(moved, "MOVE_PRIVATE_MEMORY");
            await Program.Command(moved, "Set-Location -LiteralPath '" + Quote(directories[4]) + "'");
            await Program.Until(() => Task.FromResult(moved.WorkingDirectory == directories[4]), "The moved shell stopped reporting its actual directory.");
            Require(processes.All(item => !item.Value.WaitForExit(0)), "A transfer restarted or terminated an existing shell.");
            Pass("terminal-memory-cwd-and-source-collapse", shell, passed);

            window.SelectWorkspace(0);
            browser.FocusPane();
            menu = Open(window);
            Row(window, menu, destination).PerformClick();
            await window.PendingAction;
            Require(window.LastError is null && window.Current == destination && destination.Active == browser
                && sourceWorkspace.Controls[0] == remaining && sourceWorkspace.Panes.SequenceEqual(new[] { remaining })
                && destination.Panes.SequenceEqual(new WorkspacePane[] { target, moved, browser })
                && ReferenceEquals(browser.Core, core) && await core.ExecuteScriptAsync("++window.browserMoveState") == "42",
                "Moving into a nested destination recreated a browser or lost its source/destination neighbors.");
            var address = browser.Controls.OfType<ToolStrip>().First(item => item is not StatusStrip).Items.OfType<ToolStripTextBox>().Single();
            Require(address.TextBox.ContainsFocus && address.TextBox.SelectionLength == address.Text.Length,
                "The moved browser did not select its own address field.");
            Pass("browser-native-state-nested-layout-and-address-focus", shell, passed);

            var gate = Path.Combine(root, "bell-gate");
            var ready = Path.Combine(root, "bell-ready");
            await Program.Command(moved, "[IO.File]::WriteAllText('" + Quote(ready) + "','ready'); while (!(Test-Path -LiteralPath '" + Quote(gate) + "')) { Start-Sleep -Milliseconds 10 }; [Console]::Write([char]7)");
            await Program.Until(() => Task.FromResult(File.Exists(ready)), "The moved BEL producer was not ready.");
            window.SelectWorkspace(0);
            remaining.FocusPane();
            File.WriteAllText(gate, "release");
            await Program.Until(() => Task.FromResult(moved.HasUnread), "The moved terminal BEL did not reach its current owner.");
            var sidebar = window.Controls.OfType<ListBox>().Single();
            var destinationIndex = Array.IndexOf(window.Workspaces.ToArray(), destination);
            Require(sidebar.GetItemText(sidebar.Items[0]) == duplicate
                && sidebar.GetItemText(sidebar.Items[destinationIndex]) == new Strings().Format("UnreadWorkspace", duplicate),
                "A moved BEL marked its former workspace or missed its new workspace.");
            Pass("real-bell-current-owner-after-transfer", shell, passed);
            Require(!Toolbar(window).Items["MovePaneToWorkspace"]!.Enabled, "A sole source pane offered a split-only transfer.");
            await window.ClickAction("CloseWorkspace");
            Require(processes[remaining].WaitForExit(0) && !processes[moved].WaitForExit(0)
                && await core.ExecuteScriptAsync("++window.browserMoveState") == "43", "Closing the source ended a transferred live resource.");
            Pass("source-close-isolation", shell, passed);

            window.SelectWorkspace(Array.IndexOf(window.Workspaces.ToArray(), destination));
            browser.FocusPane();
            Require(File.ReadAllBytes(path).SequenceEqual(originalFile), "A transfer implicitly saved layout or history.");
            var expected = JsonSerializer.Serialize(window.CaptureSession());
            await window.ClickAction("SaveSession");
            Require(File.ReadAllText(path).Contains("\"Version\": 1", StringComparison.Ordinal)
                && JsonSerializer.Serialize(JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))) == expected
                && !File.ReadAllText(path).Contains("MOVE_PRIVATE_MEMORY", StringComparison.Ordinal)
                && !File.ReadAllText(path).Contains("browserMoveState", StringComparison.Ordinal), "Transfer save changed schema or persisted shell/browser memory.");
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.FormClosed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Require(JsonSerializer.Serialize(JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))) == expected
                && processes.Values.All(process => process.WaitForExit(0)), "Normal close lost the transfer or leaked an original shell.");
            Pass("existing-schema-save-and-normal-close", shell, passed);
            restored.Show();
            await restored.Started.WaitAsync(TimeSpan.FromSeconds(30));
            Track(restored);
            var reopened = restored.Workspaces.ToArray();
            Require(reopened.Length == 2 && restored.Current == reopened[1] && reopened[1].Active is BrowserPane
                && reopened[1].Panes.Count == 3 && reopened[1].Panes.OfType<TerminalPane>().Last().WorkingDirectory == directories[4]
                && reopened[1].Panes.OfType<BrowserPane>().Single().SessionAddress == fixture.Url("move-live"), "Reopen lost transferred order, directory, browser URL or selection.");
            Pass("reopen-order-layout-cwd-and-browser-selection", shell, passed);
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            await restored.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var process in processes.Values) { Require(process.WaitForExit(0), "An owned move fixture shell survived shutdown."); process.Dispose(); }
            foreach (var profile in profiles) Require(!Directory.Exists(profile), "An owned move profile survived shutdown.");
            Delete(root);
        }
    }

    private static async Task Failures(ShellLaunch source, string shell, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-pane-move-").FullName;
        using var window = new DesktopWindow(source with { Directory = root }, new MemoryClipboard()) { ShowInTaskbar = false };
        var processes = new List<Process>();
        string? profile = null;
        void Track(TerminalPane pane) { var process = Process.GetProcessById(pane.ProcessId); _ = process.SafeHandle; processes.Add(process); }
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = window.ProfilePath;
            var original = window.Current!;
            Track((TerminalPane)original.Active!);
            await window.NewWorkspace();
            var destination = window.Current!;
            var target = (TerminalPane)destination.Active!;
            Track(target);
            window.SelectWorkspace(0);
            await window.ClickAction("SplitRight");
            var moved = (TerminalPane)original.Active!;
            Track(moved);
            foreach (var pane in window.Workspaces.SelectMany(workspace => workspace.Panes).OfType<TerminalPane>()) await Program.Contains(pane, "CMUX_TEST_READY>");
            var sourceTree = original.Controls[0];
            var targetTree = destination.Controls[0];
            var before = JsonSerializer.Serialize(window.CaptureSession());
            ControlEventHandler fail = (_, e) => { if (e.Control == moved) throw new InvalidOperationException("SYNTHETIC_EXISTING_ADOPTION_FAILURE"); };
            var installed = new List<Control>();
            ControlEventHandler install = (_, e) =>
            {
                if (e.Control is SplitContainer split) { split.Panel2.ControlAdded += fail; installed.Add(split.Panel2); }
            };
            destination.ControlAdded += install;
            try { Row(window, Open(window), destination).PerformClick(); await window.PendingAction; }
            finally { destination.ControlAdded -= install; foreach (var control in installed) control.ControlAdded -= fail; }
            Require(window.LastError?.Contains("SYNTHETIC_EXISTING_ADOPTION_FAILURE", StringComparison.Ordinal) == true,
                "A native adoption failure did not reach the shared error boundary.");
            Require(original.Controls[0] == sourceTree && destination.Controls[0] == targetTree && !sourceTree.IsDisposed
                && !targetTree.IsDisposed && window.Current == original && original.Active == moved && moved.ContainsFocus
                && JsonSerializer.Serialize(window.CaptureSession()) == before && processes.All(process => !process.WaitForExit(0)),
                "Failed transfer lost live native resources, layout, owners or original selection.");
            Pass("native-adoption-failure-restores-both-live-trees", shell, passed);
            Row(window, Open(window), destination).PerformClick();
            await window.PendingAction;
            Require(window.Current == destination && destination.Active == moved && sourceTree.IsDisposed && !moved.IsDisposed,
                "Retry did not adopt the original live pane.");
            await Program.Command(moved, "Write-Output ('MOVE_' + 'RETRY_LIVE')");
            await Program.Contains(moved, "MOVE_RETRY_LIVE");
            Pass("native-adoption-retry-preserves-live-shell", shell, passed);

            var menu = Open(window);
            var stale = Row(window, menu, original);
            Close(window, menu);
            window.SelectWorkspace(0);
            await window.ClickAction("CloseWorkspace");
            before = JsonSerializer.Serialize(window.CaptureSession());
            stale.PerformClick();
            await window.PendingAction;
            Require(JsonSerializer.Serialize(window.CaptureSession()) == before && !moved.IsDisposed, "A closed destination was revived by a stale row.");
            Pass("closed-destination-row-is-no-op", shell, passed);
            await window.NewWorkspace();
            Track((TerminalPane)window.Current!.Active!);
            var later = window.Current!;
            window.SelectWorkspace(0);
            moved.FocusPane();
            menu = Open(window);
            stale = Row(window, menu, later);
            Close(window, menu);
            target.FocusPane();
            before = JsonSerializer.Serialize(window.CaptureSession());
            stale.PerformClick();
            await window.PendingAction;
            Require(JsonSerializer.Serialize(window.CaptureSession()) == before && destination.Active == target,
                "A stale menu transferred the newly selected source pane.");
            Pass("changed-source-selection-row-is-no-op", shell, passed);
            moved.FocusPane();
            await window.ClickAction("ClosePane");
            before = JsonSerializer.Serialize(window.CaptureSession());
            stale.PerformClick();
            await window.PendingAction;
            Require(JsonSerializer.Serialize(window.CaptureSession()) == before && !target.IsDisposed && moved.IsDisposed,
                "A closed source pane was transferred by a stale row.");
            Pass("closed-source-row-is-no-op", shell, passed);
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var process in processes) { Require(process.WaitForExit(0), "An owned failed-transfer shell survived shutdown."); process.Dispose(); }
            if (profile is not null) Require(!Directory.Exists(profile), "A failed-transfer fixture profile survived shutdown.");
            Delete(root);
        }
    }

    private static ToolStrip Toolbar(DesktopWindow window) => window.Controls.OfType<ToolStrip>().Single(strip => strip is not StatusStrip);
    private static ToolStripDropDownButton Open(DesktopWindow window)
    {
        var strip = Toolbar(window);
        var button = strip.Items.OfType<ToolStripDropDownButton>().SingleOrDefault(item => item.Name == "MovePaneToWorkspace");
        Require(button is { Enabled: true }, "The native move-to-existing-workspace action is missing or disabled for a live split.");
        if (button!.IsOnOverflow) strip.OverflowButton.ShowDropDown();
        button.ShowDropDown();
        Require(button.DropDown.Visible, "The existing-workspace menu did not open.");
        return button;
    }
    private static ToolStripMenuItem Row(DesktopWindow window, ToolStripDropDownButton menu, WorkspaceView destination)
    {
        var index = Array.IndexOf(window.Workspaces.ToArray(), destination);
        var label = new Strings().Format("WorkspaceDestination", index + 1, destination.Title);
        return menu.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.AccessibleName == label);
    }
    private static void Close(DesktopWindow window, ToolStripDropDownButton menu) { menu.HideDropDown(); Toolbar(window).OverflowButton.HideDropDown(); }
    private static string Quote(string text) => text.Replace("'", "''", StringComparison.Ordinal);
    private static void Pass(string name, string shell, List<string> passed) { var value = "pane-move-" + shell + "-" + name; passed.Add(value); Console.WriteLine("passed: " + value); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Delete(string root)
    {
        var full = Path.GetFullPath(root);
        Require(Path.GetDirectoryName(full)!.Equals(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("cmux-pane-move-", StringComparison.Ordinal), "Refusing to delete an unowned fixture directory.");
        Directory.Delete(full, recursive: true);
    }
}

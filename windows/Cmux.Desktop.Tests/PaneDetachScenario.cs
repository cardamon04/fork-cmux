using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cmux.Desktop;

internal static class PaneDetachScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed, string? artifacts)
    {
        await Regression(source, passed);
        await Integration(source, passed, artifacts);
        await AdoptionFailure(source, passed);
        foreach (var locale in new[] { "en", "ja", "de", "fr", "ar", "es", "zh-Hant", "zh-Hans", "ko" })
            Require(new Strings(CultureInfo.GetCultureInfo(locale))["MovePaneToNewWorkspace"].Length > 0, "A detach translation is missing: " + locale);
        Pass("pane-detach-nine-locale-action", passed);
    }

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
            Pass("pane-detach-native-terminal-identity-state-and-new-owner", passed);
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var process in processes) { Require(process.WaitForExit(0), "An original detach shell survived shutdown."); process.Dispose(); }
            if (profile is not null) Require(!Directory.Exists(profile), "The detach fixture profile survived shutdown.");
            DeleteFixture(root);
        }
    }

    private static async Task Integration(ShellLaunch source, List<string> passed, string? artifacts)
    {
        var root = Directory.CreateTempSubdirectory("cmux-pane-detach-").FullName;
        var directories = new[] { "first 日本語", "moved & 🍀", "other", "changed 日本語", "later" }
            .Select(name => Directory.CreateDirectory(Path.Combine(root, name)).FullName).ToArray();
        var path = Path.Combine(root, "layout.json");
        await using var fixture = new BrowserFixture();
        const string title = "同じ名前 & 🍀";
        var seed = new SessionSnapshot(1, 2, 0, [
            new(title, 1, new("split", Orientation: "vertical", Ratio: .56,
                First: new("terminal", Directory: directories[0]),
                Second: new("split", Orientation: "horizontal", Ratio: .61,
                    First: new("terminal", Directory: directories[1]), Second: new("browser", Url: fixture.Url("pane-detach"))))),
            new(title, 0, new("terminal", Directory: directories[2])),
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
            foreach (var pane in target.Workspaces.SelectMany(workspace => workspace.Panes).OfType<TerminalPane>())
            {
                if (processes.ContainsKey(pane)) continue;
                var process = Process.GetProcessById(pane.ProcessId);
                _ = process.SafeHandle;
                processes.Add(pane, process);
            }
        }
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            Track(window);
            var initial = window.Workspaces.ToArray();
            var original = initial[0];
            var remaining = original.Panes.OfType<TerminalPane>().First();
            var moved = (TerminalPane)original.Active!;
            var other = (TerminalPane)initial[1].Active!;
            var browser = original.Panes.OfType<BrowserPane>().Single();
            foreach (var terminal in new[] { remaining, moved, other }) await Program.Contains(terminal, "CMUX_TEST_READY>");
            // Establish actual prompt reports before attributing a later cd
            // failure to moving. The general desktop fixture has no reporter.
            await Program.Command(moved, "Set-Location -LiteralPath '" + directories[3].Replace("'", "''", StringComparison.Ordinal) + "'");
            await Program.Until(() => Task.FromResult(moved.WorkingDirectory == directories[3]), "The pre-move integrated directory report was not ready.");
            await Program.Command(moved, "Set-Location -LiteralPath '" + directories[1].Replace("'", "''", StringComparison.Ordinal) + "'");
            await Program.Until(() => Task.FromResult(moved.WorkingDirectory == directories[1]), "The pre-move directory reset was not reported.");
            await Program.Until(async () => await browser.Core.ExecuteScriptAsync("document.querySelector('#page')?.textContent") == "\"/pane-detach\"", "The detach browser precondition was not ready.");
            var core = browser.Core;
            await core.ExecuteScriptAsync("window.paneDetachState = 41");
            await Program.Command(moved, "$global:cmuxDetachMemory = 'DETACH_PRIVATE_' + 'MEMORY'");
            var outer = (SplitContainer)original.Controls[0];
            var removedSplit = (SplitContainer)outer.Panel2.Controls[0];
            await window.ClickAction("MovePaneToNewWorkspace");
            var terminalWorkspace = window.Current!;
            Require(window.Workspaces.SequenceEqual(new[] { original, initial[1], terminalWorkspace })
                && terminalWorkspace.Title == new Strings().Format("Workspace", 3), "Detach changed neighbor identities or the naming sequence.");
            Require(original.Controls[0] == outer && outer.Panel1.Controls[0] == remaining && outer.Panel2.Controls[0] == browser
                && removedSplit.IsDisposed && !moved.IsDisposed && terminalWorkspace.Controls[0] == moved,
                "Nested detach replaced an unrelated split or disposed a live pane.");
            await Program.Command(moved, "Set-Location -LiteralPath '" + directories[3].Replace("'", "''", StringComparison.Ordinal) + "'");
            await Program.Until(() => Task.FromResult(moved.WorkingDirectory == directories[3]), "A moved pane stopped reporting cd.");
            var directoryLabel = window.Controls.OfType<StatusStrip>().Single().Items.Find("WorkingDirectory", false).Single();
            Require(directoryLabel.AccessibleDescription!.Contains(directories[3], StringComparison.Ordinal), "Moved cd did not update the selected pane's status.");
            await Program.Command(moved, "Write-Output $global:cmuxDetachMemory");
            await Program.Contains(moved, "DETACH_PRIVATE_MEMORY");
            Pass("pane-detach-nested-split-neighbors-cwd-and-live-memory", passed);

            window.SelectWorkspace(0);
            browser.FocusPane();
            Require(original.Active == browser, "The browser precondition was not selected.");
            await window.ClickAction("MovePaneToNewWorkspace");
            var browserWorkspace = window.Current!;
            Require(browserWorkspace != terminalWorkspace && browserWorkspace.Controls[0] == browser && original.Controls[0] == remaining
                && original.Active == remaining && original.Panes.SequenceEqual(new[] { remaining }), "Browser detach did not collapse the source around its original terminal.");
            Require(ReferenceEquals(browser.Core, core) && await core.ExecuteScriptAsync("window.paneDetachState") == "41", "Browser detach recreated or navigated its native page.");
            var address = browser.Controls.OfType<ToolStrip>().First(strip => strip is not StatusStrip).Items.OfType<ToolStripTextBox>().Single();
            Require(address.TextBox.ContainsFocus && address.TextBox.SelectionLength == address.Text.Length, "Browser detach did not select its owned address field.");
            Require(!window.IsActionEnabled("MovePaneToNewWorkspace"), "A sole browser could be detached again.");
            Pass("pane-detach-live-browser-address-intent-and-source-collapse", passed);

            await window.ClickAction("SplitBelow");
            Track(window);
            var temporary = (TerminalPane)browserWorkspace.Active!;
            await Program.Contains(temporary, "CMUX_TEST_READY>");
            browser.FocusPane();
            Require(browserWorkspace.Active == browser && window.Current == browserWorkspace, "Moved browser activation retained the old owner.");
            temporary.FocusPane();
            await window.ClickAction("ClosePane");
            Require(browserWorkspace.Panes.SequenceEqual(new[] { browser }) && browserWorkspace.Active == browser && processes[temporary].WaitForExit(0),
                "Shared split removal closed the moved browser or leaked its neighbor.");
            window.SelectWorkspace(0);
            await window.ClickAction("CloseWorkspace");
            Require(processes[remaining].WaitForExit(0) && !processes[moved].WaitForExit(0) && !processes[other].WaitForExit(0),
                "Closing the source closed a moved/other shell or retained the original one.");
            await Program.Command(moved, "Write-Output ('DETACH_' + 'AFTER_SOURCE_CLOSE')");
            await Program.Contains(moved, "DETACH_AFTER_SOURCE_CLOSE");
            Require(await core.ExecuteScriptAsync("++window.paneDetachState") == "42", "Source disposal killed the moved browser.");
            window.SelectWorkspace(Array.IndexOf(window.Workspaces.ToArray(), browserWorkspace));
            Pass("pane-detach-browser-new-owner-and-source-close-isolation", passed);

            Require(File.ReadAllBytes(path).SequenceEqual(originalFile), "Detachment saved without an explicit save or close.");
            var expected = window.CaptureSession();
            await window.ClickAction("SaveSession");
            var saved = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))!;
            Require(JsonSerializer.Serialize(saved) == JsonSerializer.Serialize(expected), "Explicit save lost moved ownership, directory, order, selection or layout.");
            Require(saved.Version == 1 && saved.Workspaces.Length == 3 && saved.SelectedWorkspace == 2
                && saved.Workspaces[1].Layout.Directory == directories[3] && saved.Workspaces[2].Layout.Url == fixture.Url("pane-detach"),
                "Detachment changed the existing schema or saved wrong pane data.");
            Require(!File.ReadAllText(path).Contains("DETACH_PRIVATE_MEMORY", StringComparison.Ordinal)
                && !File.ReadAllText(path).Contains("paneDetachState", StringComparison.Ordinal), "Detachment expanded storage to shell/browser memory.");
            Pass("pane-detach-explicit-save-existing-schema-and-no-implicit-write", passed);

            window.SelectWorkspace(Array.IndexOf(window.Workspaces.ToArray(), terminalWorkspace));
            await window.ClickAction("SplitRight");
            Track(window);
            var later = (TerminalPane)terminalWorkspace.Active!;
            await Program.Contains(later, "CMUX_TEST_READY>");
            await Program.Command(later, "Set-Location -LiteralPath '" + directories[4].Replace("'", "''", StringComparison.Ordinal) + "'");
            await Program.Until(() => Task.FromResult(later.WorkingDirectory == directories[4]), "The later fixture directory was not ready.");
            moved.FocusPane();
            await window.ClickAction("MovePaneToNewWorkspace");
            Require(window.Current!.Active == moved && window.Current != terminalWorkspace && terminalWorkspace.Active == later,
                "A second transfer lost ownership or the source's remaining shell.");
            var latest = window.CaptureSession();
            if (artifacts is not null)
            {
                Directory.CreateDirectory(artifacts);
                Capture(window.Controls.OfType<ListBox>().Single(), Path.Combine(artifacts, "pane-detach-workspaces.png"));
                Capture(window.Controls.OfType<ToolStrip>().First(strip => strip is not StatusStrip), Path.Combine(artifacts, "pane-detach-controls.png"));
            }
            var closed = Closed(window);
            window.Close();
            await closed.WaitAsync(TimeSpan.FromSeconds(30));
            var normal = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))!;
            Require(JsonSerializer.Serialize(normal) == JsonSerializer.Serialize(latest) && processes.Values.All(process => process.WaitForExit(0)),
                "Normal close lost the latest transfer or retained an original shell.");
            Pass("pane-detach-second-transfer-and-normal-close-latest-state", passed);

            restored.Show();
            await restored.Started.WaitAsync(TimeSpan.FromSeconds(30));
            Track(restored);
            var reopened = restored.Workspaces.ToArray();
            Require(reopened.Length == 4 && restored.Current == reopened[3] && reopened[3].Active is TerminalPane
                && ((TerminalPane)reopened[0].Active!).WorkingDirectory == directories[2]
                && ((TerminalPane)reopened[1].Active!).WorkingDirectory == directories[4]
                && ((TerminalPane)reopened[3].Active!).WorkingDirectory == directories[3], "Reopen lost transferred directory/order/selection.");
            var reopenedBrowser = (BrowserPane)reopened[2].Active!;
            await Program.Until(async () => await reopenedBrowser.Core.ExecuteScriptAsync("document.querySelector('#page')?.textContent") == "\"/pane-detach\"", "Moved browser URL did not reopen.");
            Require(await reopenedBrowser.Core.ExecuteScriptAsync("typeof window.paneDetachState") == "\"undefined\"", "Browser memory was unexpectedly persisted.");
            Pass("pane-detach-reopen-directory-browser-url-order-and-selection", passed);

            var pending = restored.PendingAction;
            var state = JsonSerializer.Serialize(restored.CaptureSession());
            await restored.ClickAction("MovePaneToNewWorkspace");
            Require(ReferenceEquals(pending, restored.PendingAction) && state == JsonSerializer.Serialize(restored.CaptureSession()), "A disabled sole-pane action ran.");
            while (restored.Current is not null) await restored.ClickAction("CloseWorkspace");
            Require(!restored.IsActionEnabled("MovePaneToNewWorkspace"), "Empty state enabled pane movement.");
            pending = restored.PendingAction;
            await restored.ClickAction("MovePaneToNewWorkspace");
            Require(ReferenceEquals(pending, restored.PendingAction) && !restored.Workspaces.Any(), "Empty detach created a workspace.");
            await restored.ClickAction("NewWorkspace");
            Track(restored);
            Require(restored.Current!.Title == new Strings().Format("Workspace", 6) && !restored.IsActionEnabled("MovePaneToNewWorkspace"), "Detach changed numbering or enabled a single new pane.");
            Require(window.LastError is null && restored.LastError is null, "Native detachment reported an unexpected error.");
            Pass("pane-detach-disabled-empty-single-and-numbering", passed);
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            await restored.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var process in processes.Values) { Require(process.WaitForExit(0), "A detach integration shell survived cleanup."); process.Dispose(); }
            foreach (var profile in profiles) Require(!Directory.Exists(profile), "A detach integration profile survived cleanup.");
            DeleteFixture(root);
        }
    }

    private static async Task AdoptionFailure(ShellLaunch source, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-pane-detach-").FullName;
        using var window = new DesktopWindow(source with { Directory = root }, new MemoryClipboard()) { ShowInTaskbar = false };
        var processes = new List<Process>();
        string? profile = null;
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = window.ProfilePath;
            var original = window.Current!;
            var first = (TerminalPane)original.Active!;
            var handle = Process.GetProcessById(first.ProcessId); _ = handle.SafeHandle; processes.Add(handle);
            await Program.Contains(first, "CMUX_TEST_READY>");
            await window.ClickAction("SplitBelow");
            var moved = (TerminalPane)original.Active!;
            handle = Process.GetProcessById(moved.ProcessId); _ = handle.SafeHandle; processes.Add(handle);
            await Program.Contains(moved, "CMUX_TEST_READY>");
            var tree = original.Controls[0];
            var before = JsonSerializer.Serialize(window.CaptureSession());
            var content = window.Controls.OfType<Panel>().Single(panel => panel.Controls.Contains(original));
            WorkspaceView? attempted = null;
            ControlEventHandler fail = (_, e) => { if (e.Control == moved) throw new InvalidOperationException("SYNTHETIC_ADOPTION_FAILURE"); };
            ControlEventHandler install = (_, e) => { if (e.Control is WorkspaceView created) { attempted = created; created.ControlAdded += fail; } };
            content.ControlAdded += install;
            try { await window.ClickAction("MovePaneToNewWorkspace"); }
            finally { content.ControlAdded -= install; if (attempted is not null) attempted.ControlAdded -= fail; }
            Require(window.LastError?.Contains("SYNTHETIC_ADOPTION_FAILURE", StringComparison.Ordinal) == true, "The native adoption failure did not reach the product error boundary.");
            Require(window.Workspaces.SequenceEqual(new[] { original }) && window.Current == original && original.Active == moved
                && original.Controls[0] == tree && !tree.IsDisposed && !moved.IsDisposed && moved.ContainsFocus
                && JsonSerializer.Serialize(window.CaptureSession()) == before, "Adoption failure lost layout/identity/selection or advanced the naming sequence.");
            await Program.Command(moved, "Write-Output ('DETACH_' + 'ROLLBACK_LIVE')");
            await Program.Contains(moved, "DETACH_ROLLBACK_LIVE");
            Require(processes.All(process => !process.WaitForExit(0)), "Rollback ended an original live shell.");
            Pass("pane-detach-native-adoption-failure-restores-live-tree-and-counter", passed);
            await window.ClickAction("MovePaneToNewWorkspace");
            Require(window.Workspaces.Count() == 2 && window.Current!.Active == moved
                && window.Current.Title == new Strings().Format("Workspace", 2) && tree.IsDisposed && !moved.IsDisposed,
                "Retry after a handled native failure did not transfer the same live pane.");
            await Program.Command(moved, "Write-Output ('DETACH_' + 'RETRY_LIVE')");
            await Program.Contains(moved, "DETACH_RETRY_LIVE");
            Pass("pane-detach-native-adoption-retry-retains-original-shell", passed);
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var process in processes) { Require(process.WaitForExit(0), "A detach failure fixture shell survived shutdown."); process.Dispose(); }
            if (profile is not null) Require(!Directory.Exists(profile), "The detach failure profile survived shutdown.");
            DeleteFixture(root);
        }
    }

    private static Task Closed(Form form)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        form.FormClosed += (_, _) => done.TrySetResult();
        return done.Task;
    }

    private static void Capture(Control control, string path)
    {
        using var image = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        image.Save(path);
    }

    private static void DeleteFixture(string directory)
    {
        var full = Path.GetFullPath(directory);
        Require(Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("cmux-pane-detach-", StringComparison.Ordinal), "Refusing to delete a non-fixture directory.");
        Directory.Delete(full, recursive: true);
    }

    private static void Pass(string name, List<string> passed) { passed.Add(name); Console.WriteLine("passed: " + name); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

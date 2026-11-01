using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Cmux.Desktop;

internal static class WorkspaceOrderScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed, string? artifacts)
    {
        await Regression(source, passed);
        await Integration(source, passed, artifacts);
        foreach (var locale in new[] { "en", "ja", "de", "fr", "ar", "es", "zh-Hant", "zh-Hans", "ko" })
        {
            var strings = new Strings(CultureInfo.GetCultureInfo(locale));
            Require(strings["MoveWorkspaceUp"].Length > 0 && strings["MoveWorkspaceDown"].Length > 0
                && strings["MoveWorkspaceUp"] != strings["MoveWorkspaceDown"], "Workspace ordering lacks localized distinct actions: " + locale);
        }
        Pass("workspace-order-nine-locale-actions", passed);
    }

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
            if (button is not { Enabled: true }) throw new InvalidOperationException("The selected workspace has no usable move-up action.");
            button.PerformClick();
            await window.PendingAction;
            Require(window.Workspaces.SequenceEqual(new[] { before[0], before[2], before[1] }), "Move up did not reorder the native sidebar workspace objects.");
            Require(NativeNames(window).SequenceEqual(new[] { before[0].Title, before[2].Title, before[1].Title }), "Native row text disagreed with reordered workspace objects.");
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

    private static async Task Integration(ShellLaunch source, List<string> passed, string? artifacts)
    {
        var root = Directory.CreateTempSubdirectory("cmux-workspace-order-").FullName;
        var directories = new[] { "first 日本語", "middle & 🍀", "last" }.Select(name => Directory.CreateDirectory(Path.Combine(root, name)).FullName).ToArray();
        var path = Path.Combine(root, "layout.json");
        await using var fixture = new BrowserFixture();
        const string title = "同じ名前 & 🍀";
        var snapshot = new SessionSnapshot(1, 3, 1, [
            new(title, 0, new("terminal", Directory: directories[0])),
            new(title, 1, new("split", Orientation: "vertical", Ratio: .63,
                First: new("terminal", Directory: directories[1]), Second: new("browser", Url: fixture.Url("workspace-order")))),
            new(title, 0, new("terminal", Directory: directories[2])),
        ]);
        using (var seed = new SessionStore(path)) { _ = seed.Open(); seed.Save(snapshot); }
        var originalFile = File.ReadAllText(path);
        using var window = new DesktopWindow(source with { Directory = root }, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        using var restored = new DesktopWindow(source with { Directory = root }, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
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
            var selected = initial[1];
            var browser = (BrowserPane)selected.Active!;
            var terminal = selected.Panes.OfType<TerminalPane>().Single();
            await Program.Until(async () => await browser.Core.ExecuteScriptAsync("document.querySelector('#page')?.textContent") == "\"/workspace-order\"", "The restored browser precondition was not ready.");
            await browser.Core.ExecuteScriptAsync("window.workspaceOrderState = 'SYNTHETIC_BROWSER_STATE'");
            Require(window.Current == selected && selected.Active == browser, "The selected mixed workspace was not restored.");
            var initialLayouts = initial.Select(workspace => SessionLayout.Capture(workspace).Layout).ToArray();
            Require(window.IsActionEnabled("MoveWorkspaceUp") && window.IsActionEnabled("MoveWorkspaceDown"), "A middle workspace lost one ordering action.");
            await window.ClickAction("MoveWorkspaceUp");
            Require(window.Workspaces.SequenceEqual(new[] { initial[1], initial[0], initial[2] }) && window.Current == selected && selected.Active == browser,
                "Move up changed mixed workspace identity, pane selection or relative neighbor order.");
            Require(!window.IsActionEnabled("MoveWorkspaceUp") && window.IsActionEnabled("MoveWorkspaceDown"), "Top boundary actions were wrong.");
            var pending = window.PendingAction;
            await window.ClickAction("MoveWorkspaceUp");
            Require(ReferenceEquals(pending, window.PendingAction), "A disabled boundary action ran.");
            await window.ClickAction("MoveWorkspaceDown");
            await window.ClickAction("MoveWorkspaceDown");
            Require(window.Workspaces.SequenceEqual(new[] { initial[0], initial[2], initial[1] }) && window.Current == selected && selected.Active == browser,
                "Move down did not preserve the selected mixed workspace.");
            Require(window.IsActionEnabled("MoveWorkspaceUp") && !window.IsActionEnabled("MoveWorkspaceDown"), "Bottom boundary actions were wrong.");
            Pass("workspace-order-both-directions-boundaries-and-duplicate-identity", passed);

            Require(NativeNames(window).SequenceEqual(Enumerable.Repeat(title, 3)), "Literal duplicate titles were damaged by reordering.");
            Require(await browser.Core.ExecuteScriptAsync("window.workspaceOrderState") == "\"SYNTHETIC_BROWSER_STATE\"", "Reordering recreated or navigated the live browser.");
            Require(selected.Active == browser && browser.ContainsFocus, "Reordering lost the owned logical browser focus.");
            Require(processes.Values.All(process => !process.WaitForExit(0)), "Reordering ended an original shell.");
            await Program.Command(terminal, "Write-Output ('ORDER_TERMINAL_'+ 'LIVE')");
            await Program.Contains(terminal, "ORDER_TERMINAL_LIVE");
            browser.FocusPane();
            Pass("workspace-order-preserves-live-browser-shell-and-native-literals", passed);

            var toolbar = window.Controls.OfType<ToolStrip>().First(strip => strip is not StatusStrip);
            var order = window.Workspaces.ToArray();
            pending = window.PendingAction;
            toolbar.Enabled = false;
            try
            {
                ((ToolStripButton)toolbar.Items["MoveWorkspaceUp"]!).PerformClick();
                ((ToolStripButton)toolbar.Items["MoveWorkspaceDown"]!).PerformClick();
                Require(ReferenceEquals(pending, window.PendingAction) && window.Workspaces.SequenceEqual(order)
                    && window.Current == selected && selected.Active == browser, "Disabled toolbar actions changed workspace state.");
            }
            finally { toolbar.Enabled = true; }
            Require(File.ReadAllText(path) == originalFile, "Moving in-memory rows wrote session state before save.");
            Pass("workspace-order-disabled-actions-and-display-only-mutation", passed);

            await window.ClickAction("SaveSession");
            var saved = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))!;
            saved.Validate();
            Console.WriteLine("ORDER_SAVE_TRACE " + JsonSerializer.Serialize(new { saved, seedLayouts = snapshot.Workspaces.Select(item => item.Layout), initialLayouts,
                selectedStillBrowser = selected.Active == browser, currentStillSelected = window.Current == selected }));
            Require(saved.Version == 1 && saved.WorkspaceSequence == 3 && saved.SelectedWorkspace == 2
                && saved.Workspaces.Select(item => item.Layout).SequenceEqual(new[] { snapshot.Workspaces[0].Layout, snapshot.Workspaces[2].Layout, snapshot.Workspaces[1].Layout }),
                "Explicit save lost workspace order, current selection or nested data.");
            Require(!File.ReadAllText(path).Contains("SYNTHETIC_BROWSER_STATE", StringComparison.Ordinal)
                && !File.ReadAllText(path).Contains("ORDER_TERMINAL_LIVE", StringComparison.Ordinal), "Reordering expanded storage to output or browser memory.");
            Pass("workspace-order-explicit-save-existing-schema-and-selection", passed);

            await window.ClickAction("MoveWorkspaceUp");
            await window.ClickAction("MoveWorkspaceUp");
            Require(window.Workspaces.SequenceEqual(new[] { initial[1], initial[0], initial[2] }), "Final unsaved ordering was wrong.");
            if (artifacts is not null)
            {
                Directory.CreateDirectory(artifacts);
                var sidebar = window.Controls.OfType<ListBox>().Single();
                using var image = new Bitmap(sidebar.Width, sidebar.Height);
                sidebar.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(Path.Combine(artifacts, "workspace-order-sidebar.png"));
            }
            var closed = Closed(window);
            window.Close();
            await closed.WaitAsync(TimeSpan.FromSeconds(30));
            Require(processes.Values.All(process => process.WaitForExit(0)), "An original ordered shell survived normal close.");
            var normal = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))!;
            Require(normal.SelectedWorkspace == 0 && normal.Workspaces.Select(item => item.Layout).SequenceEqual(new[] { snapshot.Workspaces[1].Layout, snapshot.Workspaces[0].Layout, snapshot.Workspaces[2].Layout }),
                "Normal close did not save the latest order/selection.");
            Pass("workspace-order-normal-close-saves-latest-order", passed);

            restored.Show();
            await restored.Started.WaitAsync(TimeSpan.FromSeconds(30));
            Track(restored);
            var reopened = restored.Workspaces.ToArray();
            Require(reopened.Length == 3 && restored.Current == reopened[0] && reopened[0].Active is BrowserPane
                && reopened.Select(workspace => workspace.Panes.OfType<TerminalPane>().Single().WorkingDirectory).SequenceEqual(new[] { directories[1], directories[0], directories[2] }),
                "Reopening lost ordered duplicate identities, directories or active browser.");
            var reopenedBrowser = (BrowserPane)reopened[0].Active!;
            await Program.Until(async () => await reopenedBrowser.Core.ExecuteScriptAsync("document.querySelector('#page')?.textContent") == "\"/workspace-order\"", "The ordered browser did not reopen its URL.");
            Pass("workspace-order-reopens-order-cwd-and-browser-selection", passed);

            await restored.ClickAction("NewWorkspace");
            Track(restored);
            Require(restored.Current!.Title == new Strings().Format("Workspace", 4), "Reordering changed the workspace naming sequence.");
            await restored.ClickAction("MoveWorkspaceUp");
            await restored.ClickAction("CloseWorkspace");
            Require(restored.Workspaces.SequenceEqual(reopened), "Closing a moved new workspace disturbed its neighbors.");
            while (restored.Current is not null) await restored.ClickAction("CloseWorkspace");
            Require(!restored.IsActionEnabled("MoveWorkspaceUp") && !restored.IsActionEnabled("MoveWorkspaceDown"), "Empty workspace ordering actions remained enabled.");
            await restored.ClickAction("NewWorkspace");
            Track(restored);
            Require(!restored.IsActionEnabled("MoveWorkspaceUp") && !restored.IsActionEnabled("MoveWorkspaceDown"), "A single workspace could move outside its bounds.");
            Pass("workspace-order-create-close-empty-and-single-workspace", passed);
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            await restored.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var process in processes.Values) { Require(process.WaitForExit(0), "An original workspace-order process survived cleanup."); process.Dispose(); }
            foreach (var profile in profiles) Require(!Directory.Exists(profile), "An order fixture profile survived cleanup.");
            DeleteFixture(root);
        }
    }

    private static string[] NativeNames(DesktopWindow window)
    {
        var list = window.Controls.OfType<ListBox>().Single();
        GetWindowThreadProcessId(list.Handle, out var owner);
        Require(owner == Environment.ProcessId, "Only the owned sidebar may be read.");
        return Enumerable.Range(0, list.Items.Count).Select(index =>
        {
            var length = (int)SendMessage(list.Handle, 0x018A, index, IntPtr.Zero);
            Require(length is >= 0 and <= 512, "The owned native row returned an invalid length.");
            var text = new StringBuilder(length + 1);
            Require((int)GetText(list.Handle, 0x0189, index, text) == length, "The owned native row returned incomplete text.");
            return text.ToString();
        }).ToArray();
    }

    private static Task Closed(Form form)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        form.FormClosed += (_, _) => done.TrySetResult();
        return done.Task;
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] private static extern IntPtr GetText(IntPtr window, uint message, int index, StringBuilder text);

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

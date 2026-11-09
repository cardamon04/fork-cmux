using System.Diagnostics;
using System.Text.Json;
using Cmux.Desktop;

internal static class SplitDepthScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed)
    {
        await Live(source, "pwsh", passed);
        await Live(source with { Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") }, "windows-powershell", passed);
    }

    private static async Task Live(ShellLaunch source, string shell, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-split-depth-").FullName;
        var path = Path.Combine(root, "layout.json");
        var launch = source with { Directory = root, Arguments = ShellLaunch.PowerShellArguments(source.Arguments[^1], noProfile: true) };
        LayoutSnapshot Branch(int depth) => depth == 0 ? new("terminal", Directory: root)
            : new("split", Orientation: "vertical", Ratio: 0, First: new("browser", Url: "about:blank"), Second: Branch(depth - 1));
        var seed = new SessionSnapshot(1, 1, 0, [new("Depth boundary", SessionSnapshot.MaxLayoutDepth - 1, Branch(SessionSnapshot.MaxLayoutDepth - 1))]);
        seed.Validate();
        using (var store = new SessionStore(path)) { _ = store.Open(); store.Save(seed); }
        var originalFile = File.ReadAllBytes(path);
        using var window = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        using var reopened = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
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
        void Pass(string name) { var result = "split-depth-" + shell + "-" + name; passed.Add(result); Console.WriteLine("passed: " + result); }
        try
        {
            window.Show(); await window.Started.WaitAsync(TimeSpan.FromSeconds(90)); Track(window);
            var workspace = window.Current!;
            var original = (TerminalPane)workspace.Active!;
            var shallow = workspace.Panes.OfType<BrowserPane>().First();
            await Program.Contains(original, "CMUX_TEST_READY>");
            await Program.Command(original, "$global:cmuxDepthMemory = 'DEPTH_' + 'LIVE'; Write-Output ('DEPTH_' + 'READY')");
            await Program.Contains(original, "DEPTH_READY");
            Require(Depth(window.CaptureSession().Workspaces[0].Layout) == SessionSnapshot.MaxLayoutDepth - 1 && window.IsActionEnabled("SplitRight"), "The private depth15 fixture is not ready for its last valid split.");
            await window.ClickAction("SplitRight"); Track(window);
            var deepest = (TerminalPane)workspace.Active!;
            await Program.Contains(deepest, "CMUX_TEST_READY>");
            var valid = window.CaptureSession(); valid.Validate();
            Require(Depth(valid.Workspaces[0].Layout) == SessionSnapshot.MaxLayoutDepth && workspace.Panes.Count == SessionSnapshot.MaxLayoutDepth + 1,
                "A valid depth15 leaf was not allowed to split exactly once.");
            Console.WriteLine("SPLIT_DEPTH_PRECONDITION " + JsonSerializer.Serialize(new { shell, depth = Depth(valid.Workspaces[0].Layout), panes = workspace.Panes.Count, valid = true, terminalReady = true }));
            Pass("last-valid-split");

            async Task Blocked(Func<Task> action, string name)
            {
                var before = JsonSerializer.Serialize(window.CaptureSession());
                var panes = workspace.Panes.ToArray();
                await action(); Track(window);
                var after = window.CaptureSession();
                var validAfter = true;
                try { after.Validate(); } catch (InvalidDataException) { validAfter = false; }
                Console.WriteLine("SPLIT_DEPTH_POST_ACTION " + JsonSerializer.Serialize(new { shell, action = name, depth = Depth(after.Workspaces[0].Layout), panes = workspace.Panes.Count, validAfter }));
                Require(validAfter, "A " + name + " split exceeded the persisted layout depth; the live session can no longer be saved.");
                Require(window.LastError is null && JsonSerializer.Serialize(after) == before && window.Current == workspace
                    && workspace.Active == deepest && workspace.Panes.SequenceEqual(panes) && deepest.ContainsFocus
                    && processes.Values.All(process => !process.WaitForExit(0)), "A refused depth-boundary action changed live resources, layout or selection.");
                Pass(name + "-no-op");
            }
            await Blocked(() => window.Split(Orientation.Vertical), "direct-vertical");
            await Blocked(() => window.Split(Orientation.Horizontal), "direct-horizontal");
            await Blocked(window.OpenBrowser, "direct-browser");
            foreach (var key in new[] { "SplitRight", "SplitBelow", "OpenBrowser" })
            {
                Require(!window.IsActionEnabled(key), "The native " + key + " control offers an unsavable depth17 split.");
                await window.ClickAction(key);
            }
            valid = window.CaptureSession(); valid.Validate();
            Require(workspace.Panes.Count == SessionSnapshot.MaxLayoutDepth + 1 && workspace.Active == deepest
                && File.ReadAllBytes(path).SequenceEqual(originalFile), "Disabled native actions mutated or implicitly saved the depth-boundary session.");
            Pass("native-actions-disabled-without-mutation");

            shallow.FocusPane();
            await shallow.Core.ExecuteScriptAsync("window.cmuxDepthState=41");
            Require(window.IsActionEnabled("SplitRight") && window.IsActionEnabled("SplitBelow") && window.IsActionEnabled("OpenBrowser"), "A deep branch globally disabled splitting a shallow browser.");
            await window.ClickAction("OpenBrowser");
            var addedBrowser = (BrowserPane)workspace.Active!;
            Require(addedBrowser != shallow && await shallow.Core.ExecuteScriptAsync("++window.cmuxDepthState") == "42", "Splitting a shallow browser recreated its live page.");
            deepest.FocusPane();
            Require(!window.IsActionEnabled("OpenBrowser"), "Returning to the deep leaf retained shallow-pane action availability.");
            original.FocusPane(); await Program.Command(original, "Write-Output $global:cmuxDepthMemory"); await Program.Contains(original, "DEPTH_LIVE");
            deepest.FocusPane();
            valid = window.CaptureSession(); valid.Validate();
            Require(Depth(valid.Workspaces[0].Layout) == SessionSnapshot.MaxLayoutDepth && processes.Values.All(process => !process.WaitForExit(0)), "A shallow split lost the deep branch or its live shells.");
            Pass("shallow-browser-and-deep-live-state-isolation");

            await window.ClickAction("ClosePane");
            Require(processes[deepest].WaitForExit(0) && !processes[original].WaitForExit(0), "Closing the deepest leaf ended its live sibling.");
            original.FocusPane();
            Require(window.IsActionEnabled("SplitBelow"), "Collapsing the last split did not restore depth capacity.");
            await window.ClickAction("SplitBelow"); Track(window);
            var horizontal = (TerminalPane)workspace.Active!;
            await Program.Contains(horizontal, "CMUX_TEST_READY>");
            Require(horizontal.Parent?.Parent is SplitContainer { Orientation: Orientation.Horizontal }
                && !window.IsActionEnabled("SplitBelow"), "A depth15 leaf could not grow horizontally back to depth16.");
            valid = window.CaptureSession(); valid.Validate();
            Pass("close-releases-depth-for-horizontal-split");

            var savedPaneCount = workspace.Panes.Count;
            var expected = JsonSerializer.Serialize(valid);
            await window.ClickAction("SaveSession");
            Require(window.LastError is null && JsonSerializer.Serialize(JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))) == expected
                && !File.ReadAllText(path).Contains("cmuxDepthMemory", StringComparison.Ordinal), "A bounded layout did not save through the unchanged version-1 schema.");
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.FormClosed += (_, _) => closed.TrySetResult(); window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(90));
            Require(processes.Values.All(process => process.WaitForExit(0)) && JsonSerializer.Serialize(JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))) == expected,
                "Normal close lost the bounded layout or retained an owned shell.");
            reopened.Show(); await reopened.Started.WaitAsync(TimeSpan.FromSeconds(90)); Track(reopened);
            var restored = reopened.CaptureSession(); restored.Validate();
            Require(restored.Version == 1 && restored.WorkspaceSequence == 1 && restored.Workspaces.Length == 1
                && restored.Workspaces[0].ActivePane == valid.Workspaces[0].ActivePane
                && Depth(restored.Workspaces[0].Layout) == SessionSnapshot.MaxLayoutDepth
                && reopened.Current!.Panes.Count == savedPaneCount && reopened.Current.Active is TerminalPane,
                "Reopen failed to retain the valid boundary layout and selected pane.");
            Pass("existing-schema-save-normal-close-and-reopen");
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(90));
            await reopened.Shutdown().WaitAsync(TimeSpan.FromSeconds(90));
            foreach (var process in processes.Values) { Require(process.WaitForExit(0), "An owned depth fixture shell survived shutdown."); process.Dispose(); }
            foreach (var profile in profiles) Require(!Directory.Exists(profile), "An owned depth profile survived shutdown.");
            var full = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(full)!.Equals(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("cmux-split-depth-", StringComparison.Ordinal), "Refusing to delete an unowned depth fixture.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static int Depth(LayoutSnapshot node) => node.Kind == "split" ? 1 + Math.Max(Depth(node.First!), Depth(node.Second!)) : 0;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

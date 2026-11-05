using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cmux.Desktop;

internal static class TerminalAttentionScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed)
    {
        await RestoreDuringBell(source, passed);
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TerminalAttention.Parser.cjs"));
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Assets"));
        using (var parser = Process.Start(start)!)
        {
            _ = parser.SafeHandle;
            var stdout = parser.StandardOutput.ReadToEndAsync();
            var stderr = parser.StandardError.ReadToEndAsync();
            try
            {
                await parser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(parser.ExitCode == 0, "The real BEL parser contract failed: " + await stderr);
                Console.WriteLine("ATTENTION_PARSER " + await stdout);
            }
            finally { if (!parser.HasExited) { parser.Kill(); await parser.WaitForExitAsync(); } }
        }
        Pass(passed, "terminal-attention-real-parser-framing-and-flood");
        foreach (var locale in new[] { "en", "ja", "de", "fr", "ar", "es", "zh-Hant", "zh-Hans", "ko" })
        {
            var strings = new Strings(CultureInfo.GetCultureInfo(locale));
            Require(!string.IsNullOrWhiteSpace(strings["NextUnread"]) && strings.Format("UnreadWorkspace", "WORKSPACE").Contains("WORKSPACE", StringComparison.Ordinal), "Attention translation missing: " + locale);
            Require(!string.IsNullOrWhiteSpace(strings["Notifications"]) && !string.IsNullOrWhiteSpace(strings["NoUnreadNotifications"])
                && strings.Format("NotificationTerminal", 12).Contains("12", StringComparison.Ordinal)
                && strings.Format("NotificationTarget", "WORKSPACE", "TERMINAL").Contains("WORKSPACE", StringComparison.Ordinal)
                && strings.Format("NotificationTarget", "WORKSPACE", "TERMINAL").Contains("TERMINAL", StringComparison.Ordinal),
                "Notification list translation or placeholders missing: " + locale);
        }
        Pass(passed, "terminal-attention-nine-locales");
        await Integration(source, "pwsh", passed);
        await Integration(source with { Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") }, "windows-powershell", passed);
    }

    private static async Task RestoreDuringBell(ShellLaunch source, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-attention-restore-").FullName;
        var path = Path.Combine(root, "layout.json");
        var gate = Path.Combine(root, "startup-gate");
        var history = Path.Combine(root, "history.txt");
        var titles = Enumerable.Range(0, 3).Select(index => "RESTORE_BELL_" + index).ToArray();
        var snapshot = new SessionSnapshot(SessionSnapshot.CurrentVersion, titles.Length, titles.Length - 1,
            titles.Select(title => new WorkspaceSnapshot(title, 0, new LayoutSnapshot("terminal", Directory: root))).ToArray());
        using (var seed = new SessionStore(path)) { _ = seed.Open(); seed.Save(snapshot); }
        var original = File.ReadAllBytes(path);
        static string Quote(string text) => text.Replace("'", "''", StringComparison.Ordinal);
        var launch = source with { Directory = root, Arguments = ["-NoLogo", "-NoProfile", "-NoExit", "-Command",
            "Set-PSReadLineOption -HistorySaveStyle SaveNothing -HistorySavePath '" + Quote(history) + "'; "
            + "function global:prompt { 'CMUX_TEST_READY> ' }; while (!(Test-Path -LiteralPath '" + Quote(gate)
            + "')) { Start-Sleep -Milliseconds 10 }; [Console]::Write([char]7)"] };
        var store = new SessionStore(path);
        using var window = new DesktopWindow(launch, new MemoryClipboard(), store) { ShowInTaskbar = false };
        var processes = new Dictionary<TerminalPane, Process>();
        var subscriptions = new List<(TerminalPane Pane, Action Handler)>();
        var early = new HashSet<TerminalPane>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrierEntered = false;
        Exception? barrierFailure = null;
        string? profile = null;
        void Track(TerminalPane pane)
        {
            if (processes.ContainsKey(pane)) return;
            var process = Process.GetProcessById(pane.ProcessId);
            _ = process.SafeHandle; // Retain the original live producer handle.
            processes.Add(pane, process);
        }
        try
        {
            // Subscribe synchronously as saved workspace trees are mounted.
            // A test-owned modal loop holds the second projection until a real
            // first-shell BEL is delivered, without a sleep or production seam.
            window.Controls.OfType<Panel>().Single().ControlAdded += (_, args) =>
            {
                if (args.Control is not WorkspaceView workspace) return;
                workspace.ControlAdded += (_, _) =>
                {
                    foreach (var pane in workspace.Panes.OfType<TerminalPane>().ToArray())
                    {
                        if (subscriptions.Any(item => item.Pane == pane)) continue;
                        void Observe()
                        {
                            Track(pane);
                            if (!window.Started.IsCompleted) { early.Add(pane); delivered.TrySetResult(); }
                        }
                        pane.Bell += Observe;
                        subscriptions.Add((pane, Observe));
                    }
                };
                workspace.VisibleChanged += (_, _) =>
                {
                    if (workspace.Title != titles[1] || !workspace.Visible || barrierEntered) return;
                    barrierEntered = true;
                    using var barrier = new Form { ShowInTaskbar = false };
                    barrier.Shown += async (_, _) =>
                    {
                        try
                        {
                            File.WriteAllText(gate, "release");
                            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                        }
                        catch (Exception error) { barrierFailure = error; }
                        finally { barrier.Close(); }
                    };
                    barrier.ShowDialog(window);
                };
            };
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = window.ProfilePath;
            var restored = window.Workspaces.ToArray();
            foreach (var pane in restored.SelectMany(workspace => workspace.Panes).OfType<TerminalPane>()) Track(pane);
            var unchanged = original.SequenceEqual(File.ReadAllBytes(path));
            Console.WriteLine("ATTENTION_RESTORE " + JsonSerializer.Serialize(new { startupBells = early.Count,
                barrierEntered, barrierError = barrierFailure?.Message, nativeSidebar = window.Controls.OfType<ListBox>().Single().IsHandleCreated,
                expected = titles.Length, actual = restored.Length, titles = restored.Select(workspace => workspace.Title).ToArray(),
                canSave = store.CanSave, status = window.SessionStatus, unchanged }));
            Require(barrierEntered && barrierFailure is null, "The owned startup barrier did not deliver a real shell BEL during restoration.");
            Require(early.Count > 0, "The startup BEL fixture did not interleave notification delivery with restoration.");
            Require(restored.Select(workspace => workspace.Title).SequenceEqual(titles) && store.CanSave
                && window.SessionStatus == new Strings()["SessionRestored"], "A startup BEL made a valid saved layout fall back and disabled saving.");
            Require(unchanged && window.Current == restored[^1], "Startup BEL changed the original save or selected workspace.");
            Require(early.Where(pane => pane != window.Current!.Active).All(pane => pane.HasUnread), "Restoration discarded unread startup notifications for unviewed panes.");
            Pass(passed, "terminal-attention-startup-bell-preserves-valid-session-restore");
        }
        finally
        {
            foreach (var (pane, handler) in subscriptions) pane.Bell -= handler;
            File.WriteAllText(gate, "release");
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var process in processes.Values) { Require(process.WaitForExit(0), "An original startup BEL producer survived shutdown."); process.Dispose(); }
            if (profile is not null) Require(!Directory.Exists(profile), "The startup BEL profile survived shutdown.");
            var full = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(full).StartsWith("cmux-attention-restore-", StringComparison.Ordinal), "Unexpected startup BEL cleanup path.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static async Task Integration(ShellLaunch source, string shell, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-attention-").FullName;
        using var window = new DesktopWindow(source with { Directory = root }, new MemoryClipboard()) { ShowInTaskbar = false };
        var processes = new List<Process>();
        string? profile = null, browserProfile = null;
        void Track(TerminalPane pane) { var process = Process.GetProcessById(pane.ProcessId); _ = process.SafeHandle; processes.Add(process); }
        ToolStripDropDownButton OpenNotifications()
        {
            var strip = window.Controls.OfType<ToolStrip>().Single(item => item is not StatusStrip);
            var button = strip.Items.OfType<ToolStripDropDownButton>().SingleOrDefault(item => item.Name == "Notifications");
            Require(button is not null, "The native unread notifications list is missing.");
            if (button!.IsOnOverflow) strip.OverflowButton.ShowDropDown();
            button.ShowDropDown();
            Require(button.DropDown.Visible, "The native unread notifications list did not open.");
            return button;
        }
        void CloseNotifications(ToolStripDropDownButton button)
        {
            button.HideDropDown();
            window.Controls.OfType<ToolStrip>().Single(item => item is not StatusStrip).OverflowButton.HideDropDown();
        }
        async Task Bell(TerminalPane pane, Action beforeRelease, Func<bool>? focusReady = null, bool focusTextarea = false)
        {
            await Task.Delay(150); // Distinct BELs must outlive the renderer cooldown.
            var token = Guid.NewGuid().ToString("N");
            var gate = Path.Combine(root, "gate-" + token);
            var deliveries = 0;
            void Delivered() => deliveries++;
            pane.Bell += Delivered;
            try
            {
            await Program.Command(pane, "Write-Output ('BEL_WAIT_' + '" + token + "'); while (!(Test-Path -LiteralPath '" + gate.Replace("'", "''", StringComparison.Ordinal) + "')) { Start-Sleep -Milliseconds 20 }; [Console]::Write([char]7); Write-Output ('BEL_SENT_' + '" + token + "')");
            await Program.Contains(pane, "BEL_WAIT_" + token);
            beforeRelease();
            var target = window.Current?.Active;
            try { await Program.Until(() => Task.FromResult(focusReady?.Invoke() ?? target?.ContainsFocus == true), "The owned target did not acquire native focus before releasing BEL."); }
            catch
            {
                var browser = target as BrowserPane;
                var navigation = browser?.Controls.OfType<ToolStrip>().First(strip => strip is not StatusStrip);
                var address = navigation?.Items.OfType<ToolStripTextBox>().Single();
                Console.WriteLine("ATTENTION_TARGET_FAILURE " + JsonSerializer.Serialize(new { shell,
                    targetKind = target?.Kind.ToString(), currentKind = window.Current?.Active?.Kind.ToString(),
                    targetStillActive = window.Current?.Active == target, target?.Visible, target?.Enabled,
                    nativeFocus = target?.ContainsFocus, sourceFocus = pane.ContainsFocus, windowFocus = window.ContainsFocus,
                    browserSource = browser?.Core.Source, navigationVisible = navigation?.Visible, navigationEnabled = navigation?.Enabled,
                    addressVisible = address?.Visible, addressEnabled = address?.Enabled, addressOverflow = address?.IsOnOverflow,
                    addressControlVisible = address?.Control.Visible, addressControlFocused = address?.Control.Focused,
                    width = target?.Width, navigationWidth = navigation?.Width, addressWidth = address?.Width }));
                throw;
            }
            if (focusTextarea && target is TerminalPane terminal)
            {
                // A newly split WebView can own native focus before its DOM
                // textarea has ever been focused. Establish that precondition
                // before BEL, rather than attributing its absence to arrival.
                await terminal.Core.ExecuteScriptAsync("window.cmux.focus()");
                await Program.Until(() => TextareaFocused(terminal), "The target textarea was not focused before releasing BEL.");
            }
            var selectedBefore = window.Current;
            var activeBefore = selectedBefore?.Active;
            var nativeBefore = target?.ContainsFocus == true;
            bool? domBefore = target is TerminalPane beforeTerminal ? await TextareaFocused(beforeTerminal) : null;
            File.WriteAllText(gate, "release");
            await Program.Contains(pane, "BEL_SENT_" + token);
            await Program.Until(() => Task.FromResult(deliveries == 1), "The shell BEL did not reach its native event exactly once.");
            bool? domAfter = target is TerminalPane afterTerminal ? await TextareaFocused(afterTerminal) : null;
            var stable = window.Current == selectedBefore && selectedBefore?.Active == activeBefore
                && (target?.ContainsFocus == true) == nativeBefore && domAfter == domBefore
                && (focusReady?.Invoke() ?? true);
            var evidence = JsonSerializer.Serialize(new { shell, focusTextarea, nativeBefore, nativeAfter = target?.ContainsFocus == true,
                domBefore, domAfter, selectionStable = window.Current == selectedBefore && selectedBefore?.Active == activeBefore, stable, unread = pane.HasUnread });
            Console.WriteLine("ATTENTION_DELIVERY " + evidence);
            Require(stable, "BEL changed the established logical/native/DOM focus: " + evidence);
            }
            finally { pane.Bell -= Delivered; }
        }
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = window.ProfilePath;
            var workspace = window.Current!;
            var first = (TerminalPane)workspace.Active!;
            Track(first);
            await Program.Contains(first, "CMUX_TEST_READY>");
            await window.Split(Orientation.Vertical);
            var second = (TerminalPane)workspace.Active!;
            Track(second);
            await Program.Contains(second, "CMUX_TEST_READY>");
            var sidebar = window.Controls.OfType<ListBox>().Single();
            string Row(WorkspaceView item) => WorkspaceOrderScenario.NativeNames(window)[sidebar.Items.IndexOf(item)];
            var plain = Row(workspace);
            Require(!window.IsActionEnabled("NextUnread"), "Next unread must be disabled without unread panes.");
            var empty = OpenNotifications();
            Require(empty.DropDownItems.Count == 1 && !empty.DropDownItems[0].Enabled,
                "The empty notification list must show one disabled placeholder.");
            Require(window.Current == workspace && workspace.Active == second && !first.HasUnread && !second.HasUnread,
                "Opening the empty notification list changed selection or unread state.");
            CloseNotifications(empty);
            second.FocusTerminal();
            Pass(passed, "terminal-notification-list-" + shell + "-empty-native-menu");

            await Bell(first, () => second.FocusTerminal());
            await Program.Until(() => Task.FromResult(first.HasUnread), "A real background ConPTY BEL did not mark its pane unread.");
            Require(window.Current == workspace && workspace.Active == second && second.ContainsFocus, "Background BEL stole logical/native focus.");
            Require(first.BackColor == Color.Gold && Row(workspace) != plain && workspace.Title == plain && window.IsActionEnabled("NextUnread"), "The unread pane border/native row/action is missing, or its saved title changed.");
            Pass(passed, "terminal-attention-" + shell + "-inactive-pane-without-focus-theft");

            await window.ClickAction("NextUnread");
            await Program.Until(() => Task.FromResult(!first.HasUnread && workspace.Active == first && first.ContainsFocus), "Next unread did not focus and acknowledge the actual terminal.");
            Require(Row(workspace) == plain && first.BackColor == Color.CornflowerBlue && !window.IsActionEnabled("NextUnread"), "Acknowledgment left a stale native unread marker.");
            await Bell(first, () => second.FocusTerminal(), focusTextarea: true);
            Require(first.HasUnread && second.ContainsFocus && await TextareaFocused(second), "BEL changed an explicitly established textarea focus.");
            await window.ClickAction("NextUnread");
            Require(!first.HasUnread && workspace.Active == first, "The repeated unread target did not acknowledge.");
            await Bell(first, () => { });
            Require(!first.HasUnread && !window.IsActionEnabled("NextUnread"), "An actively focused terminal created unread attention.");
            Pass(passed, "terminal-attention-" + shell + "-navigation-acknowledgment-and-active-bell");

            using (var cover = new Form { ShowInTaskbar = false })
            {
                var input = new TextBox { Dock = DockStyle.Fill };
                cover.Controls.Add(input);
                await Bell(first, () => { cover.Show(window); input.Focus(); }, () => input.ContainsFocus);
                await Program.Until(() => Task.FromResult(first.HasUnread), "The selected pane in an inactive window did not become unread.");
                Require(input.ContainsFocus && window.Current == workspace && workspace.Active == first, "BEL activated the inactive owning window.");
                cover.Close();
                first.FocusTerminal();
                await Program.Until(() => Task.FromResult(!first.HasUnread), "Reactivating and focusing the terminal did not acknowledge unread.");
            }
            Pass(passed, "terminal-attention-" + shell + "-inactive-window-focus-acknowledgment");

            await window.NewWorkspace();
            var otherWorkspace = window.Current!;
            var other = (TerminalPane)otherWorkspace.Active!;
            Track(other);
            await Program.Contains(other, "CMUX_TEST_READY>");
            window.SelectWorkspace(0);
            await Bell(first, () => window.SelectWorkspace(1));
            await Program.Until(() => Task.FromResult(first.HasUnread), "A hidden workspace BEL did not set unread.");
            Require(window.Current == otherWorkspace && otherWorkspace.Active == other && other.ContainsFocus, "A hidden workspace BEL selected or focused its owner.");
            await window.ClickAction("NextUnread");
            Require(window.Current == workspace && workspace.Active == first && !first.HasUnread, "Next unread did not navigate across workspaces.");
            Require(!JsonSerializer.Serialize(window.CaptureSession()).Contains("Unread", StringComparison.Ordinal), "Unread attention expanded the saved session schema.");
            Pass(passed, "terminal-attention-" + shell + "-workspace-routing-and-ephemeral-state");

            var notificationWorkspaceTitle = workspace.Title;
            Require(workspace.TryRename("通知 & list"), "Could not name the private notification fixture.");
            await Bell(first, () => window.SelectWorkspace(1));
            var notifications = OpenNotifications();
            var drawn = notifications.DropDownItems.OfType<ToolStripMenuItem>().Single();
            Require(drawn.Enabled && (drawn.Text ?? "").Contains(workspace.Title.Replace("&", "&&", StringComparison.Ordinal), StringComparison.Ordinal),
                "The notification row does not identify its workspace.");
            var drawnName = drawn.AccessibilityObject.Name ?? "";
            Require(drawnName.Contains(workspace.Title, StringComparison.Ordinal) && !drawnName.Contains("&&", StringComparison.Ordinal),
                "The native notification row's accessible label lost literal workspace text.");
            Pass(passed, "terminal-notification-list-" + shell + "-native-localized-accessibility");
            await Bell(second, () => { }, () => notifications.DropDown.Visible);
            Require(first.HasUnread && second.HasUnread && notifications.DropDownItems.Count == 1
                && ReferenceEquals(notifications.DropDownItems[0], drawn), "A new BEL changed the row the user already saw.");
            drawn.PerformClick();
            await window.PendingAction;
            await Program.Until(() => Task.FromResult(window.Current == workspace && workspace.Active == first
                && first.ContainsFocus && !first.HasUnread), "The drawn notification row did not select and acknowledge its original pane.");
            Require(second.HasUnread, "Selecting one notification acknowledged another pane.");
            Pass(passed, "terminal-notification-list-" + shell + "-drawn-row-stable-during-arrival");
            notifications = OpenNotifications();
            var remaining = notifications.DropDownItems.OfType<ToolStripMenuItem>().Single();
            Require(remaining.AccessibilityObject.Name != drawnName, "Different panes have indistinguishable notification labels.");
            remaining.PerformClick();
            await window.PendingAction;
            await Program.Until(() => Task.FromResult(workspace.Active == second && second.ContainsFocus && !second.HasUnread),
                "Reopening the list did not show and acknowledge the remaining unread pane.");
            first.FocusTerminal();
            Require(workspace.TryRename(notificationWorkspaceTitle), "Could not restore the private notification fixture title.");
            Require(workspace.Active == first && !window.IsActionEnabled("NextUnread"), "Notification navigation left a stale unread marker.");
            Pass(passed, "terminal-notification-list-" + shell + "-reopen-and-acknowledge");

            await window.ClickAction("MovePaneToNewWorkspace");
            var destination = window.Current!;
            Require(destination != workspace && destination.Active == first, "The live pane did not move.");
            await Bell(first, () => window.SelectWorkspace(1));
            await Program.Until(() => Task.FromResult(first.HasUnread), "A moved terminal lost its BEL subscription.");
            Require(Row(workspace) == plain && Row(destination) != destination.Title && window.Current == otherWorkspace, "BEL metadata used a captured previous workspace owner.");
            await window.ClickAction("NextUnread");
            Require(window.Current == destination && destination.Active == first && !first.HasUnread, "Next unread did not resolve the moved live owner.");
            Pass(passed, "terminal-attention-" + shell + "-live-move-owner");

            await window.OpenBrowser();
            var browser = (BrowserPane)destination.Active!;
            browserProfile = window.BrowserProfilePath;
            var address = browser.Controls.OfType<ToolStrip>().First(strip => strip is not StatusStrip).Items.OfType<ToolStripTextBox>().Single();
            Require(browser.Core.Source == "about:blank" && address.IsOnOverflow && !address.Control.Visible,
                "The split browser focus regression must exercise an overflowed address control on about:blank.");
            first.FocusTerminal();
            await Bell(first, () => browser.FocusPane());
            await Program.Until(() => Task.FromResult(first.HasUnread), "A BEL behind a browser did not set unread.");
            Require(destination.Active == browser && browser.ContainsFocus, "BEL stole focus from the browser pane.");
            Console.WriteLine("ATTENTION_BROWSER_FOCUS " + JsonSerializer.Serialize(new { shell, addressOverflow = address.IsOnOverflow,
                addressVisible = address.Control.Visible, browserSource = browser.Core.Source, nativeFocus = browser.ContainsFocus }));
            await window.ClickAction("NextUnread");
            Require(destination.Active == first && !first.HasUnread, "Unread navigation from a browser selected the wrong pane.");
            // Keep a different pane unread while closing its workspace, so row
            // removal must also remove the navigation target without a stale event.
            await Bell(first, () => { browser.FocusPane(); window.SelectWorkspace(1); });
            await Program.Until(() => Task.FromResult(first.HasUnread), "The close fixture did not establish unread attention.");
            var closingNotifications = OpenNotifications();
            var stale = closingNotifications.DropDownItems.OfType<ToolStripMenuItem>().Single();
            window.SelectWorkspace(2);
            browser.FocusPane();
            await window.CloseWorkspace();
            Require(!window.Workspaces.Contains(destination) && !window.IsActionEnabled("NextUnread"), "Closing an unread workspace left a navigation target.");
            var selectedAfterClose = window.Current;
            var activeAfterClose = selectedAfterClose?.Active;
            stale.PerformClick();
            await window.PendingAction;
            Require(window.Current == selectedAfterClose && selectedAfterClose?.Active == activeAfterClose,
                "A stale notification row changed the surviving selection.");
            Pass(passed, "terminal-notification-list-" + shell + "-closed-target-no-op");
            Pass(passed, "terminal-attention-" + shell + "-browser-isolation-and-close-cleanup");
            Require(window.LastError is null, "Attention actions reported a native error: " + window.LastError);
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var process in processes) { Require(process.WaitForExit(0), "An attention fixture shell survived shutdown."); process.Dispose(); }
            if (profile is not null) Require(!Directory.Exists(profile), "Attention fixture terminal profile survived shutdown.");
            if (browserProfile is not null) Require(!Directory.Exists(browserProfile), "Attention fixture browser profile survived shutdown.");
            var full = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(full).StartsWith("cmux-attention-", StringComparison.Ordinal), "Unexpected attention cleanup path.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<bool> TextareaFocused(TerminalPane pane) => await pane.Core.ExecuteScriptAsync("document.activeElement === document.querySelector('.xterm-helper-textarea')") == "true";
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(List<string> passed, string name) { passed.Add(name); Console.WriteLine("PASS " + name); }
}

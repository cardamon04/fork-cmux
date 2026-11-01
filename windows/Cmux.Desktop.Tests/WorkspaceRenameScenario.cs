using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using Cmux.Desktop;

internal static class WorkspaceRenameScenario
{
    internal static async Task Run(ShellLaunch launch, List<string> passed, string? artifacts = null)
    {
        void Pass(string name) { passed.Add(name); Console.WriteLine("passed: " + name); }
        PolicyAndLocales();
        Pass("workspace-rename-bounds-unicode-and-nine-locale-controls");
        var directory = Directory.CreateTempSubdirectory("cmux-rename-ui-").FullName;
        var path = Path.Combine(directory, "state", "session.json");
        var shell = launch with { Directory = directory };
        using var first = new DesktopWindow(shell, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        using var restored = new DesktopWindow(shell, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        var processes = new List<Process>();
        var trackedPanes = new HashSet<TerminalPane>();
        var profiles = new HashSet<string>();
        void Track(DesktopWindow window)
        {
            profiles.Add(window.ProfilePath);
            if (window.BrowserProfilePath is { } browser) profiles.Add(browser);
            foreach (var pane in window.Workspaces.SelectMany(workspace => workspace.Panes).OfType<TerminalPane>())
            {
                if (!trackedPanes.Add(pane)) continue;
                var process = Process.GetProcessById(pane.ProcessId);
                _ = process.SafeHandle;
                processes.Add(process);
            }
        }
        try
        {
            first.Show();
            await first.Started.WaitAsync(TimeSpan.FromSeconds(30));
            Track(first);
            await Regression(first, passed);
            await CaseOnly(first, passed);
            var workspace = first.Current!;
            var pane = workspace.Active;
            var original = workspace.Title;
            await Edit(first, () => { _ = first.ClickAction("RenameWorkspace"); }, dialog =>
            {
                Require(Input(dialog).Text == original, "Rename did not prefill the exact current name.");
                Input(dialog).Text = "CANCELLED_TITLE_SHOULD_NOT_PERSIST";
                Require(SessionLayout.Capture(workspace).Title == original, "Unconfirmed text reached the workspace model.");
                ExpectNoEditor(first, () => DoubleClick(first), dialog);
                Button(dialog, "Cancel").PerformClick();
            });
            Require(workspace.Title == original && NativeTitle(first) == original && workspace.Active == pane, "Cancel changed committed workspace state.");
            await Edit(first, () => { _ = first.ClickAction("RenameWorkspace"); }, dialog =>
            {
                foreach (var invalid in new[] { "", "   ", new string('x', 513), "bad\nname", "bad\uD800" })
                {
                    Input(dialog).Text = invalid;
                    Require(!Button(dialog, "Confirm").Enabled && dialog.Controls.Find("Validation", true).Single().Text.Length > 0, "Invalid rename remained applicable or had no feedback.");
                    Require(workspace.Title == original, "Invalid input mutated the committed title.");
                }
                dialog.Close();
            });
            Require(workspace.Title == original && NativeTitle(first) == original, "Closing an invalid editor changed its title.");
            Pass("workspace-rename-cancel-and-invalid-input-preserve-state");
            ExpectNoEditor(first, () => DoubleClick(first, MouseButtons.Right));
            ExpectNoEditor(first, () => DoubleClick(first, blank: true));
            Pass("workspace-rename-ignores-busy-right-and-empty-row-events");

            var boundary = new string('x', 510) + "😀";
            await Rename(first, boundary);
            Require(workspace.Title == boundary && NativeTitle(first) == boundary, "The 512-unit Unicode boundary was truncated or rejected.");
            const string literal = "  project 日本語😀 ; $() & <literal>  ";
            await Rename(first, literal);
            Require(workspace.Title == literal && NativeTitle(first) == literal, "Rename normalized or interpreted literal name data.");
            Pass("workspace-rename-toolbar-preserves-literals-and-utf16-boundary");

            await first.ClickAction("NewWorkspace");
            Track(first);
            var other = first.Current!;
            await Rename(first, literal);
            Require(workspace != other && first.Workspaces.Count() == 2 && NativeTitle(first, 0) == literal && NativeTitle(first, 1) == literal, "Duplicate display names merged identities.");
            await first.ClickAction("OpenBrowser");
            Track(first);
            var browserPane = other.Active;
            Require(first.IsActionEnabled("RenameWorkspace") && browserPane is BrowserPane, "Browser workspaces cannot be renamed.");
            first.SelectWorkspace(0);
            await Edit(first, () => { _ = first.ClickAction("RenameWorkspace"); }, dialog =>
            {
                first.SelectWorkspace(1);
                Button(dialog, "Cancel").PerformClick();
            });
            Console.WriteLine("RENAME_CANCEL_SELECTION " + JsonSerializer.Serialize(new { Selected = first.Current == other, Browser = other.Active == browserPane, Focus = browserPane!.ContainsFocus }));
            await Program.Until(() => Task.FromResult(first.Current == other && other.Active == browserPane && browserPane!.ContainsFocus), "Cancel did not return logical focus to the newly selected browser pane.");
            first.SelectWorkspace(0);
            const string selectedElsewhere = "  pinned 日本語😀 ; $() & <literal>  ";
            await Edit(first, () => { _ = first.ClickAction("RenameWorkspace"); }, dialog =>
            {
                Input(dialog).Text = selectedElsewhere;
                first.SelectWorkspace(1);
                Console.WriteLine("RENAME_BEFORE_CONFIRM " + JsonSerializer.Serialize(new { Selected = first.Current == other, Browser = other.Active == browserPane }));
                Button(dialog, "Confirm").PerformClick();
            });
            Console.WriteLine("RENAME_AFTER_CONFIRM " + JsonSerializer.Serialize(new { Target = workspace.Title == selectedElsewhere, Other = other.Title == literal, Selected = first.Current == other, Browser = other.Active == browserPane }));
            Require(workspace.Title == selectedElsewhere && other.Title == literal && first.Current == other && other.Active == browserPane,
                "A changed selection redirected rename or lost the browser pane.");
            Require(NativeTitle(first, 0) == selectedElsewhere && NativeTitle(first, 1) == literal, "Sidebar refresh lost a selected identity or title.");
            Pass("workspace-rename-duplicates-browser-and-captured-target-isolation");

            await first.ClickAction("SaveSession");
            var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))!;
            Require(snapshot.Version == 1 && snapshot.WorkspaceSequence == 2 && snapshot.SelectedWorkspace == 1
                && snapshot.Workspaces[0].Title == selectedElsewhere && snapshot.Workspaces[1].Title == literal, "Save changed the existing title schema/order/selection.");
            Require(!File.ReadAllText(path).Contains("AFTER_RENAME_ALIVE", StringComparison.Ordinal), "Renaming expanded terminal output persistence.");
            Pass("workspace-rename-explicit-save-uses-existing-title-field");

            const string last = "ブラウザ overview & final";
            await Rename(first, last);
            await Close(first);
            restored.Show();
            await restored.Started.WaitAsync(TimeSpan.FromSeconds(30));
            Track(restored);
            var workspaces = restored.Workspaces.ToArray();
            Require(workspaces.Length == 2 && workspaces[0].Title == selectedElsewhere && workspaces[1].Title == last
                && restored.Current == workspaces[1] && restored.Current.Active is BrowserPane,
                "Normal close/reopen lost renamed titles or restored selection.");
            Require(NativeTitle(restored, 0) == selectedElsewhere && NativeTitle(restored, 1) == last, "Restored native labels differ from saved names.");
            if (artifacts is not null)
            {
                await Edit(restored, () => { _ = restored.ClickAction("RenameWorkspace"); }, dialog =>
                {
                    foreach (var button in new[] { Button(dialog, "Confirm"), Button(dialog, "Cancel") })
                        Require(dialog.ClientRectangle.Contains(dialog.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))), "A rename button is outside the dialog client area.");
                    Capture(dialog, artifacts, "workspace-rename-dialog");
                    Button(dialog, "Cancel").PerformClick();
                });
                Capture(restored.Controls.OfType<ListBox>().Single(), artifacts, "workspace-renamed-sidebar");
            }
            Pass("workspace-rename-normal-close-restores-titles-and-browser-selection");
            await restored.ClickAction("CloseWorkspace");
            await restored.ClickAction("CloseWorkspace");
            Require(restored.Current is null && !restored.IsActionEnabled("RenameWorkspace"), "Rename remained enabled with no workspace.");
            Pass("workspace-rename-disabled-without-workspaces");
        }
        finally
        {
            await first.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            await restored.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            first.Close(); restored.Close();
            try
            {
                foreach (var process in processes) Require(process.WaitForExit(0), "An original rename fixture shell survived shutdown.");
                Require(profiles.All(profile => !Directory.Exists(profile)), "An owned rename profile survived shutdown.");
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
                Require(string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(directory).StartsWith("cmux-rename-ui-", StringComparison.Ordinal), "Refusing cleanup outside the owned fixture directory.");
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }

    private static void PolicyAndLocales()
    {
        using var workspace = new WorkspaceView("original");
        foreach (var invalid in new string?[] { null, "", "  ", "a\tb", new string('x', 513), "high\uD800", "low\uDC00" })
            Require(!workspace.TryRename(invalid) && workspace.Title == "original", "The model accepted invalid rename input.");
        foreach (var valid in new[] { " leading and trailing ", "日本語😀", new string('x', 510) + "😀" })
            Require(workspace.TryRename(valid) && workspace.Title == valid && workspace.ToString() == valid, "The model changed valid name data.");
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var locale in new[] { "en", "ja", "de", "fr", "ar", "es", "zh-Hant", "zh-Hans", "ko" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(locale);
                var strings = new Strings();
                using var dialog = new WorkspaceRenameDialog(strings, "valid");
                Require(dialog.Text == strings["RenameWorkspace"] && Input(dialog).AccessibleName == strings["WorkspaceName"]
                    && Button(dialog, "Confirm").Text == strings["Rename"] && Button(dialog, "Cancel").Text == strings["Cancel"], "Rename controls did not use their locale catalog.");
                Require(dialog.RightToLeftLayout == (locale == "ar"), "Rename dialog direction does not match its locale.");
                Input(dialog).Text = "";
                Require(!Button(dialog, "Confirm").Enabled && dialog.Controls.Find("Validation", true).Single().Text == strings.Format("WorkspaceNameInvalid", 512), "Validation feedback was missing or untranslated.");
            }
        }
        finally { CultureInfo.CurrentUICulture = originalCulture; }
    }

    private static Task Rename(DesktopWindow window, string title) => Edit(window, () => { _ = window.ClickAction("RenameWorkspace"); }, dialog =>
    {
        Input(dialog).Text = title;
        Require(Button(dialog, "Confirm").Enabled, "A valid rename cannot be confirmed.");
        Button(dialog, "Confirm").PerformClick();
    });
    private static TextBox Input(Form dialog) => (TextBox)dialog.Controls.Find("WorkspaceName", true).Single();
    private static Button Button(Form dialog, string name) => (Button)dialog.Controls.Find(name, true).Single();
    private static async Task Close(DesktopWindow window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.FormClosed += (_, _) => closed.TrySetResult();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    private static void Capture(Control control, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        using var image = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(image, new Rectangle(Point.Empty, control.Size));
        image.Save(Path.Combine(directory, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
    }
    internal static async Task Regression(DesktopWindow window, List<string> passed)
    {
        var workspace = window.Current!;
        var pane = (TerminalPane)workspace.Active!;
        await Program.Command(pane, "Write-Output ('RENAME_'+'READY')");
        await Program.Contains(pane, "RENAME_READY");
        const string title = "名前 & 日本語 😀";
        await Edit(window, () => DoubleClick(window), dialog =>
        {
            ((TextBox)dialog.Controls.Find("WorkspaceName", true).Single()).Text = title;
            ((Button)dialog.Controls.Find("Confirm", true).Single()).PerformClick();
        });
        Require(workspace.Title == title && NativeTitle(window) == title, "Workspace rename did not update both the model and native sidebar label.");
        Require(window.Current == workspace && workspace.Active == pane, "Renaming changed workspace/pane identity.");
        Require(SessionLayout.Capture(workspace).Title == title, "The existing session Title field did not capture the renamed workspace.");
        await Program.Command(pane, "Write-Output ('AFTER_RENAME_'+'ALIVE')");
        await Program.Contains(pane, "AFTER_RENAME_ALIVE");
        passed.Add("workspace-rename-double-click-updates-label-and-existing-title");
        Console.WriteLine("passed: " + passed[^1]);
    }

    private static async Task CaseOnly(DesktopWindow window, List<string> passed)
    {
        var workspace = window.Current!;
        var pane = workspace.Active!;
        const string before = "Case 日本語😀 & project";
        const string upper = "CASE 日本語😀 & PROJECT";
        const string lower = "case 日本語😀 & project";
        await Rename(window, before);
        Require(NativeTitle(window) == before, "The exact-case rename fixture did not establish its initial native label.");
        await Rename(window, upper);
        Console.WriteLine("WORKSPACE_CASE " + JsonSerializer.Serialize(new { expected = upper, model = workspace.Title, native = NativeTitle(window) }));
        Require(workspace.Title == upper && NativeTitle(window) == upper, "A case-only toolbar rename left a stale native workspace label.");
        await Edit(window, () => DoubleClick(window), dialog =>
        {
            Input(dialog).Text = lower;
            Button(dialog, "Confirm").PerformClick();
        });
        Require(workspace.Title == lower && NativeTitle(window) == lower, "A case-only row rename left a stale native workspace label.");
        Require(window.Current == workspace && workspace.Active == pane && pane.ContainsFocus, "Case-only renaming changed the selected live pane.");
        passed.Add("workspace-rename-case-only-toolbar-and-row-native-label");
        Console.WriteLine("passed: " + passed[^1]);
    }

    internal static async Task Edit(DesktopWindow window, Action trigger, Action<Form> edit)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = new System.Windows.Forms.Timer { Interval = 25 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.Owner == window && form.Modal && form.Visible);
            if (dialog is null) return;
            timer.Stop();
            try { edit(dialog); finished.TrySetResult(); }
            catch (Exception error) { finished.TrySetException(error); }
            finally { if (!dialog.IsDisposed && dialog.Visible) dialog.Close(); }
        };
        timer.Start();
        try
        {
            trigger();
            try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { throw new InvalidOperationException("A delivered workspace-row double-click did not open a rename workflow."); }
            await window.PendingAction.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { timer.Stop(); }
    }

    private static void ExpectNoEditor(DesktopWindow window, Action trigger, Form? existing = null)
    {
        var unexpected = 0;
        var pending = window.PendingAction;
        using var timer = new System.Windows.Forms.Timer { Interval = 25 };
        timer.Tick += (_, _) =>
        {
            foreach (var dialog in Application.OpenForms.Cast<Form>().Where(form => form != existing && form.Owner == window && form.Modal && form.Visible).ToArray())
            {
                unexpected++;
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
            }
        };
        timer.Start();
        try { trigger(); }
        finally { timer.Stop(); }
        Require(unexpected == 0 && ReferenceEquals(pending, window.PendingAction), "An ignored row event opened an editor or replaced the pending action.");
    }

    private static void DoubleClick(DesktopWindow window, MouseButtons button = MouseButtons.Left, bool blank = false)
    {
        var sidebar = window.Controls.OfType<ListBox>().Single();
        var index = sidebar.SelectedIndex;
        Require(index >= 0 && sidebar.SelectedItem == window.Current, "The owned workspace row was not ready.");
        var bounds = sidebar.GetItemRectangle(index);
        var delivered = false;
        MouseEventHandler observed = (_, _) => delivered = true;
        sidebar.MouseDoubleClick += observed;
        try
        {
            // Exercise the existing framework event on the owned control. No
            // global mouse/keyboard input or foreground/IME assumptions.
            typeof(Control).GetMethod("OnMouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(sidebar, [new MouseEventArgs(button, 2, bounds.Left + 4, blank ? sidebar.ClientSize.Height - 2 : bounds.Top + bounds.Height / 2, 0)]);
            Require(delivered, "The framework double-click event was not delivered.");
        }
        finally { sidebar.MouseDoubleClick -= observed; }
    }

    internal static string NativeTitle(DesktopWindow window, int? index = null)
    {
        var sidebar = window.Controls.OfType<ListBox>().Single();
        _ = GetWindowThreadProcessId(sidebar.Handle, out var owner);
        Require(owner == Environment.ProcessId, "Sidebar inspection must target the owned process.");
        var selected = index ?? sidebar.SelectedIndex;
        var length = (int)SendMessage(sidebar.Handle, 0x18A, selected, IntPtr.Zero);
        Require(length >= 0 && length <= 512, "The native workspace label has an invalid length.");
        var text = new StringBuilder(length + 1);
        _ = GetText(sidebar.Handle, 0x189, selected, text);
        return text.ToString();
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr window, uint message, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] private static extern IntPtr GetText(IntPtr window, uint message, int index, StringBuilder text);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

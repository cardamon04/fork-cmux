using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Cmux.Desktop;

internal static class WorkspaceRenameScenario
{
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

    private static void DoubleClick(DesktopWindow window)
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
                .Invoke(sidebar, [new MouseEventArgs(MouseButtons.Left, 2, bounds.Left + 4, bounds.Top + bounds.Height / 2, 0)]);
            Require(delivered, "The framework double-click event was not delivered.");
        }
        finally { sidebar.MouseDoubleClick -= observed; }
    }

    internal static string NativeTitle(DesktopWindow window)
    {
        var sidebar = window.Controls.OfType<ListBox>().Single();
        _ = GetWindowThreadProcessId(sidebar.Handle, out var owner);
        Require(owner == Environment.ProcessId, "Sidebar inspection must target the owned process.");
        var length = (int)SendMessage(sidebar.Handle, 0x18A, sidebar.SelectedIndex, IntPtr.Zero);
        Require(length >= 0 && length <= 512, "The native workspace label has an invalid length.");
        var text = new StringBuilder(length + 1);
        _ = GetText(sidebar.Handle, 0x189, sidebar.SelectedIndex, text);
        return text.ToString();
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr window, uint message, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] private static extern IntPtr GetText(IntPtr window, uint message, int index, StringBuilder text);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

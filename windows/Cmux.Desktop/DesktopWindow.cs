namespace Cmux.Desktop;

internal sealed class DesktopWindow : Form
{
    private readonly Strings strings = new();
    private readonly ShellLaunch launch;
    private readonly IClipboard clipboard;
    private readonly ToolStrip toolbar = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly ListBox sidebar = new() { Dock = DockStyle.Left, Width = 180, IntegralHeight = false };
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly ToolStripStatusLabel status = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TerminalEnvironment? environment;
    private Task? initialize;
    private bool shuttingDown;
    private bool shutdownComplete;
    private int workspaceSequence;
    internal Task Started => started.Task;
    internal WorkspaceView? Current => sidebar.SelectedItem as WorkspaceView;
    internal IEnumerable<WorkspaceView> Workspaces => sidebar.Items.Cast<WorkspaceView>();
    internal string ProfilePath => environment!.Profile;
    internal string? LastError { get; private set; }
    internal Task PendingAction { get; private set; } = Task.CompletedTask;

    internal DesktopWindow(ShellLaunch launch, IClipboard? clipboard = null)
    {
        this.launch = launch;
        this.clipboard = clipboard ?? new SystemClipboard();
        Text = strings["AppTitle"];
        ClientSize = new(1180, 760);
        MinimumSize = new(640, 400);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(24, 26, 31);
        sidebar.BackColor = Color.FromArgb(32, 35, 43);
        sidebar.ForeColor = Color.WhiteSmoke;
        sidebar.BorderStyle = BorderStyle.None;
        sidebar.SelectedIndexChanged += (_, _) => SelectWorkspace();
        AddAction("NewWorkspace", NewWorkspace);
        AddAction("SplitRight", () => Split(Orientation.Vertical));
        AddAction("SplitBelow", () => Split(Orientation.Horizontal));
        AddAction("ClosePane", ClosePane);
        AddAction("CloseWorkspace", CloseWorkspace);
        toolbar.Items.Add(new ToolStripSeparator());
        AddAction("Copy", async () => { if (Current?.Active is { } pane) await pane.Copy(); });
        AddAction("Paste", () => { Current?.Active?.Paste(); return Task.CompletedTask; });
        var footer = new StatusStrip();
        footer.Items.Add(status);
        Controls.Add(content);
        Controls.Add(sidebar);
        Controls.Add(toolbar);
        Controls.Add(footer);
        status.Text = strings["Starting"];
        Shown += (_, _) => initialize = Initialize();
        FormClosing += CloseWindow;
    }

    private void AddAction(string key, Func<Task> action)
    {
        var button = new ToolStripButton(strings[key]) { Name = key, ToolTipText = strings[key], DisplayStyle = ToolStripItemDisplayStyle.Text };
        button.Click += (_, _) => PendingAction = Run(action);
        toolbar.Items.Add(button);
    }

    internal Task ClickAction(string key)
    {
        ((ToolStripButton)toolbar.Items[key]!).PerformClick();
        return PendingAction;
    }

    private async Task Run(Func<Task> action)
    {
        if (shuttingDown || environment is null) return;
        toolbar.Enabled = false;
        try { await action(); status.Text = strings["Ready"]; }
        catch (Exception error) { ShowError(error.Message); }
        finally { if (!shuttingDown) toolbar.Enabled = true; }
    }

    private async Task Initialize()
    {
        toolbar.Enabled = false;
        try
        {
            environment = await TerminalEnvironment.Create(strings);
            await NewWorkspace();
            status.Text = strings["Ready"];
            started.TrySetResult();
        }
        catch (Exception error) { ShowError(error.Message); started.TrySetException(error); }
        finally { if (!shuttingDown) toolbar.Enabled = true; }
    }

    private void ShowError(string message)
    {
        LastError = message;
        status.Text = strings.Format("Error", message);
    }

    internal async Task NewWorkspace()
    {
        var workspace = new WorkspaceView(strings.Format("Workspace", ++workspaceSequence)) { Dock = DockStyle.Fill };
        content.Controls.Add(workspace);
        sidebar.Items.Add(workspace);
        sidebar.SelectedItem = workspace;
        var pane = AddPane(workspace);
        workspace.Controls.Add(pane);
        try { await pane.Start(environment!, launch); pane.FocusTerminal(); }
        catch
        {
            await RemoveWorkspace(workspace);
            throw;
        }
    }

    private TerminalPane AddPane(WorkspaceView workspace)
    {
        var pane = new TerminalPane(strings, clipboard);
        pane.Activated += active =>
        {
            workspace.Active = active;
            foreach (var item in workspace.Panes)
            {
                item.BackColor = item == active ? Color.CornflowerBlue : Color.FromArgb(24, 26, 31);
            }
        };
        pane.Failed += ShowError;
        workspace.Panes.Add(pane);
        workspace.Active = pane;
        return pane;
    }

    private void SelectWorkspace()
    {
        foreach (var workspace in Workspaces) workspace.Visible = workspace == Current;
        Current?.BringToFront();
        Current?.Active?.FocusTerminal();
    }

    internal void SelectWorkspace(int index) => sidebar.SelectedIndex = index;

    internal async Task Split(Orientation orientation)
    {
        if (Current is not { Active: { } previous } workspace) return;
        var parent = previous.Parent!;
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = orientation, SplitterWidth = 5, Panel1MinSize = 0, Panel2MinSize = 0 };
        var pane = AddPane(workspace);
        parent.Controls.Remove(previous);
        parent.Controls.Add(split);
        split.Panel1.Controls.Add(previous);
        split.Panel2.Controls.Add(pane);
        var available = orientation == Orientation.Vertical ? split.Width : split.Height;
        split.SplitterDistance = Math.Clamp(available / 2, 0, Math.Max(0, available - split.SplitterWidth));
        try { await pane.Start(environment!, launch); pane.FocusTerminal(); }
        catch
        {
            split.Panel1.Controls.Remove(previous);
            parent.Controls.Remove(split);
            parent.Controls.Add(previous);
            workspace.Panes.Remove(pane);
            workspace.Active = previous;
            await pane.DisposeAsync();
            split.Dispose();
            throw;
        }
    }

    internal async Task ClosePane()
    {
        if (Current is not { Active: { } pane } workspace) return;
        if (workspace.Panes.Count == 1) { await RemoveWorkspace(workspace); return; }
        var splitPanel = (SplitterPanel)pane.Parent!;
        var split = (SplitContainer)splitPanel.Parent!;
        var sibling = (splitPanel == split.Panel1 ? split.Panel2 : split.Panel1).Controls[0];
        var parent = split.Parent!;
        sibling.Parent!.Controls.Remove(sibling);
        parent.Controls.Remove(split);
        parent.Controls.Add(sibling);
        workspace.Panes.Remove(pane);
        workspace.Active = workspace.Panes[0];
        await pane.DisposeAsync();
        split.Dispose();
        workspace.Active.FocusTerminal();
    }

    internal async Task CloseWorkspace()
    {
        if (Current is { } workspace) await RemoveWorkspace(workspace);
    }

    private async Task RemoveWorkspace(WorkspaceView workspace)
    {
        // Continue teardown of other owned panes even if one reports failure.
        Exception? failure = null;
        foreach (var pane in workspace.Panes)
            try { await pane.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        workspace.Panes.Clear();
        content.Controls.Remove(workspace);
        var index = sidebar.Items.IndexOf(workspace);
        sidebar.Items.Remove(workspace);
        workspace.Dispose();
        if (sidebar.Items.Count > 0) sidebar.SelectedIndex = Math.Min(index, sidebar.Items.Count - 1);
        if (failure is not null) throw failure;
    }

    internal async Task Shutdown()
    {
        if (shutdownComplete) return;
        shuttingDown = true;
        toolbar.Enabled = false;
        if (initialize is not null) await initialize;
        await PendingAction;
        Exception? failure = null;
        foreach (var workspace in Workspaces.ToArray())
            try { await RemoveWorkspace(workspace); } catch (Exception error) { failure ??= error; }
        if (environment is not null)
            try { await environment.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        shutdownComplete = true;
        if (failure is not null) throw failure;
    }

    private async void CloseWindow(object? sender, FormClosingEventArgs e)
    {
        if (shutdownComplete) return;
        e.Cancel = true;
        if (shuttingDown) return;
        try { await Shutdown(); }
        catch (Exception error) { ShowError(error.Message); }
        finally { Close(); }
    }
}

namespace Cmux.Desktop;

internal sealed class DesktopWindow : Form
{
    private readonly Strings strings = new();
    private readonly ShellLaunch launch;
    private readonly IClipboard clipboard;
    private readonly SessionStore? session;
    private readonly ToolStrip toolbar = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly ListBox sidebar = new() { Dock = DockStyle.Left, Width = 180, IntegralHeight = false };
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly ToolStripStatusLabel status = new();
    private readonly ToolStripStatusLabel sessionStatus = new() { AutoToolTip = true };
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebViewProfile? environment;
    private WebViewProfile? browserEnvironment;
    private Task? initialize;
    private bool shuttingDown;
    private bool shutdownComplete;
    private bool selectingWorkspace;
    private bool closingWindow;
    private int workspaceSequence;
    internal Task Started => started.Task;
    internal WorkspaceView? Current => sidebar.SelectedItem as WorkspaceView;
    internal IEnumerable<WorkspaceView> Workspaces => sidebar.Items.Cast<WorkspaceView>();
    internal string ProfilePath => environment!.Profile;
    internal string? BrowserProfilePath => browserEnvironment?.Profile;
    internal string? LastError { get; private set; }
    internal string SessionStatus => sessionStatus.Text ?? "";
    internal Task PendingAction { get; private set; } = Task.CompletedTask;

    internal DesktopWindow(ShellLaunch launch, IClipboard? clipboard = null, SessionStore? session = null)
    {
        this.launch = launch;
        this.clipboard = clipboard ?? new SystemClipboard();
        this.session = session;
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
        AddAction("OpenBrowser", OpenBrowser);
        AddAction("ClosePane", ClosePane);
        AddAction("CloseWorkspace", CloseWorkspace);
        AddAction("SaveSession", SaveSession);
        toolbar.Items.Add(new ToolStripSeparator());
        AddAction("Copy", async () => { if (Current?.Active is { } pane) await pane.Copy(); });
        AddAction("Paste", () => { Current?.Active?.Paste(); return Task.CompletedTask; });
        var footer = new StatusStrip { ShowItemToolTips = true };
        footer.Items.Add(status);
        footer.Items.Add(sessionStatus);
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

    internal bool IsActionEnabled(string key) => toolbar.Enabled && toolbar.Items[key]!.Enabled;

    private void UpdateActions()
    {
        var allowed = Current?.Active?.SupportsClipboard == true;
        toolbar.Items["Copy"]!.Enabled = allowed;
        toolbar.Items["Paste"]!.Enabled = allowed;
        toolbar.Items["SaveSession"]!.Enabled = session?.CanSave == true;
    }

    private async Task Run(Func<Task> action)
    {
        if (shuttingDown || closingWindow || environment is null) return;
        toolbar.Enabled = false;
        try { await action(); status.Text = strings["Ready"]; }
        catch (Exception error) { ShowError(error.Message); }
        finally { if (!shuttingDown && !closingWindow) toolbar.Enabled = true; }
    }

    private async Task Initialize()
    {
        toolbar.Enabled = false;
        try
        {
            SessionSnapshot? saved = null;
            if (session is not null)
                try { saved = await Task.Run(session.Open); }
                catch (Exception) { sessionStatus.Text = strings["SessionUnavailable"]; }
            environment = await WebViewProfile.Create(strings);
            if (saved is not null)
            {
                try { await RestoreSession(saved); }
                catch (Exception)
                {
                    session!.ProtectExisting();
                    sessionStatus.Text = strings["SessionUnavailable"];
                    foreach (var workspace in Workspaces.ToArray()) await RemoveWorkspace(workspace);
                    await NewWorkspace();
                }
            }
            else await NewWorkspace();
            status.Text = strings["Ready"];
            UpdateActions();
            started.TrySetResult();
        }
        catch (Exception error) { ShowError(error.Message); started.TrySetException(error); }
        finally { if (!shuttingDown && !closingWindow) toolbar.Enabled = true; }
    }

    private async Task RestoreSession(SessionSnapshot saved)
    {
        workspaceSequence = saved.WorkspaceSequence;
        var addresses = new Dictionary<BrowserPane, string>();
        var missingDirectory = false;
        foreach (var item in saved.Workspaces)
        {
            var workspace = new WorkspaceView(item.Title) { Dock = DockStyle.Fill, Visible = false };
            content.Controls.Add(workspace);
            sidebar.Items.Add(workspace);
            var tree = SessionLayout.Restore(item.Layout, workspace, node =>
            {
                var paneLaunch = launch;
                if (node.Kind == "terminal")
                {
                    if (SessionStore.IsExistingLocalDirectory(node.Directory!)) paneLaunch = launch with { Directory = node.Directory! };
                    else missingDirectory = true;
                }
                var pane = AddPane(workspace, node.Kind == "terminal" ? PaneKind.Terminal : PaneKind.Browser, paneLaunch);
                if (pane is BrowserPane browser) addresses.Add(browser, node.Url!);
                return pane;
            });
            workspace.Controls.Add(tree);
            workspace.Active = workspace.Panes[item.ActivePane];
        }
        foreach (var workspace in Workspaces)
            foreach (var pane in workspace.Panes)
            {
                await StartPane(pane);
                if (pane is BrowserPane browser) browser.NavigateFromAddress(addresses[browser]);
            }
        if (saved.Workspaces.Length == 0) await NewWorkspace();
        else sidebar.SelectedIndex = saved.SelectedWorkspace;
        sessionStatus.Text = strings[missingDirectory ? "SessionDirectoryFallback" : "SessionRestored"];
    }

    internal SessionSnapshot CaptureSession() => new(SessionSnapshot.CurrentVersion, workspaceSequence,
        sidebar.SelectedIndex, Workspaces.Select(SessionLayout.Capture).ToArray());

    private async Task SaveSession()
    {
        if (session?.CanSave != true) return;
        var snapshot = CaptureSession();
        try { await Task.Run(() => session.Save(snapshot)); sessionStatus.Text = strings["SessionSaved"]; }
        catch (Exception) { sessionStatus.Text = strings["SessionSaveFailed"]; throw new IOException(strings["SessionSaveFailed"]); }
    }

    private void ShowError(string message)
    {
        LastError = message;
        status.Text = strings.Format("Error", message);
    }

    internal Task NewWorkspace() => NewWorkspace(PaneKind.Terminal);

    private async Task NewWorkspace(PaneKind kind)
    {
        var workspace = new WorkspaceView(strings.Format("Workspace", ++workspaceSequence)) { Dock = DockStyle.Fill };
        content.Controls.Add(workspace);
        sidebar.Items.Add(workspace);
        sidebar.SelectedItem = workspace;
        var pane = AddPane(workspace, kind);
        workspace.Controls.Add(pane);
        var selection = SelectPendingPane(workspace, pane);
        try
        {
            await StartPane(pane);
            if (workspace.SelectionRevision == selection && Current == workspace) pane.FocusPane();
        }
        catch
        {
            await RemoveWorkspace(workspace);
            throw;
        }
    }

    private async Task StartPane(WorkspacePane pane)
    {
        var profile = pane.Kind == PaneKind.Browser
            ? browserEnvironment ??= await WebViewProfile.Create(strings, "cmux-browsing-")
            : environment!;
        await pane.Start(profile);
    }

    private WorkspacePane AddPane(WorkspaceView workspace, PaneKind kind, ShellLaunch? paneLaunch = null)
    {
        WorkspacePane pane = kind == PaneKind.Browser
            ? new BrowserPane(strings) : new TerminalPane(strings, clipboard, paneLaunch ?? launch);
        pane.Activated += active =>
        {
            if (!selectingWorkspace && Current == workspace) ActivatePane(workspace, active);
        };
        pane.Failed += ShowError;
        workspace.Panes.Add(pane);
        return pane;
    }

    private long SelectPendingPane(WorkspaceView workspace, WorkspacePane pane)
    {
        // Establish focus after reparenting, before yielding to initialization.
        // Later native selection changes own the final focus decision.
        pane.Focus();
        ActivatePane(workspace, pane);
        return workspace.SelectionRevision;
    }

    private void ActivatePane(WorkspaceView workspace, WorkspacePane pane)
    {
        if (!workspace.Panes.Contains(pane)) return;
        if (workspace.Active != pane) workspace.SelectionRevision++;
        workspace.Active = pane;
        foreach (var item in workspace.Panes)
            item.BackColor = item == pane ? Color.CornflowerBlue : Color.FromArgb(24, 26, 31);
        UpdateActions();
    }

    private void SelectWorkspace()
    {
        if (selectingWorkspace) return;
        var selected = Current;
        var pane = selected?.Active;
        selectingWorkspace = true;
        try
        {
            // Visibility/layout can emit native focus events while projecting
            // the saved selection. They must not replace that model state.
            foreach (var workspace in Workspaces) workspace.Visible = workspace == selected;
            selected?.BringToFront();
            selected?.ApplyRestoredRatios();
            if (selected == Current) pane?.FocusPane();
        }
        finally { selectingWorkspace = false; }
        if (selected != Current) { SelectWorkspace(); return; }
        if (selected is not null && pane is not null) ActivatePane(selected, pane);
        UpdateActions();
    }

    internal void SelectWorkspace(int index) => sidebar.SelectedIndex = index;

    internal Task Split(Orientation orientation) => Split(orientation, PaneKind.Terminal);

    internal Task OpenBrowser() => Current is null ? NewWorkspace(PaneKind.Browser) : Split(Orientation.Vertical, PaneKind.Browser);

    private async Task Split(Orientation orientation, PaneKind kind)
    {
        if (Current is not { Active: { } previous } workspace) return;
        var parent = previous.Parent!;
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = orientation, SplitterWidth = 5, Panel1MinSize = 0, Panel2MinSize = 0 };
        var pane = AddPane(workspace, kind);
        parent.Controls.Remove(previous);
        parent.Controls.Add(split);
        split.Panel1.Controls.Add(previous);
        split.Panel2.Controls.Add(pane);
        var available = orientation == Orientation.Vertical ? split.Width : split.Height;
        split.SplitterDistance = Math.Clamp(available / 2, 0, Math.Max(0, available - split.SplitterWidth));
        var selection = SelectPendingPane(workspace, pane);
        try
        {
            await StartPane(pane);
            if (workspace.SelectionRevision == selection && Current == workspace) pane.FocusPane();
        }
        catch
        {
            split.Panel1.Controls.Remove(previous);
            parent.Controls.Remove(split);
            parent.Controls.Add(previous);
            var selected = workspace.Active;
            workspace.Panes.Remove(pane);
            ActivatePane(workspace, selected is not null && workspace.Panes.Contains(selected) ? selected : previous);
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
        ActivatePane(workspace, workspace.Panes[0]);
        await pane.DisposeAsync();
        split.Dispose();
        workspace.Active.FocusPane();
        UpdateActions();
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
        UpdateActions();
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
        if (browserEnvironment is not null)
            try { await browserEnvironment.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        session?.Dispose();
        shutdownComplete = true;
        if (failure is not null) throw failure;
    }

    private async void CloseWindow(object? sender, FormClosingEventArgs e)
    {
        if (shutdownComplete) return;
        e.Cancel = true;
        if (shuttingDown || closingWindow) return;
        closingWindow = true;
        toolbar.Enabled = false;
        try
        {
            if (initialize is not null) await initialize;
            await PendingAction;
            while (session?.CanSave == true)
            {
                try { await SaveSession(); break; }
                catch (Exception)
                {
                    var choice = MessageBox.Show(this, strings["SessionCloseFailed"], strings["AppTitle"], MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (choice == DialogResult.Cancel) return;
                    if (choice == DialogResult.No) break;
                }
            }
            await Shutdown();
        }
        catch (Exception error)
        {
            ShowError(error.Message);
            if (!shutdownComplete) await Shutdown();
        }
        finally
        {
            closingWindow = false;
            if (shutdownComplete) Close();
            else toolbar.Enabled = true;
        }
    }
}

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
    private readonly ToolStripStatusLabel directoryStatus = new() { Name = "WorkingDirectory", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel gitStatus = new() { Name = "GitBranch", AutoSize = false, Width = 220, TextAlign = ContentAlignment.MiddleLeft };
    private readonly CancellationTokenSource gitClosing = new();
    private readonly SemaphoreSlim gitWake = new(0, 1);
    private CancellationTokenSource? gitQuery;
    private Task? gitWorker;
    private TerminalPane? gitPane;
    private string? gitDirectory;
    private long gitRevision;
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebViewProfile? environment;
    private WebViewProfile? browserEnvironment;
    private Task? initialize;
    private bool shuttingDown;
    private bool shutdownComplete;
    private bool selectingWorkspace;
    private bool editingWorkspaceTitle;
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
        sidebar.MouseDoubleClick += (_, e) =>
        {
            if (toolbar.Enabled && e.Button == MouseButtons.Left && sidebar.SelectedIndex >= 0
                && sidebar.IndexFromPoint(e.Location) == sidebar.SelectedIndex)
                PendingAction = Run(RenameWorkspace);
        };
        AddAction("NewWorkspace", NewWorkspace);
        AddAction("RenameWorkspace", RenameWorkspace);
        AddAction("MoveWorkspaceUp", () => MoveWorkspace(-1));
        AddAction("MoveWorkspaceDown", () => MoveWorkspace(1));
        AddAction("SplitRight", () => Split(Orientation.Vertical));
        AddAction("SplitBelow", () => Split(Orientation.Horizontal));
        AddAction("OpenBrowser", OpenBrowser);
        AddAction("MovePaneToNewWorkspace", MovePaneToNewWorkspace);
        AddAction("ClosePane", ClosePane);
        AddAction("CloseWorkspace", CloseWorkspace);
        AddAction("SaveSession", SaveSession);
        toolbar.Items.Add(new ToolStripSeparator());
        AddAction("Copy", async () => { if (Current?.Active is { } pane) await pane.Copy(); });
        AddAction("Paste", () => { Current?.Active?.Paste(); return Task.CompletedTask; });
        AddAction("Find", () => { (Current?.Active as TerminalPane)?.Find(); return Task.CompletedTask; });
        var footer = new StatusStrip { ShowItemToolTips = true };
        footer.Items.Add(status);
        footer.Items.Add(directoryStatus);
        gitStatus.AccessibleName = strings["GitStatusLabel"];
        footer.Items.Add(gitStatus);
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
        toolbar.Items["Find"]!.Enabled = Current?.Active is TerminalPane;
        toolbar.Items["SaveSession"]!.Enabled = session?.CanSave == true;
        toolbar.Items["RenameWorkspace"]!.Enabled = Current is not null;
        toolbar.Items["MoveWorkspaceUp"]!.Enabled = sidebar.SelectedIndex > 0;
        toolbar.Items["MoveWorkspaceDown"]!.Enabled = sidebar.SelectedIndex >= 0 && sidebar.SelectedIndex < sidebar.Items.Count - 1;
        toolbar.Items["MovePaneToNewWorkspace"]!.Enabled = Current is { Active: not null, Panes.Count: > 1 };
        SetStatusText(directoryStatus, Current?.Active is TerminalPane terminal ? strings.Format("WorkingDirectory", terminal.WorkingDirectory) : "");
        UpdateGitTarget();
        UpdateCaption();
    }

    private void UpdateCaption()
    {
        if (!shuttingDown)
        {
            var caption = TerminalTitle.Caption(strings, (Current?.Active as TerminalPane)?.Title);
            if (Text != caption) Text = caption;
        }
    }

    private static void SetStatusText(ToolStripStatusLabel label, string text)
    {
        // Escape before WinForms measures and draws mnemonic-bearing text.
        // Keep the original value available to tooltips and accessibility.
        label.Text = text.Replace("&", "&&", StringComparison.Ordinal);
        label.ToolTipText = label.AccessibleDescription = text;
    }

    private void UpdateGitTarget()
    {
        if (shuttingDown) return;
        var pane = Current?.Active as TerminalPane;
        var directory = pane?.WorkingDirectory;
        if (pane == gitPane && directory == gitDirectory) return;
        gitPane = pane;
        gitDirectory = directory;
        gitRevision++;
        SetStatusText(gitStatus, "");
        gitQuery?.Cancel();
        if (gitWake.CurrentCount == 0) gitWake.Release();
    }

    private async Task PollGit()
    {
        try
        {
            while (!gitClosing.IsCancellationRequested)
            {
                _ = gitWake.Wait(0);
                var revision = gitRevision;
                if (gitDirectory is { } directory)
                {
                    using var query = CancellationTokenSource.CreateLinkedTokenSource(gitClosing.Token);
                    gitQuery = query;
                    GitStatus? result;
                    try { result = await Task.Run(() => GitStatus.Read(directory, query.Token)); }
                    finally { gitQuery = null; }
                    if (!shuttingDown && revision == gitRevision)
                        SetStatusText(gitStatus, result is null ? ""
                            : strings.Format(result.Detached ? "GitDetached" : "GitBranch", result.Value));
                }
                await gitWake.WaitAsync(TimeSpan.FromSeconds(2), gitClosing.Token);
            }
        }
        catch (OperationCanceledException) when (gitClosing.IsCancellationRequested) { }
    }

    private async Task Run(Func<Task> action)
    {
        if (shuttingDown || closingWindow || environment is null) return;
        var selected = Current;
        var active = selected?.Active;
        var toolbarHadFocus = toolbar.ContainsFocus;
        try
        {
            var previousFence = selectingWorkspace;
            selectingWorkspace = true;
            try
            {
                // Disabling a focused toolbar can restore another native pane.
                // Preserve the logical target while projecting that UI change.
                toolbar.Enabled = false;
                if (toolbarHadFocus && selected == Current && active is { ContainsFocus: false })
                {
                    // Restore only during the synchronous projection. A later
                    // user focus change during the action must remain theirs.
                    try { active.FocusPane(); }
                    catch (Exception error) { ShowError(error.Message); }
                }
            }
            finally { selectingWorkspace = previousFence; }
            await action();
            status.Text = strings["Ready"];
        }
        catch (Exception error) { ShowError(error.Message); }
        finally { if (!shuttingDown && !closingWindow) toolbar.Enabled = true; }
    }

    private Task RenameWorkspace()
    {
        if (Current is not { } workspace) return Task.CompletedTask;
        using var dialog = new WorkspaceRenameDialog(strings, workspace.Title);
        DialogResult result;
        editingWorkspaceTitle = true;
        try { result = dialog.ShowDialog(this); }
        finally { editingWorkspaceTitle = false; }
        // The selected workspace may have changed while the modal loop ran.
        // Restore its logical pane on every exit, including Cancel.
        if (!shuttingDown && !closingWindow) SelectWorkspace();
        if (result != DialogResult.OK || shuttingDown || closingWindow
            || !Workspaces.Contains(workspace)) return Task.CompletedTask;
        if (!workspace.TryRename(dialog.WorkspaceName)) throw new InvalidOperationException(strings.Format("WorkspaceNameInvalid", WorkspaceTitle.MaxCharacters));
        RefreshSidebar(() => sidebar.Items[sidebar.Items.IndexOf(workspace)] = workspace);
        return Task.CompletedTask;
    }

    private Task MoveWorkspace(int offset)
    {
        var index = sidebar.SelectedIndex;
        var target = index + offset;
        if (Current is not { } workspace || target < 0 || target >= sidebar.Items.Count) return Task.CompletedTask;
        RefreshSidebar(() =>
        {
            sidebar.Items.RemoveAt(index);
            sidebar.Items.Insert(target, workspace);
        });
        return Task.CompletedTask;
    }

    private void RefreshSidebar(Action update)
    {
        var selected = Current;
        var active = selected?.Active;
        var wasSelecting = selectingWorkspace;
        selectingWorkspace = true;
        sidebar.BeginUpdate();
        try
        {
            // Renaming/reordering native rows can emit temporary selection and
            // focus events. Preserve the workspace and pane by object identity.
            update();
            sidebar.SelectedItem = selected;
        }
        finally { sidebar.EndUpdate(); selectingWorkspace = wasSelecting; }
        if (selected is not null && selected == Current && active is not null) ActivatePane(selected, active);
        UpdateActions();
        if (selected == Current) active?.FocusPane();
    }

    private async Task Initialize()
    {
        toolbar.Enabled = false;
        sidebar.Enabled = false;
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
            gitWorker = PollGit();
            started.TrySetResult();
        }
        catch (Exception error)
        {
            // An opened store is not a restored session. Startup failures must
            // never checkpoint a partially created or empty replacement.
            if (session is not null)
            {
                session.ProtectExisting();
                sessionStatus.Text = strings["SessionUnavailable"];
            }
            UpdateActions();
            ShowError(error.Message);
            started.TrySetException(error);
        }
        finally { if (!shuttingDown && !closingWindow) toolbar.Enabled = sidebar.Enabled = true; }
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
        {
            // ConPTY must start with the measured renderer dimensions. Hidden
            // Dock controls have not been laid out and can report a tiny grid.
            workspace.Visible = true;
            workspace.BringToFront();
            workspace.ApplyRestoredRatios();
            foreach (var pane in workspace.Panes)
            {
                await StartPane(pane);
                if (pane is BrowserPane browser) browser.RestoreAddress(addresses[browser]);
            }
            workspace.Visible = false;
        }
        if (saved.Workspaces.Length == 0) await NewWorkspace();
        else sidebar.SelectedIndex = saved.SelectedWorkspace;
        sessionStatus.Text = strings[missingDirectory ? "SessionDirectoryFallback" : "SessionRestored"];
    }

    internal SessionSnapshot CaptureSession(IReadOnlyDictionary<TerminalPane, string>? directories = null) => new(SessionSnapshot.CurrentVersion, workspaceSequence,
        sidebar.SelectedIndex, Workspaces.Select(workspace => SessionLayout.Capture(workspace, directories)).ToArray());

    private async Task SaveSession()
    {
        if (session?.CanSave != true) return;
        try
        {
            var captures = await Task.WhenAll(Workspaces.SelectMany(workspace => workspace.Panes).OfType<TerminalPane>()
                .Select(async pane => (Pane: pane, Directory: await pane.CaptureWorkingDirectory())));
            var snapshot = CaptureSession(captures.ToDictionary(item => item.Pane, item => item.Directory));
            await Task.Run(() => session.Save(snapshot)); sessionStatus.Text = strings["SessionSaved"];
        }
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
            // A modal editor can restore a native child focus that does not
            // represent a new user pane selection.
            if (!selectingWorkspace && !editingWorkspaceTitle && Current is { } selected) ActivatePane(selected, active);
        };
        pane.Failed += ShowError;
        if (pane is TerminalPane terminal)
        {
            terminal.WorkingDirectoryChanged += () => { if (Current?.Active == terminal) UpdateActions(); };
            // Title arrival is display-only. Never project focus or selection.
            terminal.TitleChanged += () => { if (Current?.Active == terminal) UpdateCaption(); };
        }
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
        var paneLaunch = previous is TerminalPane terminal && kind == PaneKind.Terminal
            && SessionStore.IsExistingLocalDirectory(terminal.WorkingDirectory)
            ? launch with { Directory = terminal.WorkingDirectory } : launch;
        var pane = AddPane(workspace, kind, paneLaunch);
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

    private sealed record PaneLocation(WorkspaceView Workspace, WorkspacePane Pane, SplitContainer Split,
        SplitterPanel Panel, Control Sibling, Control Parent, int Index, double Ratio);

    private static PaneLocation LocatePane(WorkspaceView workspace, WorkspacePane pane)
    {
        var panel = (SplitterPanel)pane.Parent!;
        var split = (SplitContainer)panel.Parent!;
        var sibling = (panel == split.Panel1 ? split.Panel2 : split.Panel1).Controls[0];
        return new(workspace, pane, split, panel, sibling, split.Parent!, workspace.Panes.IndexOf(pane), workspace.SplitRatio(split));
    }

    private static void DetachPane(PaneLocation location)
    {
        // The removed split still owns the pane until a live destination
        // adopts it, or ClosePane disposes that split and its child controls.
        location.Sibling.Parent!.Controls.Remove(location.Sibling);
        location.Parent.Controls.Remove(location.Split);
        location.Parent.Controls.Add(location.Sibling);
        location.Workspace.Panes.Remove(location.Pane);
        location.Workspace.PendingRatios.Remove(location.Split);
    }

    private static void RestorePane(PaneLocation location)
    {
        location.Pane.Parent?.Controls.Remove(location.Pane);
        location.Sibling.Parent?.Controls.Remove(location.Sibling);
        location.Parent.Controls.Add(location.Split);
        location.Panel.Controls.Add(location.Pane);
        (location.Panel == location.Split.Panel1 ? location.Split.Panel2 : location.Split.Panel1).Controls.Add(location.Sibling);
        if (!location.Workspace.Panes.Contains(location.Pane)) location.Workspace.Panes.Insert(location.Index, location.Pane);
        location.Workspace.PendingRatios[location.Split] = location.Ratio;
    }

    private Task MovePaneToNewWorkspace()
    {
        if (Current is not { Active: { } pane } workspace || workspace.Panes.Count < 2) return Task.CompletedTask;
        var location = LocatePane(workspace, pane);
        var sequence = workspaceSequence + 1;
        var destination = new WorkspaceView(strings.Format("Workspace", sequence)) { Dock = DockStyle.Fill, Visible = false };
        var previousFence = selectingWorkspace;
        selectingWorkspace = true;
        try
        {
            content.Controls.Add(destination);
            sidebar.Items.Add(destination);
            DetachPane(location);
            destination.Controls.Add(pane);
            destination.Panes.Add(pane);
            destination.Active = pane;
            ActivatePane(workspace, workspace.Panes[0]);
            sidebar.SelectedItem = destination;
            workspaceSequence = sequence;
        }
        catch
        {
            // Keep the original split alive until adoption commits, so a
            // native reparent failure can restore the exact live resources.
            destination.Panes.Clear();
            RestorePane(location);
            content.Controls.Remove(destination);
            sidebar.Items.Remove(destination);
            destination.Dispose();
            sidebar.SelectedItem = workspace;
            ActivatePane(workspace, pane);
            throw;
        }
        finally
        {
            selectingWorkspace = previousFence;
            SelectWorkspace();
        }
        location.Split.Dispose();
        if (pane is BrowserPane browser) browser.FocusAddress();
        return Task.CompletedTask;
    }

    internal async Task ClosePane()
    {
        if (Current is not { Active: { } pane } workspace) return;
        if (workspace.Panes.Count == 1) { await RemoveWorkspace(workspace); return; }
        var location = LocatePane(workspace, pane);
        DetachPane(location);
        ActivatePane(workspace, workspace.Panes[0]);
        await pane.DisposeAsync();
        location.Split.Dispose();
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
        gitClosing.Cancel();
        Exception? metadataFailure = null;
        if (gitWorker is not null)
            try { await gitWorker; } catch (Exception error) { metadataFailure = error; }
        await PendingAction;
        Exception? failure = null;
        foreach (var workspace in Workspaces.ToArray())
            try { await RemoveWorkspace(workspace); } catch (Exception error) { failure ??= error; }
        if (environment is not null)
            try { await environment.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        if (browserEnvironment is not null)
            try { await browserEnvironment.DisposeAsync(); } catch (Exception error) { failure ??= error; }
        session?.Dispose();
        gitClosing.Dispose();
        gitWake.Dispose();
        shutdownComplete = true;
        if (failure is not null) throw failure;
        if (metadataFailure is not null) throw metadataFailure;
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
            else toolbar.Enabled = sidebar.Enabled = true;
        }
    }
}

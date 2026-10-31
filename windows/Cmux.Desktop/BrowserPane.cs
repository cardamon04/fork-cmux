using Microsoft.Web.WebView2.Core;

namespace Cmux.Desktop;

internal sealed class BrowserPane : WorkspacePane
{
    private readonly Strings strings;
    private readonly ToolStrip navigation = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripTextBox address = new() { AutoSize = false, Width = 260 };
    private readonly ToolStripStatusLabel message = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly StatusStrip footer = new();
    private CoreWebView2Controller? controller;
    private bool stopped;
    private ulong navigationId;
    internal override CoreWebView2 Core => controller!.CoreWebView2;
    internal override PaneKind Kind => PaneKind.Browser;
    internal string AddressText => address.Text;
    internal string StatusText => message.Text ?? "";

    internal BrowserPane(Strings strings)
    {
        this.strings = strings;
        Dock = DockStyle.Fill;
        Padding = new Padding(2);
        BackColor = Color.FromArgb(24, 26, 31);
        AddButton("Back", () => { if (Core.CanGoBack) Core.GoBack(); });
        AddButton("Forward", () => { if (Core.CanGoForward) Core.GoForward(); });
        AddButton("Reload", () => Core.Reload());
        address.AccessibleName = strings["Address"];
        address.ToolTipText = strings["Address"];
        address.Enter += (_, _) => NotifyActivated();
        address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            ClickNavigation("Go");
        };
        navigation.Items.Add(address);
        AddButton("Go", NavigateAddress);
        AddButton("DeveloperTools", () => Core.OpenDevToolsWindow());
        navigation.Enter += (_, _) => NotifyActivated();
        navigation.Enabled = false;
        footer.Items.Add(message);
        footer.SizingGrip = false;
        Controls.Add(navigation);
        Controls.Add(footer);
        message.Text = strings["Starting"];
        Resize += (_, _) => UpdateBoundsAndAddress();
        navigation.SizeChanged += (_, _) => UpdateBoundsAndAddress();
        footer.SizeChanged += (_, _) => UpdateBoundsAndAddress();
        VisibleChanged += (_, _) => { if (controller is not null) controller.IsVisible = Visible; };
    }

    private void AddButton(string key, Action action)
    {
        var button = new ToolStripButton(strings[key]) { Name = key, ToolTipText = strings[key], DisplayStyle = ToolStripItemDisplayStyle.Text };
        button.Click += (_, _) =>
        {
            if (stopped || controller is null) return;
            NotifyActivated();
            try { action(); } catch (Exception error) { message.Text = strings.Format("Error", error.Message); }
        };
        navigation.Items.Add(button);
    }

    private void UpdateBoundsAndAddress()
    {
        if (controller is not null)
            controller.Bounds = new Rectangle(Padding.Left, navigation.Bottom,
                Math.Max(1, ClientSize.Width - Padding.Horizontal), Math.Max(1, footer.Top - navigation.Bottom));
        var buttons = navigation.Items.OfType<ToolStripButton>().Sum(b => b.Width + b.Margin.Horizontal);
        address.Width = Math.Max(80, navigation.Width - buttons - 24);
    }

    internal override async Task Start(WebViewProfile profile)
    {
        controller = await profile.CreateController(Handle);
        controller.IsVisible = Visible;
        controller.GotFocus += (_, _) => { if (!stopped) NotifyActivated(); };
        // Web documents have no native message channel, host objects, or terminal
        // profile. These settings apply to every subsequent origin/frame.
        Core.Settings.AreHostObjectsAllowed = false;
        Core.Settings.IsWebMessageEnabled = false;
        Core.Settings.AreDevToolsEnabled = true;
        Core.Settings.IsStatusBarEnabled = false;
        Core.NavigationStarting += (_, e) =>
        {
            if (stopped) { e.Cancel = true; return; }
            if (!AllowsTopLevel(e.Uri)) { e.Cancel = true; message.Text = strings["BlockedAddress"]; }
            else { navigationId = e.NavigationId; message.Text = strings["Loading"]; }
        };
        Core.FrameNavigationStarting += (_, e) =>
        {
            if (stopped) { e.Cancel = true; return; }
            if (!AllowsFrame(e.Uri)) { e.Cancel = true; message.Text = strings["BlockedAddress"]; }
        };
        Core.LaunchingExternalUriScheme += (_, e) => { e.Cancel = true; if (!stopped) message.Text = strings["BlockedAddress"]; };
        Core.NewWindowRequested += (_, e) => { e.Handled = true; if (!stopped) message.Text = strings["PopupBlocked"]; };
        Core.DownloadStarting += (_, e) => { e.Cancel = true; if (!stopped) message.Text = strings["DownloadBlocked"]; };
        Core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; if (!stopped) message.Text = strings["PermissionBlocked"]; };
        Core.SourceChanged += (_, _) => { if (!stopped) address.Text = Core.Source; };
        Core.HistoryChanged += (_, _) => { if (!stopped) UpdateHistory(); };
        Core.NavigationCompleted += (_, e) =>
        {
            if (stopped || e.NavigationId != navigationId) return;
            if (e.IsSuccess) message.Text = strings["Ready"];
            else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                message.Text = strings["NavigationFailed"];
            UpdateHistory();
        };
        UpdateBoundsAndAddress();
        navigation.Enabled = true;
        UpdateHistory();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void InitialDocument(object? _, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.NavigationId == navigationId) ready.TrySetResult(e.IsSuccess);
        }
        Core.NavigationCompleted += InitialDocument;
        try
        {
            Core.Navigate("about:blank");
            if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(20)))
                throw new InvalidOperationException(strings["NavigationFailed"]);
        }
        finally { Core.NavigationCompleted -= InitialDocument; }
    }

    private void UpdateHistory()
    {
        navigation.Items["Back"]!.Enabled = Core.CanGoBack;
        navigation.Items["Forward"]!.Enabled = Core.CanGoForward;
    }

    private void NavigateAddress()
    {
        if (!TryAddress(address.Text, out var target)) { message.Text = strings["InvalidAddress"]; return; }
        Core.Navigate(target);
    }

    internal static bool TryAddress(string input, out string target)
    {
        target = "";
        var text = input.Trim();
        if (text.Length == 0 || text.Any(c => char.IsControl(c))) return false;
        if (text == "about:blank") { target = text; return true; }
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out var candidate)
                || candidate.UserInfo.Length != 0 || (text.Contains(':') && !candidate.IsLoopback)) return false;
            text = (candidate.IsLoopback ? "http://" : "https://") + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !IsHttp(uri) || uri.Host.Length == 0) return false;
        target = uri.AbsoluteUri;
        return true;
    }

    private static bool IsHttp(Uri uri) => uri.Scheme is "http" or "https";
    private static bool AllowsTopLevel(string text) => text == "about:blank"
        || Uri.TryCreate(text, UriKind.Absolute, out var uri) && IsHttp(uri);
    private static bool AllowsFrame(string text) => text is "about:blank" or "about:srcdoc"
        || Uri.TryCreate(text, UriKind.Absolute, out var uri) && (IsHttp(uri) || uri.Scheme is "data" or "blob");

    internal void NavigateFromAddress(string text) { address.Text = text; ClickNavigation("Go"); }
    internal void ClickNavigation(string key) => ((ToolStripButton)navigation.Items[key]!).PerformClick();
    internal bool IsNavigationEnabled(string key) => navigation.Enabled && navigation.Items[key]!.Enabled;
    internal override void FocusPane()
    {
        if (stopped) return;
        if (controller is null || Core.Source == "about:blank") address.Focus();
        else controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
    }
    public override ValueTask DisposeAsync()
    {
        if (stopped) return ValueTask.CompletedTask;
        stopped = true;
        navigation.Enabled = false;
        controller?.Close();
        controller = null;
        return ValueTask.CompletedTask;
    }
}

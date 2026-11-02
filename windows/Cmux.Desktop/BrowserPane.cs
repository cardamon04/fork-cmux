using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;

namespace Cmux.Desktop;

internal sealed class BrowserPane : WorkspacePane
{
    private readonly Strings strings;
    private readonly ToolStrip navigation = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripTextBox address = new() { AutoSize = false, Width = 260 };
    private readonly ToolStripStatusLabel message = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly StatusStrip footer = new();
    private readonly Label failure = new() { TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.White, ForeColor = Color.Black, Visible = false };
    private CoreWebView2Controller? controller;
    private TaskCompletionSource<bool>? initialDocument;
    private bool stopped;
    private bool failed;
    private readonly BrowserLoadState loading = new();
    internal override CoreWebView2 Core => controller?.CoreWebView2 ?? throw new ObjectDisposedException(nameof(BrowserPane));
    internal override PaneKind Kind => PaneKind.Browser;
    internal string AddressText => address.Text;
    internal string SessionAddress { get; private set; } = "about:blank";
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
        AddButton("Stop", StopLoading);
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
        Controls.Add(failure);
        failure.Click += (_, _) => { Select(); NotifyActivated(); };
        message.Text = strings["Starting"];
        Resize += (_, _) => UpdateBoundsAndAddress();
        navigation.SizeChanged += (_, _) => UpdateBoundsAndAddress();
        footer.SizeChanged += (_, _) => UpdateBoundsAndAddress();
        VisibleChanged += (_, _) => UseNative(() => controller!.IsVisible = Visible);
    }

    private void AddButton(string key, Action action)
    {
        var button = new ToolStripButton(strings[key]) { Name = key, ToolTipText = strings[key], DisplayStyle = ToolStripItemDisplayStyle.Text };
        button.Click += (_, _) =>
        {
            if (Unavailable) return;
            NotifyActivated();
            UseNative(action);
        };
        navigation.Items.Add(button);
    }

    private void UpdateBoundsAndAddress()
    {
        var bounds = new Rectangle(Padding.Left, navigation.Bottom,
            Math.Max(1, ClientSize.Width - Padding.Horizontal), Math.Max(1, footer.Top - navigation.Bottom));
        failure.Bounds = bounds;
        UseNative(() => controller!.Bounds = bounds);
        var buttons = navigation.Items.OfType<ToolStripButton>().Sum(b => b.Width + b.Margin.Horizontal);
        address.Width = Math.Max(80, navigation.Width - buttons - 24);
    }

    internal override async Task Start(WebViewProfile profile)
    {
        controller = await profile.CreateController(Handle);
        var core = Core;
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)
                BrowserFailed();
        };
        controller.IsVisible = Visible;
        controller.GotFocus += (_, _) => { if (!Unavailable && ContainsFocus) NotifyActivated(); };
        // Web documents have no native message channel, host objects, or terminal
        // profile. These settings apply to every subsequent origin/frame.
        Core.Settings.AreHostObjectsAllowed = false;
        Core.Settings.IsWebMessageEnabled = false;
        Core.Settings.AreDevToolsEnabled = true;
        Core.Settings.IsStatusBarEnabled = false;
        Core.NavigationStarting += (_, e) =>
        {
            if (Unavailable) { e.Cancel = true; return; }
            if (!AllowsTopLevel(e.Uri)) { e.Cancel = true; message.Text = strings["BlockedAddress"]; }
            else { loading.Begin(e.NavigationId); message.Text = strings["Loading"]; UpdateHistory(); }
        };
        Core.FrameNavigationStarting += (_, e) =>
        {
            if (Unavailable) { e.Cancel = true; return; }
            if (!AllowsFrame(e.Uri)) { e.Cancel = true; message.Text = strings["BlockedAddress"]; }
        };
        Core.LaunchingExternalUriScheme += (_, e) => { e.Cancel = true; if (!Unavailable) message.Text = strings["BlockedAddress"]; };
        Core.NewWindowRequested += (_, e) => { e.Handled = true; if (!Unavailable) message.Text = strings["PopupBlocked"]; };
        Core.DownloadStarting += (_, e) => { e.Cancel = true; if (!Unavailable) message.Text = strings["DownloadBlocked"]; };
        Core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; if (!Unavailable) message.Text = strings["PermissionBlocked"]; };
        Core.SourceChanged += (_, _) => UseNative(() =>
        {
            address.Text = Core.Source;
            SessionAddress = SessionSnapshot.IsBrowserAddress(Core.Source) ? Core.Source : "about:blank";
        });
        Core.HistoryChanged += (_, _) => UpdateHistory();
        Core.NavigationCompleted += (_, e) =>
        {
            if (Unavailable || !loading.Complete(e.NavigationId)) return;
            if (loading.CompletionStatus(e.IsSuccess, e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) is { } status)
                message.Text = strings[status];
            UpdateHistory();
        };
        UpdateBoundsAndAddress();
        navigation.Enabled = true;
        UpdateHistory();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        initialDocument = ready;
        void InitialDocument(object? _, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.NavigationId == loading.NavigationId) ready.TrySetResult(e.IsSuccess);
        }
        Core.NavigationCompleted += InitialDocument;
        try
        {
            Core.Navigate("about:blank");
            if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(20)))
                throw new InvalidOperationException(strings["NavigationFailed"]);
        }
        finally
        {
            if (!Unavailable) UseNative(() => core.NavigationCompleted -= InitialDocument);
            initialDocument = null;
            UpdateHistory();
        }
    }

    private void UpdateHistory()
    {
        UseNative(() =>
        {
            navigation.Items["Back"]!.Enabled = Core.CanGoBack;
            navigation.Items["Forward"]!.Enabled = Core.CanGoForward;
            navigation.Items["Stop"]!.Enabled = initialDocument is null && loading.IsLoading && !loading.StopRequested;
        });
    }

    private void StopLoading()
    {
        if (initialDocument is not null || !loading.RequestStop()) return;
        Core.Stop();
        UpdateHistory();
    }

    private bool Unavailable => stopped || failed || controller is null;
    private void UseNative(Action action)
    {
        if (Unavailable) return;
        try { action(); }
        catch (InvalidOperationException) { BrowserFailed(); }
        catch (COMException) { BrowserFailed(); }
    }
    private void BrowserFailed()
    {
        if (stopped || failed) return;
        failed = true;
        initialDocument?.TrySetResult(false);
        navigation.Enabled = false;
        failure.Text = message.Text = strings["BrowserFailed"];
        failure.Visible = true;
        failure.BringToFront();
        CloseController();
    }
    private void CloseController()
    {
        var owned = controller;
        controller = null;
        try { owned?.Close(); }
        catch (InvalidOperationException) { } // The SDK disposes controllers when their browser exits.
        catch (COMException) { }
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
    internal void RestoreAddress(string text)
    {
        // Retain the saved target while a new document has yet to commit.
        // A later SourceChanged replaces it with the actual navigated address.
        SessionAddress = text;
        address.Text = text;
        // Programmatic navigation must not synthesize a Go-button activation:
        // a newer pane selection can own focus while initialization awaits.
        UseNative(NavigateAddress);
    }
    internal void ClickNavigation(string key) => ((ToolStripButton)navigation.Items[key]!).PerformClick();
    internal bool IsNavigationEnabled(string key) => navigation.Enabled && navigation.Items[key]!.Enabled;
    internal void FocusAddress()
    {
        if (stopped) return;
        if (failed) Select();
        else { address.Focus(); address.SelectAll(); }
        NotifyActivated();
    }

    internal override void FocusPane()
    {
        if (stopped) return;
        if (failed) { Select(); NotifyActivated(); return; }
        UseNative(() =>
        {
            if (Core.Source == "about:blank")
            {
                address.Focus();
                // A narrow pane can place the address control in overflow.
                // Keep explicit pane selection inside the browser even when
                // that hidden control cannot receive native focus.
                if (!address.Control.Focused) controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            }
            else controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        });
        if (failed) Select();
        NotifyActivated();
    }
    public override ValueTask DisposeAsync()
    {
        if (stopped) return ValueTask.CompletedTask;
        stopped = true;
        navigation.Enabled = false;
        CloseController();
        return ValueTask.CompletedTask;
    }
}

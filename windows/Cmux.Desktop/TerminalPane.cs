using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Web.WebView2.Core;

namespace Cmux.Desktop;

internal sealed class TerminalPane : WorkspacePane
{
    internal const string Page = "https://cmux-terminal.invalid/index.html";
    private const int MaxInputCharacters = 32768;
    private const string PasteStart = "\u001b[200~";
    private const string PasteEnd = "\u001b[201~";
    private readonly Channel<string> output = Channel.CreateBounded<string>(32);
    private readonly TaskCompletionSource<(short Cols, short Rows)> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource closing = new();
    private readonly Strings strings;
    private readonly IClipboard clipboard;
    private readonly ShellLaunch launch;
    internal string WorkingDirectory { get; private set; }
    internal event Action? WorkingDirectoryChanged;
    private readonly TerminalTitle title = new();
    internal string Title => title.Value;
    internal event Action? TitleChanged;
    internal event Action? Bell;
    internal bool HasUnread;
    private string? pendingDirectory;
    private string? lastDirectoryReport;
    private long directoryRevision;
    private Task? directoryWorker;
    private CoreWebView2Controller? controller;
    private TerminalConnection? connection;
    private Task? pump;
    private TaskCompletionSource? ack;
    private long sequence;
    private bool stopped;
    private int viewportDeferrals;
    internal override CoreWebView2 Core => controller!.CoreWebView2;
    internal override PaneKind Kind => PaneKind.Terminal;
    internal override bool SupportsClipboard => true;
    internal int ProcessId => connection!.ProcessId;

    internal TerminalPane(Strings strings, IClipboard clipboard, ShellLaunch launch)
    {
        this.strings = strings;
        this.clipboard = clipboard;
        this.launch = launch;
        WorkingDirectory = launch.Directory;
        Dock = DockStyle.Fill;
        Padding = new Padding(2);
        BackColor = Color.FromArgb(24, 26, 31);
        Resize += (_, _) => UpdateControllerBounds();
        PaddingChanged += (_, _) => UpdateControllerBounds();
        VisibleChanged += (_, _) => { if (controller is not null) controller.IsVisible = Visible; };
    }

    private void UpdateControllerBounds()
    {
        if (controller is not null && viewportDeferrals == 0)
            controller.Bounds = new Rectangle(Padding.Left, Padding.Top,
                Math.Max(1, ClientSize.Width - Padding.Horizontal), Math.Max(1, ClientSize.Height - Padding.Vertical));
    }

    internal IDisposable DeferViewportLayout()
    {
        viewportDeferrals++;
        return new ViewportLayout(this);
    }

    private sealed class ViewportLayout(TerminalPane pane) : IDisposable
    {
        private TerminalPane? owner = pane;
        public void Dispose()
        {
            if (owner is not { } current) return;
            owner = null;
            current.viewportDeferrals--;
            current.UpdateControllerBounds();
        }
    }

    internal override async Task Start(WebViewProfile environment)
    {
        controller = await environment.CreateController(Handle);
        UpdateControllerBounds();
        controller.IsVisible = Visible;
        controller.GotFocus += (_, _) => { if (ContainsFocus) NotifyActivated(); };
        Core.Settings.AreHostObjectsAllowed = false;
        Core.Settings.AreDefaultContextMenusEnabled = false;
        Core.Settings.AreDefaultScriptDialogsEnabled = false;
        Core.Settings.AreDevToolsEnabled = false;
        Core.Settings.IsStatusBarEnabled = false;
        Core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        Core.NewWindowRequested += (_, e) => e.Handled = true;
        Core.DownloadStarting += (_, e) => e.Cancel = true;
        Core.NavigationStarting += (_, e) => { if (!IsPage(e.Uri)) e.Cancel = true; };
        Core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        Core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        Core.WebResourceRequested += (_, e) => Serve(environment.Core, e);
        Core.WebMessageReceived += Message;
        Core.Navigate(Page);
        var size = await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        pump = PumpOutput();
        connection = new(launch, size.Cols, size.Rows, text =>
        {
            try { output.Writer.WriteAsync(text, closing.Token).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
            catch (ChannelClosedException) when (closing.IsCancellationRequested) { }
        });
        await connection.Started;
    }

    private static bool IsPage(string uri) => string.Equals(uri, Page, StringComparison.OrdinalIgnoreCase);
    private void Serve(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var uri = new Uri(args.Request.Uri);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/index.html"] = "text/html", ["/terminal.js"] = "text/javascript",
            ["/terminal.css"] = "text/css", ["/xterm.js"] = "text/javascript", ["/xterm.css"] = "text/css",
            ["/addon-fit.js"] = "text/javascript", ["/addon-unicode11.js"] = "text/javascript",
            ["/addon-search.js"] = "text/javascript", ["/find.js"] = "text/javascript",
        };
        if (uri.GetLeftPart(UriPartial.Authority) != "https://cmux-terminal.invalid" || !files.TryGetValue(uri.AbsolutePath, out var mime))
        {
            args.Response = environment.CreateWebResourceResponse(new MemoryStream(), 403, "Forbidden", "");
            return;
        }
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", uri.AbsolutePath[1..]));
        var headers = $"Content-Type: {mime}; charset=utf-8\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src data:; connect-src 'none'; frame-src 'none'; object-src 'none'\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store";
        args.Response = environment.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), 200, "OK", headers);
    }

    private async void Message(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (stopped || !IsPage(e.Source) || !IsPage(Core.Source)) return;
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            switch (root.GetProperty("kind").GetString())
            {
                case "ready":
                    Core.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        kind = "configure", locale = System.Globalization.CultureInfo.CurrentUICulture.Name,
                        cwdLimit = WorkingDirectoryReport.MaxUriCharacters, titleLimit = TerminalTitle.MaxCharacters,
                        windowsBuild = Environment.OSVersion.Version.Build,
                        strings = new[] { "FindPlaceholder", "FindPrevious", "FindNext", "FindCase", "FindClose", "FindEmpty", "FindMissing", "FindResults", "FindSearching" }
                            .ToDictionary(key => key, key => strings[key]),
                    }));
                    ready.TrySetResult(SizeFrom(root));
                    break;
                case "ack": if (root.GetProperty("id").GetInt64() == sequence) ack?.TrySetResult(); break;
                case "input" when connection is not null:
                    var text = root.GetProperty("data").GetString()!;
                    // xterm surrounds bracketed paste with protocol delimiters.
                    // Apply the shared limit to content, retaining the envelope.
                    var contentLength = text.StartsWith(PasteStart, StringComparison.Ordinal)
                        && text.EndsWith(PasteEnd, StringComparison.Ordinal)
                        ? text.Length - PasteStart.Length - PasteEnd.Length : text.Length;
                    if (contentLength > MaxInputCharacters) throw new InvalidOperationException(strings["PasteTooLarge"]);
                    var win32Input = root.TryGetProperty("win32Input", out var mode) && mode.ValueKind == JsonValueKind.True;
                    await connection.Write(ConPtyInput.Encode(text, win32Input));
                    break;
                case "resize" when connection is not null:
                    var size = SizeFrom(root);
                    await connection.Resize(size.Cols, size.Rows);
                    break;
                case "focus": if (ContainsFocus) NotifyActivated(); break;
                case "activate": if (Visible) { FocusPane(); NotifyActivated(); } break;
                case "bell": Bell?.Invoke(); break;
                case "title":
                    if (root.TryGetProperty("data", out var titleValue) && titleValue.ValueKind == JsonValueKind.String
                        && title.Update(titleValue.GetString())) TitleChanged?.Invoke();
                    break;
                case "cwd":
                    var report = root.GetProperty("data").GetString();
                    if (report is null || report.Length > WorkingDirectoryReport.MaxUriCharacters) break;
                    pendingDirectory = report;
                    lastDirectoryReport = report;
                    directoryRevision++;
                    if (directoryWorker is null || directoryWorker.IsCompleted) directoryWorker = UpdateDirectory();
                    break;
            }
        }
        catch (Exception error) { if (!stopped) NotifyFailure(error.Message); }
    }

    private async Task UpdateDirectory()
    {
        // One validation worker and one latest report per pane bound disk work.
        // A slow old report cannot replace a newer report after its await.
        while (!stopped && pendingDirectory is { } report)
        {
            pendingDirectory = null;
            var revision = directoryRevision;
            var path = await Task.Run(() => WorkingDirectoryReport.Validate(report));
            ApplyDirectory(path, revision);
        }
    }

    private void ApplyDirectory(string? path, long revision)
    {
        if (!stopped && revision == directoryRevision && path is not null
            && !path.Equals(WorkingDirectory, StringComparison.Ordinal))
        {
            WorkingDirectory = path;
            WorkingDirectoryChanged?.Invoke();
        }
    }

    internal async Task<string> CaptureWorkingDirectory()
    {
        // Saving captures one report, rather than waiting for a live stream to
        // become idle. Subsequent reports cannot replace this checkpoint value.
        var report = lastDirectoryReport;
        var fallback = WorkingDirectory;
        var revision = directoryRevision;
        if (report is null || stopped) return fallback;
        var path = await Task.Run(() => WorkingDirectoryReport.Validate(report));
        ApplyDirectory(path, revision);
        return path ?? fallback;
    }

    private (short Cols, short Rows) SizeFrom(JsonElement root)
    {
        var cols = root.GetProperty("cols").GetInt32();
        var rows = root.GetProperty("rows").GetInt32();
        if (cols < 1 || rows < 1 || cols > 1000 || rows > 1000) throw new InvalidOperationException(strings["InvalidSize"]);
        return ((short)cols, (short)rows);
    }

    private async Task PumpOutput()
    {
        try
        {
            while (await output.Reader.WaitToReadAsync(closing.Token))
            {
                var batch = new StringBuilder();
                while (batch.Length < 32768 && output.Reader.TryRead(out var part)) batch.Append(part);
                ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Core.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "output", data = batch.ToString(), id = ++sequence }));
                await ack.Task.WaitAsync(TimeSpan.FromSeconds(20), closing.Token);
            }
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception error)
        {
            NotifyFailure(error.Message);
            closing.Cancel(); // Unblock the native output drain even if the renderer failed.
        }
    }

    internal void FocusTerminal() => FocusPane();
    internal void ChangeFontSize(string action)
    {
        if (stopped || controller is null || !IsPage(Core.Source)) return;
        Core.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "fontSize", action }));
    }
    internal void Find()
    {
        if (stopped || controller is null || !IsPage(Core.Source)) return;
        controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        Core.PostWebMessageAsJson("{\"kind\":\"find\"}");
        NotifyActivated();
    }
    internal override void FocusPane()
    {
        if (stopped || controller is null) return;
        controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        NotifyActivated();
    }
    internal override async Task Copy()
    {
        if (!IsPage(Core.Source)) return;
        var selection = JsonSerializer.Deserialize<string>(await Core.ExecuteScriptAsync("window.cmux.copySelection()"));
        if (!string.IsNullOrEmpty(selection)) clipboard.Write(selection);
    }
    internal async Task SelectAllOutput()
    {
        if (stopped || controller is null || !IsPage(Core.Source)) return;
        await Core.ExecuteScriptAsync("window.cmux.selectAllOutput()");
    }
    internal override void Paste()
    {
        if (!IsPage(Core.Source)) return;
        var text = clipboard.Read();
        if (text.Length > MaxInputCharacters) throw new InvalidOperationException(strings["PasteTooLarge"]);
        Core.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "paste", data = text }));
    }
    public override async ValueTask DisposeAsync()
    {
        if (stopped) return;
        stopped = true;
        closing.Cancel();
        output.Writer.TryComplete();
        try
        {
            if (connection is not null) await connection.DisposeAsync();
            if (pump is not null) await pump;
        }
        finally { controller?.Close(); controller = null; closing.Dispose(); }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal sealed class BrowserFixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentBag<Task> clients = [];
    private readonly ConcurrentDictionary<string, int> counts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> requested = new();
    private readonly Task server;
    internal string Origin { get; }
    internal BrowserFixture()
    {
        listener.Start();
        Origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        server = Listen();
    }
    internal string Url(string path) => Origin + "/" + path;
    internal int Requests(string path) => counts.GetValueOrDefault("/" + path);
    internal Task Requested(string path) => requested.GetOrAdd("/" + path,
        _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private async Task Listen()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
                clients.Add(Respond(await listener.AcceptTcpClientAsync(stopping.Token)));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (SocketException) when (stopping.IsCancellationRequested) { }
    }

    private async Task Respond(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(stopping.Token);
                if (request is null) return;
                var parts = request.Split(' ');
                if (parts.Length != 3 || parts[0] != "GET") return;
                var size = request.Length;
                while (await reader.ReadLineAsync(stopping.Token) is { Length: > 0 } header)
                    if ((size += header.Length) > 16384) return;
                var path = parts[1].Split('?')[0];
                var count = counts.AddOrUpdate(path, 1, (_, value) => value + 1);
                requested.GetOrAdd(path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                if (path == "/slow") await Task.Delay(Timeout.Infinite, stopping.Token);
                var download = path == "/download";
                var state = path == "/one"
                    ? "document.cookie='cmuxBrowser=owned;Max-Age=3600;SameSite=Lax;Path=/';localStorage.setItem('cmuxBrowser','owned');" : "";
                var body = download ? "private browser fixture" : $$"""
                    <!doctype html><meta charset="utf-8"><title>cmux fixture {{path}}</title>
                    <style>body{font:18px sans-serif;padding:20px}input,button{padding:8px;margin:8px}</style>
                    <body data-request="{{count}}"><h1 id="page">{{path}}</h1>
                    <label>名前<input id="name"></label><button id="apply">Apply</button><output id="result"></output>
                    <a href="/two" id="next">Two</a><button id="popup">Popup</button>
                    <a href="/download" id="download">Download</a><button id="location">Location</button>
                    <script>
                    {{state}}
                    document.querySelector('#apply').onclick=e=>{document.querySelector('#result').textContent=document.querySelector('#name').value;window.trusted=e.isTrusted;};
                    document.querySelector('#popup').onclick=()=>window.open('/two');
                    document.querySelector('#location').onclick=()=>navigator.geolocation.getCurrentPosition(()=>window.locationResult='allowed',()=>window.locationResult='denied');
                    window.nativeAttempt='not-run';try{chrome.webview.postMessage({kind:'input',data:"Write-Output ('BROWSER_'+'INJECTION')\r"});window.nativeAttempt='sent';}catch(e){window.nativeAttempt='blocked';}
                    </script></body>
                    """;
                var payload = Encoding.UTF8.GetBytes(body);
                var headers = "HTTP/1.1 200 OK\r\nConnection: close\r\nCache-Control: no-store\r\nContent-Type: "
                    + (download ? "application/octet-stream\r\nContent-Disposition: attachment; filename=cmux-fixture.txt" : "text/html; charset=utf-8")
                    + "\r\nContent-Length: " + payload.Length + "\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), stopping.Token);
                await stream.WriteAsync(payload, stopping.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (IOException) { } // The browser may cancel a load when its pane closes.
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        listener.Stop();
        await server;
        await Task.WhenAll(clients);
        stopping.Dispose();
    }
}

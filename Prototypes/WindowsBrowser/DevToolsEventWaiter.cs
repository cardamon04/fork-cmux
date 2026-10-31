using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace WindowsBrowser;

internal sealed class DevToolsEventWaiter : IDisposable
{
    private readonly CoreWebView2DevToolsProtocolEventReceiver receiver;
    private readonly EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler;
    private readonly TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DevToolsEventWaiter(CoreWebView2 core, string name, Func<JsonElement, bool> matches)
    {
        receiver = core.GetDevToolsProtocolEventReceiver(name);
        handler = (_, args) =>
        {
            try
            {
                using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
                if (matches(document.RootElement)) completion.TrySetResult(document.RootElement.Clone());
            }
            catch (Exception error) { completion.TrySetException(error); }
        };
        receiver.DevToolsProtocolEventReceived += handler;
    }

    internal Task<JsonElement> Wait() => completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    public void Dispose() => receiver.DevToolsProtocolEventReceived -= handler;
}

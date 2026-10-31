namespace WindowsBrowser;

// A real HWND and STA message pump are needed by WebView2. WS_VISIBLE is
// deliberately absent; the probe never activates or shows a desktop window.
internal sealed class HiddenBrowserWindow : NativeWindow, IDisposable
{
    internal HiddenBrowserWindow()
    {
        CreateHandle(new CreateParams
        {
            Style = unchecked((int)0x80000000), // WS_POPUP
            Width = 1024,
            Height = 768,
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    internal bool IsVisible => IsWindowVisible(Handle);

    public void Dispose() => DestroyHandle();
}

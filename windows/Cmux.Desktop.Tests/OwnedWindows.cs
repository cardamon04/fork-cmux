using System.Runtime.InteropServices;

internal static class OwnedWindows
{
    private delegate bool Callback(nint window, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(Callback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Bounds bounds);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint device, uint flags);

    internal static HashSet<nint> Visible(uint browserProcess)
    {
        var result = new HashSet<nint>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var process);
            if ((process == browserProcess || process == Environment.ProcessId) && IsWindowVisible(window)) result.Add(window);
            return true;
        }, 0);
        return result;
    }

    internal static void Close(nint window, uint browserProcess)
    {
        GetWindowThreadProcessId(window, out var process);
        if (process != browserProcess && process != Environment.ProcessId)
            throw new InvalidOperationException("Refusing to close a window outside the owned test processes.");
        if (!PostMessageW(window, 0x0010, 0, 0)) throw new InvalidOperationException("The owned inspector window did not accept WM_CLOSE.");
    }

    internal static void Capture(nint window, string path)
    {
        GetWindowThreadProcessId(window, out var process);
        if (process != Environment.ProcessId || !GetWindowRect(window, out var bounds))
            throw new InvalidOperationException("Only this test application's window can be captured.");
        using var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        using var graphics = Graphics.FromImage(bitmap);
        var device = graphics.GetHdc();
        bool captured;
        try { captured = PrintWindow(window, device, 2); }
        finally { graphics.ReleaseHdc(device); }
        if (captured) bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}

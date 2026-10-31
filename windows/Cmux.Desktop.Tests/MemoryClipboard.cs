using Cmux.Desktop;

internal sealed class MemoryClipboard : IClipboard
{
    internal string Text = "";
    public string Read() => Text;
    public void Write(string text) => Text = text;
}

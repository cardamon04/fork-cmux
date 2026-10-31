namespace Cmux.Desktop;

internal sealed class SystemClipboard : IClipboard
{
    public string Read() => Clipboard.ContainsText() ? Clipboard.GetText() : "";
    public void Write(string text) => Clipboard.SetText(text);
}

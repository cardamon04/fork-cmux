namespace Cmux.Desktop;

internal interface IClipboard
{
    string Read();
    void Write(string text);
}

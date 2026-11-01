using System.Globalization;
using System.Text;

namespace Cmux.Desktop;

internal static class ConPtyInput
{
    internal static string Encode(string text, bool win32Input)
    {
        // Size validation applies to the original text, before wire encoding.
        // Preserve bracketed paste as a complete protocol-owned envelope.
        if (!win32Input || text.StartsWith("\u001b[200~", StringComparison.Ordinal)
            && text.EndsWith("\u001b[201~", StringComparison.Ordinal)) return text;
        StringBuilder? encoded = null;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] <= 0x7f || char.IsControl(text[index])) continue;
            var units = 1;
            if (char.IsSurrogate(text[index]))
            {
                if (index + 1 >= text.Length || !char.IsSurrogatePair(text, index)) continue;
                units = 2;
            }
            encoded ??= new StringBuilder(text.Length);
            encoded.Append(text, start, index - start);
            // The UTF-8 fallback synthesizes Alt+numpad key-up events, which
            // legacy readers can discard. Negotiated VK_PACKET records keep
            // each UTF-16 unit on a real key-down event without shell changes.
            for (var part = 0; part < units; part++)
            {
                var unit = ((int)text[index + part]).ToString(CultureInfo.InvariantCulture);
                encoded.Append("\u001b[231;0;").Append(unit).Append(";1;0;1_");
                encoded.Append("\u001b[231;0;").Append(unit).Append(";0;0;1_");
            }
            index += units - 1;
            start = index + 1;
        }
        return encoded?.Append(text, start, text.Length - start).ToString() ?? text;
    }
}

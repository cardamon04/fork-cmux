using System.Globalization;
using System.Text.Json;

namespace Cmux.Desktop;

internal sealed class Strings
{
    private readonly IReadOnlyDictionary<string, string> values;
    internal Strings(CultureInfo? culture = null)
    {
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream("Cmux.Desktop.Strings.json")!;
        var locales = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)!;
        var selected = culture ?? CultureInfo.CurrentUICulture;
        values = locales["en"];
        for (var current = selected; current != CultureInfo.InvariantCulture; current = current.Parent)
        {
            if (locales.TryGetValue(current.Name, out var catalog)) { values = catalog; return; }
        }
        if (selected.TwoLetterISOLanguageName == "zh") values = locales["zh-Hans"];
    }
    internal string this[string key] => values[key];
    internal string Format(string key, object value) => string.Format(CultureInfo.CurrentCulture, values[key], value);
}

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
        var name = (culture ?? CultureInfo.CurrentUICulture).Name;
        var locale = name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? (name.Contains("TW") || name.Contains("HK") || name.Contains("Hant") ? "zh-Hant" : "zh-Hans")
            : name.Split('-')[0];
        values = locales.GetValueOrDefault(locale) ?? locales["en"];
    }
    internal string this[string key] => values[key];
    internal string Format(string key, object value) => string.Format(CultureInfo.CurrentCulture, values[key], value);
}

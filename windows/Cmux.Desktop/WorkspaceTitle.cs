namespace Cmux.Desktop;

internal static class WorkspaceTitle
{
    internal const int MaxCharacters = 512;

    // Preserve the existing version-1 stored-title policy.
    internal static bool IsValidStored(string? value) => value is { Length: > 0 and <= MaxCharacters }
        && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    internal static bool IsValidRename(string? value)
    {
        if (value is null || !IsValidStored(value)) return false;
        // New user input must round-trip without replacing malformed UTF-16.
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}

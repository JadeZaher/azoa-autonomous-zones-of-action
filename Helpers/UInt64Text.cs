using System.Globalization;

namespace AZOA.WebAPI.Helpers;

/// <summary>Canonical lossless unsigned chain identifiers; see Helpers/AGENTS.md.</summary>
public static class UInt64Text
{
    public static bool TryParse(string? text, out ulong value)
        => ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value.ToString(CultureInfo.InvariantCulture) == text;

    public static ulong Parse(string text)
        => TryParse(text, out var value) ? value : throw new FormatException("Expected canonical unsigned 64-bit decimal text.");
}

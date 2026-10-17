using System.Globalization;
using System.Numerics;

namespace AZOA.WebAPI.Helpers;

/// <summary>Parses canonical unsigned decimal text without narrowing its integer range.</summary>
public static class UnsignedIntegerText
{
    public static bool TryParse(string? text, out BigInteger value)
        => BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 0 && value.ToString(CultureInfo.InvariantCulture) == text;
}

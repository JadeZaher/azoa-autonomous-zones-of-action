using System.Globalization;
using System.Text.RegularExpressions;

namespace AZOA.WebAPI.Helpers;

/// <summary>Parses the optional <c>Admit-Before</c> request header: an RFC 3339 UTC instant; see Services/Reserve/AGENTS.md §admission-deadline.</summary>
public static partial class AdmitBeforeHeader
{
    /// <summary>The request header that names the latest instant an exchange may be admitted.</summary>
    public const string Name = "Admit-Before";

    /// <summary>Absent or blank is no deadline; otherwise only an RFC 3339 date-time in UTC (<c>Z</c> or <c>+00:00</c>) is accepted.</summary>
    public static bool TryParse(string? raw, out DateTimeOffset? deadline)
    {
        deadline = null;
        if (raw is null) return true;
        var value = raw.Trim();
        if (value.Length == 0) return true;
        if (!Rfc3339Utc().IsMatch(value)) return false;
        if (!DateTimeOffset.TryParseExact(value.ToUpperInvariant(), ["yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        deadline = parsed.ToUniversalTime();
        return true;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d{1,7})?([Zz]|\+00:00)$", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339Utc();
}

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OASIS.WebAPI.Core.Diagnostics;

/// <summary>
/// Replaces sensitive values in dictionaries and JSON strings with <c>[REDACTED]</c>
/// using case-insensitive substring matching against the configured deny-list.
/// </summary>
public static class RedactionFilter
{
    private const string Replacement = "[REDACTED]";

    /// <summary>
    /// Returns a new dictionary with any entry whose key contains a deny-list substring
    /// replaced by <c>[REDACTED]</c>. Input enumerable is not mutated.
    /// </summary>
    public static Dictionary<string, object?> Redact(
        IEnumerable<KeyValuePair<string, object?>> source,
        IReadOnlyList<string> denyList)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            result[key] = IsSensitiveKey(key, denyList) ? Replacement : value;
        }
        return result;
    }

    /// <summary>
    /// Redacts JSON string values whose property name matches a deny-list entry.
    /// Replaces <c>"key":"anyvalue"</c> with <c>"key":"[REDACTED]"</c>.
    /// </summary>
    public static string RedactJson(string json, IReadOnlyList<string> denyList)
    {
        if (string.IsNullOrEmpty(json)) return json;

        foreach (var key in denyList)
        {
            // Match "key":"<value>" — value is any quoted string (non-greedy).
            var pattern = $"\"([^\"]*{Regex.Escape(key)}[^\"]*)\": *\"[^\"]*\"";
            json = Regex.Replace(json, pattern, $"\"$1\":\"{Replacement}\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return json;
    }

    private static bool IsSensitiveKey(string key, IReadOnlyList<string> denyList)
    {
        var lower = key.ToLowerInvariant();
        foreach (var entry in denyList)
        {
            if (lower.Contains(entry, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

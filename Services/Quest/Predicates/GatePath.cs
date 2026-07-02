namespace AZOA.WebAPI.Services.Quest.Predicates;

/// <summary>
/// Single authority for the closed gate-path grammar used by both
/// <see cref="GatePredicateEvaluator"/> and <see cref="QuestConfigBindingResolver"/>.
/// See Services/Quest/AGENTS.md §output-binding for the grammar specification.
/// </summary>
/// <remarks>
/// Grammar (closed — no arithmetic, no function calls):
///   path     := root "." segment ("." segment)*
///   root     := "upstream" | "holon"
///   segment  := identifier | guid-segment
///   guid-segment := letters + digits + hyphens (post-dot GUID-friendly rule from GatePredicateEvaluator)
/// Binding roots in v1: "upstream" and "holon" only. "reads." is GateCheck-local
/// and has no meaning outside a gate (V12).
/// </remarks>
public static class GatePath
{
    /// <summary>Valid root prefixes for v1 binding paths.</summary>
    public static readonly IReadOnlyList<string> ValidRoots = ["upstream", "holon"];

    /// <summary>
    /// Parses a gate path string into its segments. Returns true on success;
    /// on failure sets <paramref name="error"/> to a human-readable message.
    /// </summary>
    /// <param name="path">String like "upstream.nodeName.field" or "holon.&lt;guid&gt;.status".</param>
    /// <param name="segments">On success: [root, seg1, seg2, …]; minimum length 2.</param>
    /// <param name="error">On failure: descriptive parse error.</param>
    public static bool TryParse(string path, out IReadOnlyList<string> segments, out string error)
    {
        segments = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "path must not be empty.";
            return false;
        }

        var parts = path.Split('.');

        if (parts.Length < 2)
        {
            error = $"path '{path}': must have at least two dot-separated segments (root.name).";
            return false;
        }

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                error = $"path '{path}': empty segment (consecutive dots).";
                return false;
            }

            if (!IsValidSegment(part))
            {
                error = $"path '{path}': segment '{part}' contains invalid characters " +
                        "(only letters, digits, '_', and '-' are allowed; segment must not start with '-').";
                return false;
            }
        }

        var root = parts[0];
        if (!ValidRoots.Contains(root))
        {
            error = $"path '{path}': root '{root}' is not valid. Allowed roots: {string.Join(", ", ValidRoots)}.";
            return false;
        }

        segments = parts;
        return true;
    }

    /// <summary>
    /// Returns true when the segment is a valid GUID-shaped string
    /// (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).
    /// </summary>
    public static bool IsGuidShaped(string segment) =>
        Guid.TryParse(segment, out _);

    // A post-dot segment may contain letters, digits, '_', and '-' (GUID-friendly),
    // but must not start with '-' (would be ambiguous with the numeric-minus prefix
    // in the lexer, though that context never applies here).
    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0) return false;
        if (segment[0] == '-') return false;  // leading hyphen disallowed

        foreach (var c in segment)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }
        return true;
    }
}

namespace AZOA.WebAPI.Helpers;

/// <summary>
/// The one username alphabet every writer and validator agrees on, plus the prefixes reserved for
/// node-generated handles. See Helpers/AGENTS.md §Username rules.
/// </summary>
public static class UsernameRules
{
    public const int MinimumLength = 3;
    public const int MaximumLength = 50;

    /// <summary>Letters, digits, underscores and hyphens; generated handles use hyphens.</summary>
    public const string Pattern = "^[a-zA-Z0-9_-]+$";

    public const string PatternMessage =
        "Username must be 3-50 characters long and contain only letters, numbers, underscores, or hyphens.";

    public const string ReservedPrefixMessage =
        "Usernames starting with 'wallet-', 'oauth-' or 'onboard-' are reserved for generated accounts.";

    private static readonly string[] GeneratedHandlePrefixes = ["wallet-", "oauth-", "onboard-"];

    /// <summary>True when the name sits in a namespace only the node itself may mint.</summary>
    public static bool IsReservedGeneratedHandle(string? username)
        => username is not null
           && GeneratedHandlePrefixes.Any(prefix => username.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}

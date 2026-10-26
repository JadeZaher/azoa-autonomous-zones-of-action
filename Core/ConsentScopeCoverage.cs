namespace AZOA.WebAPI.Core;

/// <summary>The single scope-coverage rule for a stored consent grant; see Core/AGENTS.md §consent-scope-coverage.</summary>
public static class ConsentScopeCoverage
{
    /// <summary>Reserved participation-reference prefix of the W3.6 tenant-provisioned implied grant.</summary>
    public const string TenantProvisionedRefPrefix = "tenant-provisioned:v1:";

    /// <summary>Scopes the tenant-provisioned implied grant covers at CHECK time beyond its stored list (W5.1 reserve exchange).</summary>
    public static readonly IReadOnlySet<string> TenantProvisionedImpliedScopes =
        new HashSet<string>(StringComparer.Ordinal) { AzoaScopes.SwapSign };

    /// <summary>True iff the grant is the tenant-provisioned implied participation grant.</summary>
    public static bool IsTenantProvisioned(string? origin, string? participationRef)
        => string.Equals(origin, "Participation", StringComparison.Ordinal)
           && participationRef is { } reference
           && reference.Length > TenantProvisionedRefPrefix.Length
           && reference.StartsWith(TenantProvisionedRefPrefix, StringComparison.Ordinal);

    /// <summary>True iff a grant of this origin/reference with these stored scopes covers <paramref name="scope"/> (liveness is the caller's).</summary>
    public static bool Covers(string? origin, string? participationRef, IEnumerable<string> storedScopes, string? scope)
        => !string.IsNullOrWhiteSpace(scope)
           && (storedScopes.Contains(scope, StringComparer.Ordinal)
               || (TenantProvisionedImpliedScopes.Contains(scope) && IsTenantProvisioned(origin, participationRef)));

    /// <summary>CSV-row overload of <see cref="Covers(string?, string?, IEnumerable{string}, string?)"/>.</summary>
    public static bool Covers(string? origin, string? participationRef, string? storedScopesCsv, string? scope)
        => Covers(origin, participationRef, SplitScopes(storedScopesCsv), scope);

    /// <summary>Splits a stored scope CSV exactly as every grant reader does.</summary>
    public static string[] SplitScopes(string? csv)
        => string.IsNullOrWhiteSpace(csv) ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

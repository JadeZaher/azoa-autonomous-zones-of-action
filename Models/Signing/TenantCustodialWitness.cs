namespace AZOA.WebAPI.Models.Signing;

/// <summary>How a correlated member relates to the tenant: still tenant-linked, or claimed into their own name.</summary>
public enum TenantCustodialKind
{
    /// <summary>Tenant link set; authority is the open participation membership and its implied grant.</summary>
    Custodial,

    /// <summary>Tenant link cleared by a claim; authority is a live UserExplicit grant from the person.</summary>
    Claimed,
}

/// <summary>Current tenant credential and exact member authority captured for one custody access boundary.</summary>
/// <remarks>Membership fields are set only for <see cref="TenantCustodialKind.Custodial"/>; see Providers/Stores/Surreal/AGENTS.md §claimed-custodial-fence.</remarks>
public sealed record TenantCustodialWitness(TenantCustodialKind Kind, SigningConsentWitness Authority, Guid UserId,
    string ExternalUserId, DateTimeOffset? AuthNotBefore, string? MembershipId, long? MembershipVersion, string GrantId,
    long GrantVersion, string GrantScopes, DateTimeOffset? GrantExpiresAt, string GrantCommandId, string GrantReceiptJson);

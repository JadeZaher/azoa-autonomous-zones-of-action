namespace AZOA.WebAPI.Models.Signing;

/// <summary>Current tenant credential and exact child enrollment captured for one custody access boundary.</summary>
public sealed record TenantCustodialWitness(SigningConsentWitness Authority, Guid UserId, string ExternalUserId,
    DateTimeOffset? AuthNotBefore, string MembershipId, long MembershipVersion, string GrantId, long GrantVersion,
    string GrantScopes, DateTimeOffset? GrantExpiresAt, string GrantCommandId, string GrantReceiptJson);

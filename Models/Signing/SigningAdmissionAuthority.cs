using System.Text.Json.Serialization;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Models.Signing;

/// <summary>Authenticated credential identity and immutable authorization ceiling.</summary>
public sealed record SigningCredentialWitness(int Version, string Kind, Guid ActorUserId, Guid? TenantId,
    DateTimeOffset? AuthNotBefore, DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresAt,
    Guid? ApiKeyId, string? ApiKeyIdentityHash, IReadOnlyList<string> Scopes, bool OwnerAuthority,
    IReadOnlyList<string>? StoredApiKeyScopes = null, string? TokenUse = null,
    long? OperatorCredentialRevision = null, long? OperatorSessionRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NodeAdminAuthorityWitness? NodeAdmin = null);

/// <summary>Node-admin session evidence behind operator authority (admin or root revisions); omitted from JSON when absent so legacy witness hashes are unchanged.</summary>
public sealed record NodeAdminAuthorityWitness(string Principal, string SessionRecordId,
    long? AdminRevision, long? SessionRevision, long? RootKeyRevision, long? RootSessionRevision);

/// <summary>One exact selected delegated grant and its unchanged scope/expiry evidence; a set <see cref="ParticipationRef"/>
/// names a tenant-provisioned participation grant (omitted from JSON when null so legacy witness hashes are unchanged).</summary>
public sealed record SigningGrantWitness(Guid Id, string RequiredScope, string StoredScopes,
    DateTimeOffset GrantedAt, DateTimeOffset? ExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParticipationRef = null);

/// <summary>The participant's exact owned wallet binding captured before new economic authorization.</summary>
public sealed record SigningWalletWitness(Guid Id, Guid OwnerUserId, string Address, string ChainType);

/// <summary>Discriminated server-only authority for one direct or frozen workflow effect.</summary>
public sealed record SigningAdmissionAuthority(int Version, string Kind, SigningCredentialWitness Credential,
    SigningWalletWitness Wallet, IReadOnlyList<string> RequiredScopes, IReadOnlyList<SigningGrantWitness> Grants,
    ActionEffectAuthority? Action = null, Guid? WorkflowAuthorizationId = null);

/// <summary>Per-node frozen authorization, selected atomically with economic node admission.</summary>
public sealed record WorkflowSigningWitness(int Version, SigningCredentialWitness Credential, ActionEffectAuthority Action,
    IReadOnlyList<string> RequiredScopes, IReadOnlyList<SigningGrantWitness> Grants, Guid? AuthorizationId = null);

/// <summary>Current credential and exact delegated scopes for an authorized local mutation without custody.</summary>
public sealed record SigningConsentWitness(int Version, SigningCredentialWitness Credential,
    IReadOnlyList<string> RequiredScopes, IReadOnlyList<SigningGrantWitness> Grants);

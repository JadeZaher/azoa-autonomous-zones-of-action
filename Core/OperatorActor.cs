using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Admin;

namespace AZOA.WebAPI.Core;

/// <summary>Who exercised node-operator authority on this request; see <c>Services/NodeAdmin/AGENTS.md</c> §operator-actor.</summary>
public enum OperatorActorKind
{
    Legacy,
    Admin,
    Root,
}

/// <summary>The legacy reserved operator, a named node admin, or break-glass root, acting as the node's operator subject.</summary>
public sealed record OperatorActor
{
    public const string LegacyAuditActor = "legacy-operator";

    private OperatorActor(OperatorActorKind kind, Guid? adminId, string? rootFingerprint)
    {
        Kind = kind;
        AdminId = adminId;
        RootFingerprint = rootFingerprint;
    }

    public static OperatorActor Legacy { get; } = new(OperatorActorKind.Legacy, null, null);

    public static OperatorActor Admin(Guid adminId)
        => adminId == Guid.Empty ? throw new ArgumentException("An admin id is required.", nameof(adminId)) : new(OperatorActorKind.Admin, adminId, null);

    public static OperatorActor Root(string fingerprint)
        => string.IsNullOrWhiteSpace(fingerprint) ? throw new ArgumentException("A root fingerprint is required.", nameof(fingerprint)) : new(OperatorActorKind.Root, null, fingerprint);

    public OperatorActorKind Kind { get; }

    public Guid? AdminId { get; }

    public string? RootFingerprint { get; }

    /// <summary>The user row node-operator data is owned by (wallets, reserve, genesis, reviewer links): always the reserved operator.</summary>
    public Guid SubjectUserId => NodeOperatorIdentity.UserId;

    /// <summary>Audit actor string: <c>legacy-operator</c> | <c>admin:&lt;id&gt;</c> | <c>root:&lt;fingerprint&gt;</c>.</summary>
    public string AuditActor => Kind switch
    {
        OperatorActorKind.Admin => NodeAdminClaims.AdminPrincipal(AdminId!.Value),
        OperatorActorKind.Root => NodeAdminClaims.RootPrincipal(RootFingerprint!),
        _ => LegacyAuditActor,
    };

    public override string ToString() => AuditActor;

    /// <summary>True for a credential witness carrying node-operator authority (legacy operator token or a named admin's session);
    /// a root witness never does (M3 ceiling enforced here, not only by the route map; L-4).</summary>
    public static bool IsOperatorCredential(SigningCredentialWitness credential)
        => credential.TokenUse == AzoaClaims.TokenUseNodeOperator
            || (credential.TokenUse == NodeAdminClaims.TokenUseNodeAdmin && credential.NodeAdmin is { RootKeyRevision: null, AdminRevision: not null });
}

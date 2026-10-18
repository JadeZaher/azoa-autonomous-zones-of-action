using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Wtt;

/// <summary>Workflow node that owns signing for one operation; see Services/Wormhole/AGENTS.md §Workflow authority.</summary>
public sealed record WttActionBinding(string RunId, string NodeId, string IntentHash)
{
    public static WttActionBinding? Of(string? runId, string? nodeId, string? intentHash)
        => runId is null || nodeId is null || intentHash is null ? null : new(runId, nodeId, intentHash);
}

/// <summary>Bridge/Back node request on the <c>wtt</c> action domain; the node identity supplies the request key.</summary>
public sealed record WttActionConfig(WttEndpoint Source, string TokenAddress, bool NativeAsset, string Amount, WttEndpoint Target,
    string RecipientAddress, WttChainBudget SourceBudget, WttChainBudget TargetBudget, string? ReturnOfOperationId = null);

/// <summary>Successor-visible receipt of a settled workflow transfer; Back binds <see cref="OperationId"/>.</summary>
public sealed record WttActionOutput(string OperationId, string SourceChain, string TargetChain, string RecipientAddress,
    string RecipientAmount, string TargetTransactionId, string BodyDigest, string? ReturnOfOperationId);

/// <summary>Owner takeover command: the direct owner continues a tenant-started target phase under their own authority.</summary>
public sealed record WttOwnerTakeoverCommand(long ExpectedVersion);

/// <summary>Direct-owner signing intent for an owner takeover; economics and deployments are unchanged.</summary>
public static class WttOwnerIntent
{
    /// <summary>Same intent with the tenant removed and both payer contexts rebound to the owner's direct credential.</summary>
    public static WttOperationIntent From(WttOperationIntent intent)
        => intent with
        {
            TenantId = null,
            SourceAuthority = intent.SourceAuthority with { Context = Direct(intent.SourceAuthority.Context) },
            TargetAuthority = intent.TargetAuthority with { Context = Direct(intent.TargetAuthority.Context) },
        };

    private static SigningContext Direct(SigningContext context)
        => context with { ActingTenantId = Guid.Empty, GrantorUserId = Guid.Empty };
}

/// <summary>Exposed-participant evidence: <c>Landed</c> carries the participant's exact signed candidate; <c>ExpiredNoEffect</c>
/// carries an absence proof; <c>Pending</c> may carry resumable scan progress.</summary>
public sealed record WttExposedObservation(string Verdict, string? EvidenceJson = null, WttStagePreparation? Landed = null,
    string? Detail = null);

/// <summary>Stage-key keyring health that gates new stage-key admissions (degrade, never block startup).</summary>
public sealed record WttStageKeyReadinessReport(string Status, bool Configured, string? ActiveKeyId,
    IReadOnlyList<string> AvailableKeyVersions, string? UnreadableEnvelopeId, bool RewrapPending, string Detail, DateTimeOffset CheckedAt)
{
    public bool Ready => Status == "Ready";
}

/// <summary>One bounded rewrap pass: envelopes moved to the active key, CAS losers, and whether older versions remain.</summary>
public sealed record WttStageKeyRewrapReport(int Rewrapped, int Conflicted, bool RewrapPending, WttStageKeyReadinessReport Readiness);

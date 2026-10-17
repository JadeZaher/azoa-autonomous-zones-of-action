using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Deployments;

public sealed partial class DeploymentService
{
    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentRecoveryAncestry>> InspectRecoveryAncestryAsync(string operationId, Guid actor,
        Guid? tenant, CancellationToken ct = default)
    {
        var parent = await OwnedAsync(operationId, actor, tenant, ct);
        if (parent?.RecoveryChildId is not { } headId || parent.ParentOperationId is not null)
            return Fail<DeploymentRecoveryAncestry>("An owned original deployment with a recovery child is required.");
        var head = await store.GetAsync(headId, ct);
        if (head is null || head.ParentOperationId != parent.Id || head.ActorUserId != parent.ActorUserId
            || head.TenantId != parent.TenantId || head.RecoveryBindingHash != parent.RecoveryBindingHash)
            return Fail<DeploymentRecoveryAncestry>("Recovery head linkage changed.");
        var audit = await recoveryAudits.ClaimAsync(parent, head, ct);
        if (audit.State == "Running")
            audit = await recoveryAudits.AdvanceAsync(audit.Id, audit.Version, ct)
                ?? await recoveryAudits.GetAsync(audit.Id, ct)
                ?? throw new InvalidOperationException("Recovery ancestry audit disappeared.");
        return AZOAResult<DeploymentRecoveryAncestry>.Success(new(head.Id, audit.State, audit.InspectedCount, audit.Digest, audit.Detail));
    }

    private async Task<NativeRecoveryPredecessor?> AuditedPredecessorAsync(DeploymentOperation parent,
        DeploymentOperation child, CancellationToken ct)
    {
        var audit = await recoveryAudits.ClaimAsync(parent, child, ct);
        return audit.State == "Complete" && audit.Id == NativeRecoveryAncestry.AuditId(parent.Id, child.Id)
            && audit.HeadIntentHash == child.IntentHash && audit.HeadPredecessorId == child.ReplacesOperationId
            ? PredecessorWitness(child) with { AncestryDigest = audit.Digest } : null;
    }

    private async Task<AZOAResult<DeploymentOperation?>> RecoveryPredecessorAsync(DeploymentOperation parent, CancellationToken ct)
    {
        if (parent.RecoveryChildId is null) return AZOAResult<DeploymentOperation?>.Success(null);
        var child = await store.GetAsync(parent.RecoveryChildId, ct);
        if (child is null || child.ParentOperationId != parent.Id || child.ActorUserId != parent.ActorUserId
            || child.TenantId != parent.TenantId || child.RecoveryBindingHash != parent.RecoveryBindingHash
            || child.ArtifactId != parent.ArtifactId || child.ArtifactHash != parent.ArtifactHash
            || NativeRecoveryReplacement.ProofKind(child) is not { } proof)
            return Fail<DeploymentOperation?>("Current recovery child is not eligible for replacement; reconcile every authorized wire first.");
        if (child.ReplacesOperationId is { } previousId)
        {
            var previous = await store.GetAsync(previousId, ct);
            if (previous is null || previous.Id == child.Id || previous.ReplacedByOperationId != child.Id
                || previous.ParentOperationId != parent.Id || previous.ActorUserId != parent.ActorUserId || previous.TenantId != parent.TenantId)
                return Fail<DeploymentOperation?>("Recovery predecessor linkage is inconsistent.");
        }
        if (proof == "ResolvedFailure")
        {
            var request = Read<DeploymentStartRequest>(child.RequestJson);
            var module = Module(request.Target);
            if (module is null || request.NativeIntent is not { Kind: "Recovery" } intent)
                return Fail<DeploymentOperation?>("The original recovery provider or intent is unavailable.");
            foreach (var entry in Read<List<NativeStageHistory>>(child.HistoryJson))
            {
                var observed = await module.ObserveNativeStageAsync(intent, entry.Prepared, ct);
                if (observed.IsError || observed.Result is not { } actual || actual.Verdict != entry.Observation.Verdict
                    || actual.TransactionId != entry.Prepared.TransactionId || actual.ProtocolIdentity != intent.ProtocolIdentity
                    || actual.ResourceEffect is not null || actual.CleanupEffect is not null)
                    return Fail<DeploymentOperation?>("Prior recovery transaction is not authoritatively resolved without a business effect.");
            }
        }
        return AZOAResult<DeploymentOperation?>.Success(child);
    }

    private static NativeRecoveryPredecessor PredecessorWitness(DeploymentOperation child)
        => new(child.Id, child.Version, child.IntentHash,
            CanonicalJson.Hash(Read<List<NativeStageHistory>>(child.HistoryJson)),
            NativeRecoveryReplacement.ProofKind(child) ?? throw new InvalidOperationException("Recovery predecessor is not eligible."));
}

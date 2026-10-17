using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealWttOperationStore
{
    private static readonly string[] OperationStates = ["Admitted", "Active", "Ready", "AwaitingProof", "Settled", "Cancelled", "NeedsOperator"];
    private static readonly string[] OperationPhases = ["Source", "Target", "NativeOutput", "Complete"];

    /// <inheritdoc/>
    public async Task<WttOperation?> UpdateAsync(string operationId, long expectedVersion, WttOperationPatch patch,
        CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        // An envelope recorded without its proof is unverified, so the verified envelope may replace it with that first proof.
        var firstProof = operation?.ProofsJson is null && patch?.ProofsJson is not null;
        if (operation is null || patch is null || operation.Version != expectedVersion
            || operation.State is "Settled" or "Cancelled"
            || patch.State is not null && (!OperationStates.Contains(patch.State) || patch.State is "Active" or "Admitted" or "Cancelled")
            || patch.Phase is not null && !OperationPhases.Contains(patch.Phase)
            || operation.State == "Active" && patch.State is not (null or "NeedsOperator")
            || patch.State == "Settled" && patch.SettlementJson is null && operation.SettlementJson is null
            || patch.SettlementJson is not null && !Canonical(patch.SettlementJson, null)
            || patch.ProofsJson is not null && !Canonical(patch.ProofsJson, null)
            || patch.TargetQuoteJson is not null && !Canonical(patch.TargetQuoteJson, null)
            || patch.TransferPublicationJson is not null && !Canonical(patch.TransferPublicationJson, null)
            || patch.AttestationPublicationJson is not null && !Canonical(patch.AttestationPublicationJson, null)
            || !SetOnce(operation.TransferPublicationJson, patch.TransferPublicationJson)
            || !SetOnce(operation.AttestationPublicationJson, patch.AttestationPublicationJson)
            || !firstProof && !SetOnce(operation.TransferEnvelopeBase64, patch.TransferEnvelopeBase64)
            || !firstProof && !SetOnce(operation.AttestationEnvelopeBase64, patch.AttestationEnvelopeBase64)
            || !SetOnce(operation.ProofsJson, patch.ProofsJson) || !SetOnce(operation.BodyClaimId, patch.BodyClaimId)
            || !SetOnce(operation.TargetQuoteJson, patch.TargetQuoteJson) || !SetOnce(operation.SettlementJson, patch.SettlementJson))
            return null;
        var now = Now;
        var builder = SurrealWriter.UpdateOnly<WttOperation>(operationId).Where(row => row.Version == expectedVersion)
            .Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, now);
        if (patch.State is { } state) builder = builder.Set(row => row.State, state);
        if (patch.Phase is { } phase) builder = builder.Set(row => row.Phase, phase);
        if (patch.Detail is { } detail) builder = builder.Set(row => row.Detail, detail[..Math.Min(detail.Length, 1024)]);
        if (patch.TransferPublicationJson is { } transfer) builder = builder.Set(row => row.TransferPublicationJson, transfer);
        if (patch.AttestationPublicationJson is { } attestation) builder = builder.Set(row => row.AttestationPublicationJson, attestation);
        if (patch.TransferEnvelopeBase64 is { } transferEnvelope) builder = builder.Set(row => row.TransferEnvelopeBase64, transferEnvelope);
        if (patch.AttestationEnvelopeBase64 is { } attestationEnvelope)
            builder = builder.Set(row => row.AttestationEnvelopeBase64, attestationEnvelope);
        if (patch.ProofsJson is { } proofs) builder = builder.Set(row => row.ProofsJson, proofs);
        if (patch.BodyClaimId is { } claim) builder = builder.Set(row => row.BodyClaimId, claim);
        if (patch.TargetQuoteJson is { } targetQuote) builder = builder.Set(row => row.TargetQuoteJson, targetQuote);
        if (patch.SettlementJson is { } settlement) builder = builder.Set(row => row.SettlementJson, settlement);
        if (patch.NextAttemptAt is { } next) builder = builder.Set(row => row.NextAttemptAt, next);
        var query = builder.Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var result = await executor.ExecuteAsync(query, ct);
            result.EnsureAllOk();
            return result;
        }, ct);
        return response.GetValues<WttOperation>(0).SingleOrDefault();
    }

    /// <inheritdoc/>
    public async Task<WttOperation?> ReplaceProofAsync(string operationId, long expectedVersion, string expectedProofsJson,
        string transferEnvelopeBase64, string? attestationEnvelopeBase64, string proofsJson, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        if (operation is null || operation.Version != expectedVersion || operation.Phase != "Target"
            || operation.State is not ("Ready" or "NeedsOperator") || operation.ProofsJson != expectedProofsJson
            || string.IsNullOrWhiteSpace(transferEnvelopeBase64) || !Canonical(proofsJson, null)
            || (operation.AttestationEnvelopeBase64 is null) != (attestationEnvelopeBase64 is null))
            return null;
        // The version pins the stage set: no target attempt may already carry the old signatures on chain.
        if ((await ReadStagesAsync(operationId, ct)).Any(stage => stage.Phase == "Target" && (stage.AuthorizedAt is not null || stage.ExposedAt is not null)))
            return null;
        var now = Now;
        var builder = SurrealWriter.UpdateOnly<WttOperation>(operationId)
            .Where(row => row.Version == expectedVersion && row.ProofsJson == expectedProofsJson && row.Phase == "Target")
            .Set(row => row.TransferEnvelopeBase64, transferEnvelopeBase64).Set(row => row.ProofsJson, proofsJson)
            .Set(row => row.State, "Ready").Set(row => row.Detail, "Guardian proof re-fetched and re-verified for the same canonical body.")
            .Set(row => row.Version, expectedVersion + 1).Set(row => row.UpdatedAt, now).Unset(row => row.TargetQuoteJson);
        if (attestationEnvelopeBase64 is not null) builder = builder.Set(row => row.AttestationEnvelopeBase64, attestationEnvelopeBase64);
        var query = builder.Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var result = await executor.ExecuteAsync(query, ct);
            result.EnsureAllOk();
            return result;
        }, ct);
        return response.GetValues<WttOperation>(0).SingleOrDefault();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WttOperation>> ReadDueAsync(int limit = 50, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) return [];
        var now = Now;
        return await executor.QueryAsync<WttOperation>(SurrealQuery<WttOperation>.From()
            .Where(row => row.NextAttemptAt <= now && (row.State == "Active" || row.State == "AwaitingProof"
                || row.State == "Ready" || row.State == "NeedsOperator"))
            .OrderBy(row => row.NextAttemptAt).ThenBy(row => row.Id).Limit(limit), ct);
    }

    /// <inheritdoc/>
    public async Task<bool> ClaimRecoveryAsync(string operationId, TimeSpan lease, CancellationToken ct = default)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromHours(6)) return false;
        var now = Now;
        var next = now.Add(lease);
        var query = SurrealWriter.UpdateOnly<WttOperation>(operationId)
            .Where(row => row.NextAttemptAt <= now).Set(row => row.NextAttemptAt, next).Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var result = await executor.ExecuteAsync(query, ct);
            result.EnsureAllOk();
            return result;
        }, ct);
        return response.GetValues<WttOperation>(0).Count == 1;
    }

    private static bool SetOnce(string? current, string? proposed) => proposed is null || current is null || current == proposed;
}

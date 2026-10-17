using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealEthereumDexAllowanceStore(ISurrealExecutor executor) : IEthereumDexAllowanceStore
{
    private const string Conflict = "DEX_ALLOWANCE_CONFLICT";

    /// <inheritdoc/>
    public Task<EthereumDexAllowance?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<EthereumDexAllowance>(SurrealQuery<EthereumDexAllowance>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<EthereumDexAllowance?> ClaimAsync(EthereumDexAllowance candidate, CancellationToken ct = default)
    {
        if (!Valid(candidate)) return null;
        var prior = await GetAsync(candidate.Id, ct);
        if (prior is not null && !SameResource(prior, candidate)) return null;
        var sameOwner = prior?.OperationId == candidate.OperationId && prior.IntentHash == candidate.IntentHash;
        if (prior?.OperationId == candidate.OperationId && !sameOwner) return null;
        // raw: operation admission, prior no-egress cancellation and exclusive allowance ownership are atomic.
        var statements = new List<SurrealQuery>
        {
            SurrealQuery.Of("BEGIN"),
            SurrealQuery.Of("""
                LET $_allowance_operation = (UPDATE ONLY type::record('swap_operation', $_allowance_operation_id)
                    SET updated_at = updated_at WHERE intent_hash = $_allowance_intent
                        AND state != 'Cancelled' RETURN AFTER)
                """).WithParam("_allowance_operation_id", candidate.OperationId).WithParam("_allowance_intent", candidate.IntentHash),
            SurrealQuery.Of("IF $_allowance_operation = NONE { THROW 'DEX_ALLOWANCE_CONFLICT' }")
        };
        if (prior is not null && !sameOwner && prior.State != "Released")
        {
            statements.Add(SurrealQuery.Of("""
                LET $_allowance_old = (UPDATE ONLY type::record('swap_operation', $_allowance_old_id)
                    SET updated_at = updated_at WHERE state = 'Cancelled' AND intent_hash = $_allowance_old_intent RETURN AFTER)
                """).WithParam("_allowance_old_id", prior.OperationId).WithParam("_allowance_old_intent", prior.IntentHash));
            statements.Add(SurrealQuery.Of("IF $_allowance_old = NONE { THROW 'DEX_ALLOWANCE_CONFLICT' }"));
            statements.Add(SurrealQuery.Of("""
                LET $_allowance_egress = (SELECT VALUE id FROM swap_stage WHERE operation_id = $_allowance_old_id
                    AND (authorized_at != NONE OR external_exposed_at != NONE) LIMIT 1)
                """).WithParam("_allowance_old_id", prior.OperationId));
            statements.Add(SurrealQuery.Of("IF array::len($_allowance_egress) != 0 { THROW 'DEX_ALLOWANCE_CONFLICT' }"));
        }
        SurrealQuery mutation;
        if (prior is null)
        {
            var saved = JsonSerializer.Deserialize<EthereumDexAllowance>(JsonSerializer.Serialize(candidate))!;
            saved.CreatedAt = saved.UpdatedAt = DateTimeOffset.UtcNow;
            mutation = SurrealWriter.Create(saved);
        }
        else
        {
            if (sameOwner && prior.State != "Held") return null;
            mutation = SurrealWriter.UpdateOnly<EthereumDexAllowance>(candidate.Id)
                .Where(row => row.Version == prior.Version && row.OperationId == prior.OperationId && row.IntentHash == prior.IntentHash)
                .Set(row => row.OperationId, candidate.OperationId).Set(row => row.IntentHash, candidate.IntentHash)
                .Set(row => row.State, "Held").Set(row => row.Version, prior.Version + 1)
                .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow)
                .Unset(row => row.ReleaseStageId).Unset(row => row.ReleaseEvidenceJson).Build();
        }
        var resultIndex = statements.Count;
        statements.Add(SurrealGeneratedStatementScope.Apply(mutation, "allowance_claim"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            var response = await ExecuteAsync(query, ct);
            return response.GetValues<EthereumDexAllowance>(resultIndex).SingleOrDefault();
        }
        catch (SurrealStatementException exception) when (ExpectedConflict(exception))
        {
            var winner = await GetAsync(candidate.Id, ct);
            return winner is { State: "Held" } && SameResource(winner, candidate) &&
                winner.OperationId == candidate.OperationId && winner.IntentHash == candidate.IntentHash ? winner : null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ReleaseAsync(string id, string operationId, string stageId, CancellationToken ct = default)
    {
        var row = await GetAsync(id, ct);
        if (row is null || row.OperationId != operationId) return false;
        if (row.State == "Released") return row.ReleaseStageId == stageId;
        var stage = await executor.QuerySingleAsync<SwapStageRecord>(SurrealQuery<SwapStageRecord>.Key(stageId), ct);
        if (stage is not { State: "Confirmed", Stage: "Swap" or "RevokeAllowance", ObservationJson: not null } ||
            stage.OperationId != operationId || stage.IntentHash != row.IntentHash || stage.PreparedJson is null ||
            stage.AuthorizedAt is null && stage.ExternalExposedAt is null) return false;
        SwapStageObservation? observation;
        try { observation = JsonSerializer.Deserialize<SwapStageObservation>(stage.ObservationJson); }
        catch (JsonException) { return false; }
        if (observation is not { Verdict: "Confirmed", LockedBalance: "0", EvidenceJson: not null } || !BindsRelease(row, stage, observation)) return false;
        // raw: immutable terminal stage evidence and allowance release share one commit.
        var guard = SurrealQuery.Of("""
            LET $_allowance_proof = (UPDATE ONLY type::record('swap_stage', $_allowance_stage)
                SET updated_at = updated_at WHERE version = $_allowance_stage_version AND state = 'Confirmed'
                    AND operation_id = $_allowance_operation AND intent_hash = $_allowance_intent
                    AND observation_json = $_allowance_evidence RETURN AFTER)
            """).WithParam("_allowance_stage", stageId).WithParam("_allowance_stage_version", stage.Version)
            .WithParam("_allowance_operation", operationId).WithParam("_allowance_intent", row.IntentHash)
            .WithParam("_allowance_evidence", stage.ObservationJson);
        var mutation = SurrealWriter.UpdateOnly<EthereumDexAllowance>(id)
            .Where(item => item.Version == row.Version && item.State == "Held" && item.OperationId == operationId)
            .Set(item => item.State, "Released").Set(item => item.ReleaseStageId, stageId)
            .Set(item => item.ReleaseEvidenceJson, stage.ObservationJson).Set(item => item.Version, row.Version + 1)
            .Set(item => item.UpdatedAt, DateTimeOffset.UtcNow).Build();
        var query = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"), guard,
            SurrealQuery.Of("IF $_allowance_proof = NONE { THROW 'DEX_ALLOWANCE_CONFLICT' }"),
            SurrealGeneratedStatementScope.Apply(mutation, "allowance_release"), SurrealQuery.Of("COMMIT"));
        try { return (await ExecuteAsync(query, ct)).GetValues<EthereumDexAllowance>(3).Any(); }
        catch (SurrealStatementException exception) when (ExpectedConflict(exception)) { return false; }
    }

    private Task<SurrealResponse> ExecuteAsync(SurrealQuery query, CancellationToken ct)
        => SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return response;
        }, ct);

    private static bool ExpectedConflict(SurrealStatementException exception)
        => SurrealIdempotencyLedger.IsUniqueViolation(exception.Message) || exception.Message.Contains(Conflict, StringComparison.Ordinal);

    private static bool SameResource(EthereumDexAllowance left, EthereumDexAllowance right)
        => left.ProtocolIdentity == right.ProtocolIdentity && left.OwnerAddress == right.OwnerAddress &&
            left.TokenAddress == right.TokenAddress && left.ExecutorAddress == right.ExecutorAddress;

    private static bool BindsRelease(EthereumDexAllowance row, SwapStageRecord stage, SwapStageObservation observation)
    {
        try
        {
            using var evidence = JsonDocument.Parse(observation.EvidenceJson!);
            if (!evidence.RootElement.TryGetProperty("AllowanceRelease", out var member)) return false;
            var release = member.Deserialize<EthereumDexAllowanceReleaseEvidence>();
            return release is { Version: 1, AllowanceAfter: "0" } && release.Effect == stage.Stage &&
                release.ProtocolIdentity == row.ProtocolIdentity && release.Owner == row.OwnerAddress &&
                release.Token == row.TokenAddress && release.Executor == row.ExecutorAddress &&
                release.BlockHash is { Length: 66 } && EthereumHex.TryParseData(release.BlockHash, out var hash) && hash.Length == 32 &&
                UnsignedIntegerText.TryParse(release.BlockNumber, out _) &&
                observation.TransactionIds is { Count: 1 } && observation.TransactionIds[0] == release.TransactionId &&
                release.TransactionId is { Length: 66 } && EthereumHex.TryParseData(release.TransactionId, out var transaction) && transaction.Length == 32;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    private static bool Valid(EthereumDexAllowance candidate)
        => candidate is { State: "Held", Version: 0, ReleaseStageId: null, ReleaseEvidenceJson: null } &&
            EthereumDexAllowanceIdentity.IsCanonical(candidate.ProtocolIdentity, candidate.OwnerAddress, candidate.TokenAddress, candidate.ExecutorAddress) &&
            !string.IsNullOrWhiteSpace(candidate.OperationId) && candidate.IntentHash is { Length: 64 } &&
            candidate.Id == EthereumDexAllowanceIdentity.Create(candidate.ProtocolIdentity, candidate.OwnerAddress, candidate.TokenAddress, candidate.ExecutorAddress);
}

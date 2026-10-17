using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealFungibleCreationStore
{
    /// <inheritdoc/>
    public async Task<FungibleCreationOperation?> CancelAsync(string id, FungibleContinuationRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (request.RequestId == Guid.Empty || request.Version < 0) return null;
        var row = await GetAsync(id, ct);
        if (row is null || row.ActorUserId != credential.ActorUserId.ToString("N") || row.TenantId != credential.TenantId?.ToString("N")) return null;
        if (JsonSerializer.Deserialize<FungibleCreationCommand>(row.RequestJson)?.GenesisAuthority is not null) return null;
        var identity = new { Domain = "fungible-cancellation-v1", Operation = id, Actor = row.ActorUserId, Tenant = row.TenantId, request.RequestId };
        var receiptId = CanonicalJson.Hash(identity);
        var requestHash = CanonicalJson.Hash(new { Identity = identity, request.Version });
        var prior = await executor.QuerySingleAsync<FungibleCreationCancellation>(SurrealQuery<FungibleCreationCancellation>.Key(receiptId), ct);
        if (prior is not null) return prior.RequestHash == requestHash && row.State == "Cancelled" ? row : null;
        if (row.Version != request.Version || row.State is not ("AwaitingConsent" or "Admitted" or "Preparing" or "Prepared" or "FailedNoEffect")) return null;
        if (row.State == "FailedNoEffect" && !HasNoEffectProof(row)) return null;
        var fence = SigningAuthorityFence.ForRunAdmission(credential, "fungible_cancel");
        if (fence is null) return null;
        var receipt = new FungibleCreationCancellation { Id = receiptId, OperationId = id, ActorId = row.ActorUserId, TenantId = row.TenantId,
            RequestHash = requestHash, CredentialJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(credential)),
            ExpectedVersion = row.Version, Generation = row.Generation, CreatedAt = DateTimeOffset.UtcNow };
        // raw: exact no-effect cancellation, current original actor and immutable receipt serialize against first-send CAS.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fence);
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<FungibleCreationOperation>(id)
            .Where(current => current.Version == row.Version && current.State == row.State && current.RequestHash == row.RequestHash
                && current.PreparedJson == row.PreparedJson && current.ObservationJson == row.ObservationJson
                && current.Generation == row.Generation && current.HistoryJson == row.HistoryJson)
            .Set(current => current.State, "Cancelled").Set(current => current.Version, row.Version + 1)
            .Set(current => current.Detail, "Cancelled before an effective chain creation; retained candidates cannot be submitted.")
            .Set(current => current.UpdatedAt, DateTimeOffset.UtcNow).Build(), "fungible_cancel_operation", "_fungible_cancel_result"));
        statements.Add(SurrealQuery.Of("IF $_fungible_cancel_result = NONE { THROW 'FUNGIBLE_CREATION_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(receipt), "fungible_cancel_receipt"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var transaction = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(transaction, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return await GetAsync(id, ct);
        }
        catch (SurrealStatementException exception) when (IsConflict(exception))
        {
            prior = await executor.QuerySingleAsync<FungibleCreationCancellation>(SurrealQuery<FungibleCreationCancellation>.Key(receiptId), ct);
            return prior?.RequestHash == requestHash ? await GetAsync(id, ct) : null;
        }
    }

    private static bool HasNoEffectProof(FungibleCreationOperation row)
    {
        var prepared = row.PreparedJson is null ? null : JsonSerializer.Deserialize<PreparedFungibleCreation>(row.PreparedJson);
        var proof = row.ObservationJson is null ? null : JsonSerializer.Deserialize<FungibleCreationObservation>(row.ObservationJson);
        return prepared is not null && proof is { Verdict: "ExpiredAbsent" } && !string.IsNullOrWhiteSpace(proof.EvidenceJson) && proof.TransactionId == prepared.TransactionId
            && proof.ProtocolIdentity == prepared.ProtocolIdentity && CanonicalJson.Hash(prepared.Intent) == CanonicalJson.Hash(JsonSerializer.Deserialize<FungibleCreationIntent>(row.IntentJson)!);
    }
}

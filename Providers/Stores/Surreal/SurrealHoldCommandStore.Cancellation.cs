using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public async Task<HoldCommand?> CancelRailAsync(string id, HoldRecoveryRequest request, SigningCredentialWitness credential,
        CancellationToken ct = default)
    {
        if (request.RequestId == Guid.Empty || request.Version < 0 || string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Length > 500 || credential.TenantId is not null || !credential.OwnerAuthority) return null;
        var receiptId = HoldRecoveryIdentity.Id("Cancel", id, credential.ActorUserId, null, request.RequestId);
        var requestHash = HoldRecoveryIdentity.RequestHash("Cancel", id, credential.ActorUserId, null, request);
        var prior = await executor.QuerySingleAsync<HoldRecoveryReceipt>(SurrealQuery<HoldRecoveryReceipt>.Key(receiptId), ct);
        if (prior is not null) return prior.Kind == "Cancel" && prior.RequestHash == requestHash
            ? JsonSerializer.Deserialize<HoldCommand>(prior.OutcomeJson) : null;
        var expected = await GetCommandAsync(id, ct);
        if (expected is null || expected.ActorUserId != credential.ActorUserId.ToString("N") || expected.Version != request.Version
            || expected.Version == long.MaxValue || expected.RailPlanJson is null
            || expected.State is not ("AwaitingConsent" or "Admitted" or "Preparing" or "Prepared" or "FailedNoEffect")
            || expected.AcceptanceState == "Submitted") return null;
        if (expected.State == "FailedNoEffect")
        {
            if (expected.PreparedJson is null || expected.ObservationJson is null) return null;
            var proof = JsonSerializer.Deserialize<HoldTransferEvidence>(expected.ObservationJson)!;
            var wire = JsonSerializer.Deserialize<PreparedValueTransfer>(expected.PreparedJson)!;
            if (proof.Version != 1 || proof.Confirmation != ChainConfirmation.FailedOnChain
                || proof.PreparedHash != CanonicalJson.HashCanonical(expected.PreparedJson)
                || proof.TransactionId != wire.TransactionId || proof.ProtocolIdentity != wire.ProtocolIdentity) return null;
        }
        var guards = SigningAuthorityFence.ForRunAdmission(credential, "hold_cancel_owner");
        if (guards is null) return null;
        var aggregate = await GetHoldAsync(expected.HoldRecordId, ct);
        if (aggregate is null || aggregate.ActiveCommandId != expected.Id || aggregate.Version is < 0 or long.MaxValue) return null;
        var plan = JsonSerializer.Deserialize<HoldRailPlan>(expected.RailPlanJson)!;
        if (aggregate.State != (plan.Kind == "Funding" ? "Pending" : "Held")) return null;
        var now = DateTimeOffset.UtcNow;
        var outcome = JsonSerializer.Deserialize<HoldCommand>(JsonSerializer.Serialize(expected))!;
        outcome.State = "Cancelled"; outcome.Version++; outcome.UpdatedAt = now;
        outcome.Detail = expected.AcceptanceState == "Confirmed"
            ? "Transfer cancelled. The already confirmed recipient acceptance and its network costs remain recorded."
            : "Transfer cancelled without a finalized value movement.";
        var receipt = new HoldRecoveryReceipt { Id = receiptId, OperationId = id, Kind = "Cancel",
            ActorUserId = expected.ActorUserId, RequestHash = requestHash,
            AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(credential)),
            OriginalState = expected.State, OriginalVersion = expected.Version,
            OutcomeJson = JsonSerializer.Serialize(outcome), CreatedAt = now };
        // raw: no-effect command cancellation, active liability release and immutable owner audit serialize against first egress.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(aggregate.Id)
            .Where(row => row.Version == aggregate.Version && row.ActiveCommandId == id && row.State == aggregate.State
                && row.DefinitionHash == aggregate.DefinitionHash && row.DefinitionJson == aggregate.DefinitionJson)
            .Set(row => row.Version, aggregate.Version + 1).Set(row => row.State, plan.Kind == "Funding" ? "Cancelled" : "Held")
            .Unset(row => row.ActiveCommandId).Set(row => row.UpdatedAt, now).Build(), "hold_cancel_liability", "_hold_cancel_liability"));
        statements.Add(SurrealQuery.Of("IF $_hold_cancel_liability = NONE OR $_hold_cancel_liability = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldCommand>(id)
            .Where(row => row.Version == expected.Version && row.State == expected.State && row.IntentHash == expected.IntentHash
                && row.PreparedJson == expected.PreparedJson && row.ObservationJson == expected.ObservationJson
                && row.AcceptanceState == expected.AcceptanceState && row.AcceptancePreparedJson == expected.AcceptancePreparedJson)
            .Set(row => row.State, "Cancelled").Set(row => row.Version, outcome.Version)
            .Set(row => row.Detail, outcome.Detail).Set(row => row.UpdatedAt, now).Build(), "hold_cancel_command", "_hold_cancel_command"));
        statements.Add(SurrealQuery.Of("IF $_hold_cancel_command = NONE OR $_hold_cancel_command = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(receipt), "hold_cancel_audit"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk(); return response;
            }, ct);
            return outcome;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            prior = await executor.QuerySingleAsync<HoldRecoveryReceipt>(SurrealQuery<HoldRecoveryReceipt>.Key(receiptId), ct);
            return prior is not null && prior.Kind == "Cancel" && prior.RequestHash == requestHash
                ? JsonSerializer.Deserialize<HoldCommand>(prior.OutcomeJson) : null;
        }
    }
}

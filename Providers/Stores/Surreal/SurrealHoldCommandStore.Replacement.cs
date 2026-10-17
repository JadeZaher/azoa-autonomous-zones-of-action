using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public async Task<HoldCommand?> ReplaceRailAsync(HoldCommand expected, HoldRailPlan next, HoldCommandAuthority authority,
        HoldRecoveryRequest request, CancellationToken ct = default)
    {
        if (request.RequestId == Guid.Empty || request.Version < 0 || string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Length > 500 || expected.RailPlanJson is null) return null;
        var intent = JsonSerializer.Deserialize<HoldCommandIntent>(expected.IntentJson)!;
        var receiptId = HoldRecoveryIdentity.Id("Replace", expected.Id, intent.ActorUserId, intent.TenantId, request.RequestId);
        var requestHash = HoldRecoveryIdentity.RequestHash("Replace", expected.Id, intent.ActorUserId, intent.TenantId, request);
        var prior = await executor.QuerySingleAsync<HoldRecoveryReceipt>(SurrealQuery<HoldRecoveryReceipt>.Key(receiptId), ct);
        if (prior is not null) return prior.Kind == "Replace" && prior.RequestHash == requestHash
            ? JsonSerializer.Deserialize<HoldCommand>(prior.OutcomeJson) : null;
        var replaceAcceptance = expected.State == "Admitted" && expected.AcceptanceState == "FailedNoEffect";
        if (expected.Version != request.Version || expected.Version == long.MaxValue || expected.Generation == long.MaxValue
            || (expected.State is not ("FailedNoEffect" or "AwaitingConsent") && !replaceAcceptance)
            || expected.AcceptanceState is not (null or "Confirmed" or "FailedNoEffect")) return null;
        var original = JsonSerializer.Deserialize<HoldRailPlan>(expected.RailPlanJson)!;
        if (expected.State == "FailedNoEffect")
        {
            if (expected.PreparedJson is null || expected.ObservationJson is null) return null;
            var proof = JsonSerializer.Deserialize<HoldTransferEvidence>(expected.ObservationJson)!;
            var wire = JsonSerializer.Deserialize<PreparedValueTransfer>(expected.PreparedJson)!;
            if (proof.Version != 1 || proof.Confirmation != ChainConfirmation.FailedOnChain
                || proof.PreparedHash != CanonicalJson.HashCanonical(expected.PreparedJson)
                || proof.TransactionId != wire.TransactionId || proof.ProtocolIdentity != wire.ProtocolIdentity) return null;
        }
        else if (expected.PreparedJson is not null || (!replaceAcceptance && expected.StageIntentJson is not null)
            || (!replaceAcceptance && expected.AcceptedAt is not null)) return null;
        if (expected.AcceptanceState == "FailedNoEffect")
        {
            if (expected.AcceptancePreparedJson is null || expected.AcceptanceEvidenceJson is null) return null;
            var proof = JsonSerializer.Deserialize<HoldTransferEvidence>(expected.AcceptanceEvidenceJson)!;
            var wire = JsonSerializer.Deserialize<PreparedValueAcceptance>(expected.AcceptancePreparedJson)!;
            if (proof.Version != 1 || proof.Confirmation != ChainConfirmation.FailedOnChain
                || proof.PreparedHash != CanonicalJson.HashCanonical(expected.AcceptancePreparedJson)
                || proof.TransactionId != wire.TransactionId || proof.ProtocolIdentity != wire.ProtocolIdentity) return null;
        }
        if (next.Version != 1 || next.CommandId != expected.Id || next.Generation != expected.Generation + 1
            || next.Kind != original.Kind || next.SenderWallet != original.SenderWallet || next.RecipientWallet != original.RecipientWallet
            || next.SenderSigningContext != original.SenderSigningContext || !ValueCustodyIdentity.ValidQuote(next.Quote)
            || next.Quote.Request != original.Quote.Request || next.Quote.ProtocolIdentity != original.Quote.ProtocolIdentity
            || next.Quote.AssetAuthorityHash != original.Quote.AssetAuthorityHash) return null;
        if (authority.ReleaseMandateId is not null
            || JsonSerializer.Deserialize<HoldCommandAuthority>(expected.AuthorityJson)?.ReleaseMandateId is not null) return null;
        var guards = await RailAuthorityAsync(intent, next, authority, ct);
        if (guards is null) return null;
        var history = JsonSerializer.Deserialize<List<JsonElement>>(expected.TransactionHistoryJson)!;
        history.Add(JsonSerializer.SerializeToElement(new { expected.Generation, expected.RailPlanJson, expected.StageIntentJson,
            expected.PreparedJson, expected.ObservationJson, expected.CurrentAuthorizationId, expected.CurrentAuthorityJson,
            expected.AcceptanceIntentJson, expected.AcceptancePreparedJson, expected.AcceptanceEvidenceJson,
            expected.AcceptanceAuthorityJson, expected.CurrentAcceptanceAuthorityJson, expected.CurrentAcceptanceAuthorizationId,
            expected.AcceptanceRequestId, expected.AcceptanceRequestHash, expected.AcceptanceState }));
        var now = DateTimeOffset.UtcNow;
        var outcome = JsonSerializer.Deserialize<HoldCommand>(JsonSerializer.Serialize(expected))!;
        outcome.Version++; outcome.Generation++; outcome.State = "AwaitingConsent"; outcome.UpdatedAt = now;
        outcome.TransactionHistoryJson = JsonSerializer.Serialize(history);
        outcome.RailPlanJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(next));
        outcome.PreparedJson = null; outcome.ObservationJson = null; outcome.StageIntentJson = null;
        outcome.AcceptedAt = null; outcome.AcceptedQuoteId = null; outcome.CurrentAuthorizationId = null; outcome.CurrentAuthorityJson = null;
        if (expected.AcceptanceState == "FailedNoEffect")
        {
            outcome.AcceptanceState = null; outcome.AcceptanceIntentJson = null; outcome.AcceptancePreparedJson = null;
            outcome.AcceptanceEvidenceJson = null; outcome.AcceptanceAuthorityJson = null;
            outcome.CurrentAcceptanceAuthorityJson = null; outcome.CurrentAcceptanceAuthorizationId = null;
            outcome.AcceptanceRequestId = null; outcome.AcceptanceRequestHash = null;
        }
        outcome.Detail = "Prior no-effect evidence is retained. Review and accept the replacement quote before a new transfer can be prepared.";
        var receipt = new HoldRecoveryReceipt { Id = receiptId, OperationId = expected.Id, Kind = "Replace",
            ActorUserId = expected.ActorUserId, TenantId = expected.TenantId, RequestHash = requestHash,
            AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority)),
            OriginalState = expected.State, OriginalVersion = expected.Version, OutcomeJson = JsonSerializer.Serialize(outcome), CreatedAt = now };
        // raw: original no-effect generation, new quote and immutable recovery audit rotate atomically under current authority.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.AddRange(OriginalWorkflowFence(expected));
        statements.Add(SurrealQuery.Of("IF $_hold_replace_expiry <= time::now() { THROW 'HOLD_COMMAND_CHANGED' }")
            .WithParam("_hold_replace_expiry", next.Quote.ExpiresAt));
        var update = SurrealWriter.UpdateOnly<HoldCommand>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == expected.State && row.IntentHash == expected.IntentHash
                && row.PreparedJson == expected.PreparedJson && row.ObservationJson == expected.ObservationJson
                && row.Generation == expected.Generation && row.TransactionHistoryJson == expected.TransactionHistoryJson
                && row.StageIntentJson == expected.StageIntentJson && row.CurrentAuthorizationId == expected.CurrentAuthorizationId
                && row.AcceptanceState == expected.AcceptanceState && row.AcceptancePreparedJson == expected.AcceptancePreparedJson
                && row.AcceptanceEvidenceJson == expected.AcceptanceEvidenceJson)
            .Set(row => row.Version, outcome.Version).Set(row => row.Generation, outcome.Generation)
            .Set(row => row.State, outcome.State).Set(row => row.RailPlanJson, outcome.RailPlanJson)
            .Set(row => row.TransactionHistoryJson, outcome.TransactionHistoryJson).Set(row => row.Detail, outcome.Detail)
            .Set(row => row.UpdatedAt, now).Unset(row => row.PreparedJson).Unset(row => row.ObservationJson)
            .Unset(row => row.StageIntentJson).Unset(row => row.AcceptedAt).Unset(row => row.AcceptedQuoteId)
            .Unset(row => row.CurrentAuthorizationId).Unset(row => row.CurrentAuthorityJson);
        if (expected.AcceptanceState == "FailedNoEffect")
            update = update.Unset(row => row.AcceptanceState).Unset(row => row.AcceptanceIntentJson)
                .Unset(row => row.AcceptancePreparedJson).Unset(row => row.AcceptanceEvidenceJson)
                .Unset(row => row.AcceptanceAuthorityJson).Unset(row => row.CurrentAcceptanceAuthorityJson)
                .Unset(row => row.CurrentAcceptanceAuthorizationId).Unset(row => row.AcceptanceRequestId).Unset(row => row.AcceptanceRequestHash);
        statements.Add(SurrealGeneratedStatementScope.Assign(update.Build(), "hold_replace", "_hold_replace"));
        statements.Add(SurrealQuery.Of("IF $_hold_replace = NONE OR $_hold_replace = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(receipt), "hold_replace_audit"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct); response.EnsureAllOk(); return response;
            }, ct);
            return outcome;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal))
        {
            prior = await executor.QuerySingleAsync<HoldRecoveryReceipt>(SurrealQuery<HoldRecoveryReceipt>.Key(receiptId), ct);
            return prior is not null && prior.Kind == "Replace" && prior.RequestHash == requestHash
                ? JsonSerializer.Deserialize<HoldCommand>(prior.OutcomeJson) : null;
        }
    }
}

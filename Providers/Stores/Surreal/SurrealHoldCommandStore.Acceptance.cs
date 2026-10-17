using System.Text.Json;
using AZOA.WebAPI.Core;
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
    public async Task<HoldCommand?> AdmitAcceptanceAsync(HoldCommand expected, ValueAcceptanceIntent intent,
        SigningAdmissionAuthority authority, HoldContinuationRequest request, CancellationToken ct = default)
    {
        if (expected.RailPlanJson is null || expected.AcceptedAt is null || expected.Version is < 0 or long.MaxValue
            || expected.State is not ("Admitted" or "Preparing") || expected.PreparedJson is not null
            || request.RequestId == Guid.Empty || request.QuoteId != intent.Cost.Quote.QuoteId
            || request.MaximumNetworkFeeBaseUnits != intent.Cost.MaximumNetworkFeeBaseUnits) return null;
        var plan = JsonSerializer.Deserialize<HoldRailPlan>(expected.RailPlanJson)!;
        if (plan.RecipientWallet is not { } recipient || intent.Recipient != recipient.Address
            || authority.Wallet != recipient || authority.Credential.ActorUserId != recipient.OwnerUserId
            || intent.SigningContext.IsPlatform || intent.SigningContext.UserId != recipient.OwnerUserId
            || intent.SigningContext.WalletId != recipient.Id || intent.SigningContext.Scope != AzoaScopes.TransferSign
            || intent.SigningContext.ActingTenantId != (authority.Credential.TenantId ?? Guid.Empty)
            || (authority.Credential.TenantId is not null && intent.SigningContext.GrantorUserId != recipient.OwnerUserId)
            || intent.SigningContext.ChainType != plan.Quote.Request.ChainType
            || intent.PreparationKey != $"hold:{expected.Id}:Acceptance:{expected.Generation}"
            || !ValueCustodyIdentity.MatchesAcceptance(intent) || intent.Cost.CommandId != expected.Id
            || intent.Cost.Generation != expected.Generation || intent.Cost.Stage != "Acceptance"
            || intent.Cost.Quote != plan.Quote) return null;
        var guards = SigningAuthorityFence.Create(authority, [AzoaScopes.TransferSign], "hold_acceptance_authority");
        if (guards is null) return null;
        var intentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent));
        var authorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority));
        var requestHash = HoldConsentIdentity.RequestHash("Acceptance", expected.Id, authority.Credential.ActorUserId, authority.Credential.TenantId, request);
        var auditId = HoldConsentIdentity.Id("Acceptance", expected.Id, authority.Credential.ActorUserId, authority.Credential.TenantId, request.RequestId);
        var prior = await executor.QuerySingleAsync<HoldCommandAuthorization>(SurrealQuery<HoldCommandAuthorization>.Key(auditId), ct);
        if (prior is not null) return prior.Kind == "Acceptance" && prior.OperationId == expected.Id
            && prior.RequestHash == requestHash ? expected : null;
        if (expected.AcceptanceIntentJson is not null && expected.AcceptanceIntentJson != intentJson) return null;
        if (request.Version != expected.Version) return null;
        if (expected.AcceptanceState is not (null or "Admitted" or "Preparing" or "Prepared")) return null;
        var audit = new HoldCommandAuthorization { Id = auditId, OperationId = expected.Id, Kind = "Acceptance",
            ActorUserId = authority.Credential.ActorUserId.ToString("N"), TenantId = authority.Credential.TenantId?.ToString("N"),
            ExpectedVersion = request.Version, RequestHash = requestHash, QuoteId = request.QuoteId,
            MaximumNetworkFeeBaseUnits = request.MaximumNetworkFeeBaseUnits, AuthorityJson = authorityJson,
            PreparedHash = expected.AcceptancePreparedJson is null ? null : CanonicalJson.HashCanonical(expected.AcceptancePreparedJson),
            CreatedAt = DateTimeOffset.UtcNow };
        // raw: actual recipient consent and original active command commit together before any participant custody call.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.AddRange(OriginalWorkflowFence(expected));
        var write = SurrealWriter.UpdateOnly<HoldCommand>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == expected.State && row.IntentHash == expected.IntentHash
                && row.RailPlanJson == expected.RailPlanJson && row.PreparedJson == null
                && row.AcceptanceIntentJson == expected.AcceptanceIntentJson && row.AcceptancePreparedJson == expected.AcceptancePreparedJson)
            .Set(row => row.Version, expected.Version + 1).Set(row => row.CurrentAcceptanceAuthorityJson, authorityJson)
            .Set(row => row.CurrentAcceptanceAuthorizationId, auditId).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow);
        if (expected.AcceptanceIntentJson is null)
            write = write.Set(row => row.AcceptanceIntentJson, intentJson).Set(row => row.AcceptanceRequestId, request.RequestId.ToString("N"))
                .Set(row => row.AcceptanceRequestHash, requestHash).Set(row => row.AcceptanceAuthorityJson, authorityJson)
                .Set(row => row.AcceptanceState, "Admitted");
        statements.Add(SurrealGeneratedStatementScope.Assign(write.Build(), "hold_acceptance_admit", "_hold_acceptance_admit"));
        statements.Add(SurrealQuery.Of("IF $_hold_acceptance_admit = NONE OR $_hold_acceptance_admit = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "hold_acceptance_consent"));
        return await CommitAcceptanceAsync(statements, expected.Id, ct);
    }

    /// <inheritdoc/>
    public async Task<HoldCommand?> AdvanceAcceptanceAsync(HoldCommand expected, string target,
        PreparedValueAcceptance? prepared = null, HoldTransferEvidence? evidence = null, CancellationToken ct = default)
    {
        var allowed = (expected.AcceptanceState, target) switch
        {
            ("Admitted" or "Preparing", "Preparing") => true,
            ("Preparing", "Prepared") => prepared is not null,
            ("Prepared", "Submitted") => true,
            ("Submitted", "Confirmed") => evidence is not null,
            ("Submitted", "FailedNoEffect") => evidence is not null,
            _ => false,
        };
        if (!allowed || expected.Version is < 0 or long.MaxValue || expected.AcceptanceIntentJson is null
            || expected.CurrentAcceptanceAuthorityJson is null || expected.State is not ("Admitted" or "Preparing")) return null;
        var savedIntent = JsonSerializer.Deserialize<ValueAcceptanceIntent>(expected.AcceptanceIntentJson)!;
        var authority = JsonSerializer.Deserialize<SigningAdmissionAuthority>(expected.CurrentAcceptanceAuthorityJson)!;
        var wireJson = prepared is null ? expected.AcceptancePreparedJson : CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(prepared));
        if (target is "Prepared" or "Submitted" or "Confirmed" or "FailedNoEffect")
        {
            if (wireJson is null) return null;
            var wire = JsonSerializer.Deserialize<PreparedValueAcceptance>(wireJson)!;
            if (CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(wire.Intent)) != expected.AcceptanceIntentJson
                || wire.ProtocolIdentity != savedIntent.Cost.Quote.ProtocolIdentity || string.IsNullOrWhiteSpace(wire.TransactionId)
                || string.IsNullOrWhiteSpace(wire.SignedTransactionBase64)) return null;
            if ((target is "Confirmed" or "FailedNoEffect") && (evidence!.Version != 1
                || evidence.Confirmation != (target == "Confirmed" ? ChainConfirmation.Confirmed : ChainConfirmation.FailedOnChain)
                || evidence.PreparedHash != CanonicalJson.HashCanonical(wireJson) || evidence.TransactionId != wire.TransactionId
                || evidence.ProtocolIdentity != wire.ProtocolIdentity)) return null;
        }
        // raw: acceptance stage CAS binds actual participant authority before egress, and exact finalized wire for later observation.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        if (target is not ("Confirmed" or "FailedNoEffect"))
        {
            var guards = SigningAuthorityFence.Create(authority, [AzoaScopes.TransferSign], "hold_acceptance_authority");
            if (guards is null) return null;
            statements.AddRange(guards);
            statements.AddRange(OriginalWorkflowFence(expected));
        }
        var write = SurrealWriter.UpdateOnly<HoldCommand>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == expected.State && row.IntentHash == expected.IntentHash
                && row.AcceptanceState == expected.AcceptanceState && row.AcceptanceIntentJson == expected.AcceptanceIntentJson
                && row.AcceptanceAuthorityJson == expected.AcceptanceAuthorityJson && row.AcceptancePreparedJson == expected.AcceptancePreparedJson
                && row.CurrentAcceptanceAuthorityJson == expected.CurrentAcceptanceAuthorityJson
                && row.CurrentAcceptanceAuthorizationId == expected.CurrentAcceptanceAuthorizationId)
            .Set(row => row.Version, expected.Version + 1).Set(row => row.AcceptanceState, target)
            .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow);
        if (wireJson is not null) write = write.Set(row => row.AcceptancePreparedJson, wireJson);
        if (evidence is not null) write = write.Set(row => row.AcceptanceEvidenceJson, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(evidence)));
        statements.Add(SurrealGeneratedStatementScope.Assign(write.Build(), "hold_acceptance_stage", "_hold_acceptance_stage"));
        statements.Add(SurrealQuery.Of("IF $_hold_acceptance_stage = NONE OR $_hold_acceptance_stage = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        return await CommitAcceptanceAsync(statements, expected.Id, ct);
    }

    private async Task<HoldCommand?> CommitAcceptanceAsync(List<SurrealQuery> statements, string id, CancellationToken ct)
    {
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk(); return response;
            }, ct);
            return await GetCommandAsync(id, ct);
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal))
        { return null; }
    }
}

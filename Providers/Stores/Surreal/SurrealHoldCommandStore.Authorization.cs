using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public async Task<HoldCommand?> AuthorizeRailAsync(HoldCommand expected, HoldCommandAuthorization authorization,
        CancellationToken ct = default)
    {
        if (authorization.Kind != "Transfer" || authorization.OperationId != expected.Id || authorization.ActorUserId != expected.ActorUserId
            || authorization.TenantId != expected.TenantId
            || string.IsNullOrWhiteSpace(authorization.Id) || string.IsNullOrWhiteSpace(authorization.RequestHash)) return null;
        var previous = await executor.QuerySingleAsync<HoldCommandAuthorization>(SurrealQuery<HoldCommandAuthorization>.Key(authorization.Id), ct);
        if (previous is not null)
            return previous.Kind == "Transfer" && previous.OperationId == expected.Id && previous.RequestHash == authorization.RequestHash
                && previous.ActorUserId == expected.ActorUserId && previous.TenantId == expected.TenantId
                ? await GetCommandAsync(expected.Id, ct) : null;
        if (authorization.ExpectedVersion != expected.Version
            || expected.State is not ("AwaitingConsent" or "AwaitingParticipant" or "Admitted" or "Preparing" or "Prepared")
            || expected.Version is < 0 or long.MaxValue || expected.RailPlanJson is null) return null;
        var intent = JsonSerializer.Deserialize<HoldCommandIntent>(expected.IntentJson)!;
        var plan = JsonSerializer.Deserialize<HoldRailPlan>(expected.RailPlanJson)!;
        if (authorization.QuoteId != plan.Quote.QuoteId || !ValueCustodyIdentity.ValidQuote(plan.Quote)
            || !UnsignedIntegerText.TryParse(authorization.MaximumNetworkFeeBaseUnits, out var maximum)
            || !UnsignedIntegerText.TryParse(plan.Quote.TransferNetworkFeeBaseUnits, out var quoted) || maximum < quoted
            || authorization.PreparedHash != (expected.PreparedJson is null ? null : CanonicalJson.HashCanonical(expected.PreparedJson))) return null;
        if (expected.StageIntentJson is not null)
        {
            var savedIntent = JsonSerializer.Deserialize<ValueTransferIntent>(expected.StageIntentJson)!;
            if (savedIntent.Cost?.MaximumNetworkFeeBaseUnits != authorization.MaximumNetworkFeeBaseUnits) return null;
        }
        var authority = JsonSerializer.Deserialize<HoldCommandAuthority>(authorization.AuthorityJson)!;
        if (authority.ReleaseMandateId is not null
            || JsonSerializer.Deserialize<HoldCommandAuthority>(expected.AuthorityJson)?.ReleaseMandateId is not null) return null;
        var fences = await RailAuthorityAsync(intent, plan, authority, ct);
        if (fences is null) return null;
        var stage = new ValueTransferIntent(intent.Asset.ChainType, intent.Asset.Network, intent.Asset.AssetId,
            plan.Quote.Request.Sender, plan.Quote.Request.Recipient, plan.Quote.Request.QuantityBaseUnits,
            plan.SenderSigningContext, $"hold:{expected.Id}:{plan.Kind}:{expected.Generation}",
            new(1, expected.Id, plan.Kind, expected.Generation, plan.Quote, plan.Quote.Request.Sender, authorization.MaximumNetworkFeeBaseUnits));
        var stageJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(stage));
        if (expected.StageIntentJson is not null && expected.StageIntentJson != stageJson) return null;
        // raw: exact quote/wire consent, current authority and original workflow fence commit with immutable authorization evidence.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fences);
        statements.AddRange(OriginalWorkflowFence(expected));
        if (expected.AcceptedAt is null)
            statements.Add(SurrealQuery.Of("IF $_hold_consent_expiry <= time::now() { THROW 'HOLD_COMMAND_CHANGED' }")
                .WithParam("_hold_consent_expiry", plan.Quote.ExpiresAt));
        var newState = expected.State == "AwaitingConsent" ? "Admitted" : expected.State;
        var write = SurrealWriter.UpdateOnly<HoldCommand>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == expected.State && row.IntentHash == expected.IntentHash
                && row.RailPlanJson == expected.RailPlanJson && row.PreparedJson == expected.PreparedJson
                && row.StageIntentJson == expected.StageIntentJson && row.CurrentAuthorizationId == expected.CurrentAuthorizationId)
            .Set(row => row.Version, expected.Version + 1).Set(row => row.State, newState)
            .Set(row => row.AcceptedQuoteId, plan.Quote.QuoteId).Set(row => row.AcceptedAt, expected.AcceptedAt ?? authorization.CreatedAt)
            .Set(row => row.CurrentAuthorizationId, authorization.Id).Set(row => row.CurrentAuthorityJson, authorization.AuthorityJson)
            .Set(row => row.StageIntentJson, stageJson).Set(row => row.UpdatedAt, authorization.CreatedAt);
        statements.Add(SurrealGeneratedStatementScope.Assign(write.Build(), "hold_consent", "_hold_consent"));
        statements.Add(SurrealQuery.Of("IF $_hold_consent = NONE OR $_hold_consent = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(authorization), "hold_consent_receipt"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk(); return response;
            }, ct);
            return await GetCommandAsync(expected.Id, ct);
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal))
        {
            previous = await executor.QuerySingleAsync<HoldCommandAuthorization>(SurrealQuery<HoldCommandAuthorization>.Key(authorization.Id), ct);
            return previous is not null && previous.Kind == "Transfer" && previous.OperationId == expected.Id && previous.RequestHash == authorization.RequestHash
                && previous.ActorUserId == expected.ActorUserId && previous.TenantId == expected.TenantId
                ? await GetCommandAsync(expected.Id, ct) : null;
        }
    }

    private static IReadOnlyList<SurrealQuery> OriginalWorkflowFence(HoldCommand row)
    {
        var original = JsonSerializer.Deserialize<HoldCommandAuthority>(row.AuthorityJson)!;
        if (original.Workflow is not { } workflow) return [];
        var result = SigningAuthorityFence.ForNodeAdmission(workflow, "hold_original_workflow")?.ToList()
            ?? throw new InvalidOperationException("The original Hold workflow signing witness is invalid.");
        result.AddRange(ActionExecutionFence.Create(workflow.Action.RunId.ToString("N"),
            workflow.Action.NodeId.ToString("N"), workflow.Action.ActorUserId.ToString("N"),
            workflow.Action.ActingTenantId?.ToString("N"), workflow.Action.IntentHash));
        // raw: fresh participant consent cannot replace the original frozen workflow signing authority.
        result.Add(SurrealQuery.Of("""
            IF $_effect_execution[0].signing_authority_json = NONE
                OR crypto::sha256($_effect_execution[0].signing_authority_json) != $_hold_original_workflow_hash
                { THROW 'HOLD_COMMAND_CHANGED' }
            """).WithParam("_hold_original_workflow_hash", CanonicalJson.Hash(workflow)));
        return result;
    }
}

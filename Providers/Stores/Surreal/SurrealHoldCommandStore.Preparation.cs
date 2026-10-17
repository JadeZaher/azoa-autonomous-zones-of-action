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
    public Task<HoldCommand?> BeginPreparationAsync(HoldCommand expected, CancellationToken ct = default)
        => TransitionPreparedAsync(expected, expected.State == "Preparing" ? "Preparing" : "Admitted", "Preparing", null, ct);

    /// <inheritdoc/>
    public Task<HoldCommand?> SavePreparedAsync(HoldCommand expected, PreparedValueTransfer prepared, CancellationToken ct = default)
        => TransitionPreparedAsync(expected, "Preparing", "Prepared", prepared, ct);

    /// <inheritdoc/>
    public Task<HoldCommand?> AuthorizeSendAsync(HoldCommand expected, CancellationToken ct = default)
        => TransitionPreparedAsync(expected, "Prepared", "Submitted", null, ct);

    private async Task<HoldCommand?> TransitionPreparedAsync(HoldCommand expected, string source, string target,
        PreparedValueTransfer? prepared, CancellationToken ct)
    {
        if (expected.State != source || expected.Version is < 0 or long.MaxValue || expected.RailPlanJson is null
            || expected.StageIntentJson is null || expected.CurrentAuthorityJson is null || expected.CurrentAuthorizationId is null
            || expected.AcceptedQuoteId is null || expected.AcceptedAt is null) return null;
        var intent = JsonSerializer.Deserialize<HoldCommandIntent>(expected.IntentJson)!;
        var plan = JsonSerializer.Deserialize<HoldRailPlan>(expected.RailPlanJson)!;
        var authority = JsonSerializer.Deserialize<HoldCommandAuthority>(expected.CurrentAuthorityJson)!;
        var fences = await RailAuthorityAsync(intent, plan, authority, ct);
        if (fences is null || expected.AcceptedQuoteId != plan.Quote.QuoteId) return null;
        HoldReleaseMandate? mandate = null;
        if (authority.ReleaseMandateId is { } mandateId)
        {
            mandate = await executor.QuerySingleAsync<HoldReleaseMandate>(SurrealQuery<HoldReleaseMandate>.Key(mandateId), ct);
            if (mandate is null || mandate.Status != "Active" || mandate.OperationId != expected.Id) return null;
        }
        var preparedJson = prepared is null ? expected.PreparedJson : CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(prepared));
        if (target is "Prepared" or "Submitted")
        {
            if (preparedJson is null) return null;
            var wire = JsonSerializer.Deserialize<PreparedValueTransfer>(preparedJson)!;
            if (string.IsNullOrWhiteSpace(wire.TransactionId) || string.IsNullOrWhiteSpace(wire.SignedTransactionBase64)
                || wire.ProtocolIdentity != intent.Asset.ProtocolIdentity
                || CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(wire.Intent)) != expected.StageIntentJson) return null;
        }
        var aggregate = await GetHoldAsync(expected.HoldRecordId, ct);
        if (aggregate is null || aggregate.ActiveCommandId != expected.Id || aggregate.Version is < 0 or long.MaxValue
            || aggregate.State != (plan.Kind == "Funding" ? "Pending" : "Held")) return null;
        // raw: current authority, original liability and release cutoff serialize with the command's first-egress transition.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fences);
        statements.AddRange(OriginalWorkflowFence(expected));
        if (mandate is not null)
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldReleaseMandate>(mandate.Id)
                .Where(row => row.Version == mandate.Version && row.Status == "Active" && row.OperationId == expected.Id
                    && row.TermsHash == mandate.TermsHash && row.CurrentAuthorityJson == mandate.CurrentAuthorityJson
                    && row.CurrentAuthorizationId == mandate.CurrentAuthorizationId)
                .Set(row => row.Version, mandate.Version).Build(), "hold_mandate_use", "_hold_mandate_use"));
            statements.Add(SurrealQuery.Of("IF $_hold_mandate_use = NONE OR $_hold_mandate_use = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
            var use = new HoldMandateUseReceipt { Id = CanonicalJson.Hash(new { Domain = "hold-mandate-use-v1", expected.Id, expected.Version }),
                OperationId = expected.Id, CommandVersion = expected.Version, Stage = target, MandateId = mandate.Id,
                MandateVersion = mandate.Version, TermsHash = mandate.TermsHash, AuthorityJson = mandate.CurrentAuthorityJson,
                WorkflowSourceJson = mandate.WorkflowSourceJson, ExchangeSourceJson = mandate.ExchangeSourceJson,
                AuthorizationId = mandate.CurrentAuthorizationId, PreparedHash = preparedJson is null ? null : CanonicalJson.HashCanonical(preparedJson),
                CreatedAt = DateTimeOffset.UtcNow };
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(use), "hold_mandate_use_receipt"));
        }
        if (plan.RecipientWallet is { } recipient)
        {
            var owner = SurrealLink.ToLink("user", recipient.OwnerUserId.ToString("N"))!;
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Wallet>(recipient.Id.ToString("N"))
                .Where(row => row.UserId == owner && row.Address == recipient.Address && row.ChainType == recipient.ChainType)
                .Set(row => row.Address, recipient.Address).Build(), "hold_stage_recipient", "_hold_stage_recipient"));
            statements.Add(SurrealQuery.Of("IF $_hold_stage_recipient = NONE OR $_hold_stage_recipient = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        }
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(aggregate.Id)
            .Where(row => row.Version == aggregate.Version && row.ActiveCommandId == expected.Id && row.State == aggregate.State
                && row.DefinitionHash == aggregate.DefinitionHash && row.DefinitionJson == aggregate.DefinitionJson
                && row.ReleaseNotBefore == aggregate.ReleaseNotBefore)
            .Set(row => row.Version, aggregate.Version).Build(), "hold_stage_liability", "_hold_stage_liability"));
        statements.Add(SurrealQuery.Of("IF $_hold_stage_liability = NONE OR $_hold_stage_liability = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        if (target == "Submitted" && intent.Kind == "Release" && aggregate.ReleaseNotBefore is { } cutoff)
            statements.Add(SurrealQuery.Of("IF $_hold_release_cutoff > time::now() { THROW 'HOLD_COMMAND_CHANGED' }")
                .WithParam("_hold_release_cutoff", cutoff));
        var write = SurrealWriter.UpdateOnly<HoldCommand>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == source && row.IntentHash == expected.IntentHash
                && row.RailPlanJson == expected.RailPlanJson && row.StageIntentJson == expected.StageIntentJson
                && row.CurrentAuthorityJson == expected.CurrentAuthorityJson && row.CurrentAuthorizationId == expected.CurrentAuthorizationId
                && row.PreparedJson == expected.PreparedJson && row.Generation == expected.Generation)
            .Set(row => row.Version, expected.Version + 1).Set(row => row.State, target).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow);
        if (preparedJson is not null) write = write.Set(row => row.PreparedJson, preparedJson);
        statements.Add(SurrealGeneratedStatementScope.Assign(write.Build(), "hold_stage", "_hold_stage"));
        statements.Add(SurrealQuery.Of("IF $_hold_stage = NONE OR $_hold_stage = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
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
        catch (SurrealStatementException exception) when (exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal))
        {
            return null;
        }
    }
}

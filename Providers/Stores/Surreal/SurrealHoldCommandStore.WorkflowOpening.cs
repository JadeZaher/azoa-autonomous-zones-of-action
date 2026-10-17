using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    private sealed record WorkflowOpeningCommit(HoldCommand Expected, HoldWorkflowMandateDraft Draft,
        HoldWorkflowOpeningAcceptance Acceptance, SigningConsentWitness Authority, string AuditId, string RequestHash);

    /// <inheritdoc/>
    public async Task<HoldCommand?> AdmitWorkflowOpeningAsync(HoldCommandIntent intent, HoldCommandAuthority authority,
        HoldWorkflowMandateDraft draft, CancellationToken ct = default)
    {
        if (!ValidWorkflowDraft(intent, authority, draft)) return null;
        var id = HoldCommandIdentity.Operation(intent); var hash = CanonicalJson.Hash(intent);
        var draftJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(draft));
        var authorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority));
        var prior = await GetCommandAsync(id, ct);
        if (prior is not null) return prior.IntentHash == hash && prior.AuthorityJson == authorityJson
            && prior.WorkflowMandateDraftJson == draftJson ? prior : null;
        if (intent.Kind == "ReleaseIntoHold")
        {
            var parent = await GetHoldAsync(HoldCommandIdentity.Hold(intent), ct);
            if (parent?.DefinitionJson is null || parent.State != "Held") return null;
            var inherited = JsonSerializer.Deserialize<HoldDefinition>(parent.DefinitionJson)!;
            var childDefinition = inherited with { BeneficiaryUserId = intent.NextBeneficiaryUserId!.Value,
                ReleaseNotBefore = intent.NextReleaseNotBefore, AutoRelease = true };
            var child = HoldAggregateProjection.Create(intent, intent.NextHoldId!, childDefinition, id, DateTimeOffset.UtcNow);
            if (!HoldReleaseMandateIdentity.ValidTerms(child, new(Guid.ParseExact(draft.ExecutionRecordId, "N"), 0, draft.Terms, draft.Quote))) return null;
        }
        var fences = LocalAuthority(intent, authority);
        if (fences is null) return null;
        var now = DateTimeOffset.UtcNow;
        var row = new HoldCommand { Id = id, ActorUserId = intent.ActorUserId.ToString("N"), TenantId = intent.TenantId?.ToString("N"),
            HoldRecordId = HoldCommandIdentity.Hold(intent), Kind = intent.Kind, IntentHash = hash,
            IntentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent)), AuthorityJson = authorityJson,
            WorkflowMandateDraftJson = draftJson, State = "AwaitingConsent", Stage = "Opening", CreatedAt = now, UpdatedAt = now };
        // raw: frozen workflow disclosure and exact execution identity commit without any custody liability or reserve draw.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fences);
        statements.AddRange(OpeningExecutionFence(draft, authority.Workflow!));
        statements.Add(SurrealQuery.Of("IF $_hold_open_quote_expiry <= time::now() { THROW 'HOLD_COMMAND_CHANGED' }")
            .WithParam("_hold_open_quote_expiry", draft.Quote.ExpiresAt));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(row), "hold_open_draft"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct); response.EnsureAllOk(); return response;
            }, ct);
            return row;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal))
        {
            prior = await GetCommandAsync(id, ct);
            return prior is not null && prior.IntentHash == hash && prior.AuthorityJson == authorityJson
                && prior.WorkflowMandateDraftJson == draftJson ? prior : null;
        }
    }

    /// <inheritdoc/>
    public async Task<HoldCommandReceipt?> CommitWorkflowOpeningAsync(HoldCommand expected, HoldWorkflowOpeningAcceptance acceptance,
        SigningConsentWitness currentAuthority, CancellationToken ct = default)
    {
        if (acceptance is null || acceptance.RequestId == Guid.Empty || acceptance.Version < 0 || expected.WorkflowMandateDraftJson is null
            || expected.ActorUserId != currentAuthority.Credential.ActorUserId.ToString("N")
            || expected.TenantId != currentAuthority.Credential.TenantId?.ToString("N")
            || CanonicalJson.HashCanonical(expected.WorkflowMandateDraftJson) != acceptance.DraftHash) return null;
        var auditId = HoldWorkflowOpeningIdentity.AuditId(expected.Id, currentAuthority.Credential.ActorUserId,
            currentAuthority.Credential.TenantId, acceptance.RequestId);
        var requestHash = HoldWorkflowOpeningIdentity.RequestHash(expected.Id, expected.IntentHash, acceptance);
        var prior = await GetRecoveryAsync(auditId, ct);
        if (prior is not null) return prior.Kind == "WorkflowOpeningAccept" && prior.RequestHash == requestHash
            ? await GetReceiptAsync(expected.Id, ct) : null;
        if (expected.State != "AwaitingConsent" || expected.Version != acceptance.Version || expected.Version == long.MaxValue) return null;
        var intent = JsonSerializer.Deserialize<HoldCommandIntent>(expected.IntentJson)!;
        var authority = JsonSerializer.Deserialize<HoldCommandAuthority>(expected.AuthorityJson)!;
        var draft = JsonSerializer.Deserialize<HoldWorkflowMandateDraft>(expected.WorkflowMandateDraftJson)!;
        if (!ValidWorkflowDraft(intent, authority, draft)) return null;
        await CommitLocalCoreAsync(intent, authority, new(expected, draft, acceptance, currentAuthority, auditId, requestHash), ct);
        prior = await GetRecoveryAsync(auditId, ct);
        return prior is not null && prior.Kind == "WorkflowOpeningAccept" && prior.RequestHash == requestHash
            ? await GetReceiptAsync(expected.Id, ct) : null;
    }

    private static bool ValidWorkflowDraft(HoldCommandIntent intent, HoldCommandAuthority authority, HoldWorkflowMandateDraft draft)
    {
        if (draft is null || draft.Version != 1 || !Guid.TryParseExact(draft.ExecutionRecordId, "N", out var executionId)
            || executionId == Guid.Empty
            || authority.Workflow is null || authority.Direct is not null || authority.ReleaseMandateId is not null
            || authority.EconomicSource is not null || authority.Sender is not null) return false;
        if (intent.Kind == "ReleaseIntoHold")
            return intent.NextAutoRelease && intent.Definition is null && !string.IsNullOrWhiteSpace(intent.NextHoldId)
                && intent.NextBeneficiaryUserId is { } beneficiary && beneficiary != Guid.Empty
                && draft.Terms?.Recipient is not null && draft.Quote is not null && draft.Terms.Asset == intent.Asset
                && draft.Terms.HoldRecordId == HoldCommandIdentity.Hold(intent, intent.NextHoldId)
                && draft.Terms.Recipient.OwnerUserId == beneficiary;
        if (intent.Kind != "Open" || intent.Definition is not { AutoRelease: true, FundingSource: "Reserve" }) return false;
        var prospective = HoldAggregateProjection.Create(intent, intent.HoldId, intent.Definition,
            HoldCommandIdentity.Operation(intent), DateTimeOffset.UtcNow);
        return HoldReleaseMandateIdentity.ValidTerms(prospective, new(executionId, 0, draft.Terms, draft.Quote));
    }

    private static IReadOnlyList<SurrealQuery> OpeningExecutionFence(HoldWorkflowMandateDraft draft, WorkflowSigningWitness witness)
    {
        var run = SurrealLink.ToLink("action_run", witness.Action.RunId.ToString("N"))!;
        var node = SurrealLink.ToLink("action_node", witness.Action.NodeId.ToString("N"))!;
        var signing = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(witness));
        return
        [
            SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ActionNodeExecution>(draft.ExecutionRecordId)
                .Where(row => row.RunId == run && row.NodeId == node && row.State == ActionNodeExecution.ActionNodeState.Running
                    && row.SigningAuthorityJson == signing).Set(row => row.State, ActionNodeExecution.ActionNodeState.Running).Build(),
                "hold_open_execution", "_hold_open_execution"),
            // raw: the accepted parked disclosure cannot alias another retry or fork's execution record.
            SurrealQuery.Of("IF $_hold_open_execution = NONE OR $_hold_open_execution = [] { THROW 'HOLD_COMMAND_CHANGED' }")
        ];
    }

    private static IReadOnlyList<SurrealQuery>? ComposeWorkflowOpening(HoldCommandIntent intent, HoldCommandAuthority original,
        WorkflowOpeningCommit opening, HoldLocalCommandPlan plan, DateTimeOffset now)
    {
        var draft = opening.Draft;
        var liability = plan.Child ?? plan.Result;
        if (!HoldReleaseMandateIdentity.ValidTerms(liability, new(Guid.ParseExact(draft.ExecutionRecordId, "N"), 0, draft.Terms, draft.Quote))) return null;
        var guards = SigningAuthorityFence.ForConsent(opening.Authority, [AzoaScopes.TransferSign], "hold_open_accepting");
        if (guards is null || opening.Authority.Credential.ActorUserId != intent.ActorUserId
            || opening.Authority.Credential.TenantId != intent.TenantId) return null;
        var id = HoldCommandIdentity.Operation(intent);
        var mandateId = CanonicalJson.Hash(new { Domain = "hold-workflow-release-mandate-v1", Operation = id });
        var view = JsonSerializer.Deserialize<HoldCommandView>(plan.Receipt.OutcomeJson)!;
        plan.Receipt.OutcomeJson = JsonSerializer.Serialize(view with { ReleaseMandateId = mandateId, MandateTerms = draft.Terms });
        var source = new HoldReleaseWorkflowSource(1, draft.ExecutionRecordId, id, plan.Receipt.IntentHash,
            CanonicalJson.HashCanonical(plan.Receipt.OutcomeJson), original.Workflow!);
        var authorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(opening.Authority));
        var mandate = new HoldReleaseMandate { Id = mandateId, ActorUserId = intent.ActorUserId.ToString("N"),
            TenantId = intent.TenantId?.ToString("N"), HoldRecordId = liability.Id, RequestHash = opening.RequestHash,
            TermsJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(draft.Terms)), TermsHash = CanonicalJson.Hash(draft.Terms),
            OriginalAuthorityJson = authorityJson, CurrentAuthorityJson = authorityJson, CurrentAuthorizationId = opening.AuditId,
            OriginalQuoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(draft.Quote)),
            WorkflowSourceJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(source)),
            NotBefore = draft.Terms.NotBefore, ExpiresAt = draft.Terms.ExpiresAt, NextAttemptAt = draft.Terms.NotBefore,
            CreatedAt = now, UpdatedAt = now };
        liability.ActiveReleaseMandateId = mandateId;
        var audit = new HoldRecoveryReceipt { Id = opening.AuditId, OperationId = id, Kind = "WorkflowOpeningAccept",
            ActorUserId = mandate.ActorUserId, TenantId = mandate.TenantId, RequestHash = opening.RequestHash,
            AuthorityJson = authorityJson, OriginalState = opening.Expected.State, OriginalVersion = opening.Expected.Version,
            OutcomeJson = plan.Receipt.OutcomeJson, CreatedAt = now };
        var statements = guards.ToList();
        statements.AddRange(OpeningExecutionFence(draft, original.Workflow!));
        statements.Add(SurrealQuery.Of("IF $_hold_open_quote_expiry <= time::now() OR $_hold_open_mandate_expiry <= time::now() { THROW 'HOLD_COMMAND_CHANGED' }")
            .WithParam("_hold_open_quote_expiry", draft.Quote.ExpiresAt).WithParam("_hold_open_mandate_expiry", draft.Terms.ExpiresAt));
        var owner = SurrealLink.ToLink("user", draft.Terms.Recipient.OwnerUserId.ToString("N"))!;
        var recipient = draft.Terms.Recipient;
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Wallet>(recipient.Id.ToString("N"))
            .Where(row => row.UserId == owner && row.Address == recipient.Address && row.ChainType == recipient.ChainType)
            .Set(row => row.Address, recipient.Address).Build(), "hold_open_recipient", "_hold_open_recipient"));
        statements.Add(SurrealQuery.Of("IF $_hold_open_recipient = NONE OR $_hold_open_recipient = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(mandate), "hold_open_mandate"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "hold_open_acceptance_audit"));
        return statements;
    }
}

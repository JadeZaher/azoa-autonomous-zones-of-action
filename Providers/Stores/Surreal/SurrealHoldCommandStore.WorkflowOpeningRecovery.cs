using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
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
    public async Task<HoldCommand?> ReplaceWorkflowOpeningAsync(HoldCommand expected, HoldWorkflowMandateDraft draft,
        HoldRecoveryRequest request, SigningConsentWitness authority, CancellationToken ct = default)
    {
        if (!ValidOpeningRecovery(expected, request) || authority.Credential.ActorUserId.ToString("N") != expected.ActorUserId
            || authority.Credential.TenantId?.ToString("N") != expected.TenantId) return null;
        var intent = JsonSerializer.Deserialize<HoldCommandIntent>(expected.IntentJson)!;
        var source = JsonSerializer.Deserialize<HoldCommandAuthority>(expected.AuthorityJson)!;
        var original = JsonSerializer.Deserialize<HoldWorkflowMandateDraft>(expected.WorkflowMandateDraftJson!)!;
        if (!ValidWorkflowDraft(intent, source, draft) || original.ExecutionRecordId != draft.ExecutionRecordId
            || CanonicalJson.Hash(original.Terms) != CanonicalJson.Hash(draft.Terms)) return null;
        var guards = LocalAuthority(intent, source);
        var accepting = SigningAuthorityFence.ForConsent(authority, [AzoaScopes.TransferSign], "hold_open_refresh_actor");
        if (guards is null || accepting is null) return null;
        var all = guards.Concat(accepting).Concat(OpeningExecutionFence(original, source.Workflow!)).ToList();
        // raw: only fresh provider disclosure can replace the unaccepted original version; its immutable terms stay fixed.
        all.Add(SurrealQuery.Of("IF $_hold_open_new_expiry <= time::now() OR $_hold_open_terms_expiry <= time::now() { THROW 'HOLD_COMMAND_CHANGED' }")
            .WithParam("_hold_open_new_expiry", draft.Quote.ExpiresAt).WithParam("_hold_open_terms_expiry", draft.Terms.ExpiresAt));
        return await ChangeOpeningAsync(expected, draft, request, authority.Credential,
            "WorkflowOpeningReplace", all, ct);
    }

    /// <inheritdoc/>
    public async Task<HoldCommand?> WithdrawWorkflowOpeningAsync(string id, HoldRecoveryRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (credential.TenantId is not null || !credential.OwnerAuthority || credential.Kind != "Jwt"
            || credential.TokenUse != AzoaClaims.TokenUseLogin || request is null) return null;
        var expected = await GetCommandAsync(id, ct);
        if (expected is null || expected.ActorUserId != credential.ActorUserId.ToString("N")) return null;
        var guards = SigningAuthorityFence.ForRunAdmission(credential, "hold_open_withdraw_owner");
        if (guards is null) return null;
        return await ChangeOpeningAsync(expected, null, request, credential, "WorkflowOpeningWithdraw", guards, ct);
    }

    private static bool ValidOpeningRecovery(HoldCommand expected, HoldRecoveryRequest request)
        => request is not null && request.RequestId != Guid.Empty && request.Version >= 0
            && !string.IsNullOrWhiteSpace(request.Reason) && request.Reason.Length <= 500
            && expected.WorkflowMandateDraftJson is not null;

    private async Task<HoldCommand?> ChangeOpeningAsync(HoldCommand expected, HoldWorkflowMandateDraft? replacement,
        HoldRecoveryRequest request, SigningCredentialWitness credential, string kind,
        IReadOnlyList<SurrealQuery> guards, CancellationToken ct)
    {
        if (!ValidOpeningRecovery(expected, request)) return null;
        var id = HoldRecoveryIdentity.Id(kind, expected.Id, credential.ActorUserId, credential.TenantId, request.RequestId);
        var hash = HoldRecoveryIdentity.RequestHash(kind, expected.Id, credential.ActorUserId, credential.TenantId, request);
        var prior = await GetRecoveryAsync(id, ct);
        if (prior is not null) return prior.Kind == kind && prior.RequestHash == hash
            ? JsonSerializer.Deserialize<HoldCommand>(prior.OutcomeJson) : null;
        if (expected.Version != request.Version || expected.Version == long.MaxValue || expected.State != "AwaitingConsent"
            || expected.Stage != "Opening" || expected.RailPlanJson is not null || expected.PreparedJson is not null) return null;
        var outcome = JsonSerializer.Deserialize<HoldCommand>(JsonSerializer.Serialize(expected))!;
        outcome.Version++; outcome.UpdatedAt = DateTimeOffset.UtcNow;
        outcome.State = replacement is null ? "Cancelled" : "AwaitingConsent";
        outcome.Detail = replacement is null ? "Unaccepted opening withdrawn; no reserve was drawn."
            : "Fresh disclosure requires explicit acceptance; no reserve has been drawn.";
        if (replacement is not null) outcome.WorkflowMandateDraftJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(replacement));
        var audit = new HoldRecoveryReceipt { Id = id, OperationId = expected.Id, Kind = kind, ActorUserId = expected.ActorUserId,
            TenantId = credential.TenantId?.ToString("N"), RequestHash = hash,
            AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { credential, OriginalDraft = expected.WorkflowMandateDraftJson })),
            OriginalState = expected.State, OriginalVersion = expected.Version,
            OutcomeJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(outcome)), CreatedAt = outcome.UpdatedAt };
        // raw: unaccepted disclosure CAS and immutable recovery audit serialize against opening acceptance without liability mutations.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldCommand>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == "AwaitingConsent" && row.Stage == "Opening"
                && row.IntentHash == expected.IntentHash && row.AuthorityJson == expected.AuthorityJson
                && row.WorkflowMandateDraftJson == expected.WorkflowMandateDraftJson && row.PreparedJson == null && row.RailPlanJson == null)
            .Set(row => row.Version, outcome.Version).Set(row => row.State, outcome.State)
            .Set(row => row.WorkflowMandateDraftJson, outcome.WorkflowMandateDraftJson!)
            .Set(row => row.Detail, outcome.Detail).Set(row => row.UpdatedAt, outcome.UpdatedAt).Build(), "hold_open_recovery", "_hold_open_recovery"));
        statements.Add(SurrealQuery.Of("IF $_hold_open_recovery = NONE OR $_hold_open_recovery = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "hold_open_recovery_audit"));
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
            prior = await GetRecoveryAsync(id, ct);
            return prior is not null && prior.Kind == kind && prior.RequestHash == hash
                ? JsonSerializer.Deserialize<HoldCommand>(prior.OutcomeJson) : null;
        }
    }
}

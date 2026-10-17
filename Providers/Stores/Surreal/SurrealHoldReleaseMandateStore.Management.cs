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

public sealed partial class SurrealHoldReleaseMandateStore
{
    /// <inheritdoc/>
    public async Task<HoldReleaseMandate?> GetRecoveryAsync(string id, string kind, HoldRecoveryRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (kind is not ("MandateRevoke" or "MandateReauthorize") || request is null || request.RequestId == Guid.Empty) return null;
        var auditId = HoldRecoveryIdentity.Id(kind, id, credential.ActorUserId, credential.TenantId, request.RequestId);
        var hash = HoldRecoveryIdentity.RequestHash(kind, id, credential.ActorUserId, credential.TenantId, request);
        var receipt = await executor.QuerySingleAsync<HoldRecoveryReceipt>(SurrealQuery<HoldRecoveryReceipt>.Key(auditId), ct);
        return receipt is not null && receipt.Kind == kind && receipt.OperationId == id && receipt.RequestHash == hash
            && receipt.ActorUserId == credential.ActorUserId.ToString("N") && receipt.TenantId == credential.TenantId?.ToString("N")
            ? JsonSerializer.Deserialize<HoldReleaseMandate>(receipt.OutcomeJson) : null;
    }

    /// <inheritdoc/>
    public Task<HoldReleaseMandate?> ReauthorizeAsync(string id, HoldRecoveryRequest request, SigningConsentWitness authority,
        CancellationToken ct = default) => ChangeAuthorityAsync(id, request, authority.Credential, authority, false, ct);

    /// <inheritdoc/>
    public Task<HoldReleaseMandate?> RevokeAsync(string id, HoldRecoveryRequest request, SigningCredentialWitness credential,
        CancellationToken ct = default) => ChangeAuthorityAsync(id, request, credential, null, true, ct);

    private async Task<HoldReleaseMandate?> ChangeAuthorityAsync(string id, HoldRecoveryRequest request,
        SigningCredentialWitness credential, SigningConsentWitness? authority, bool revoke, CancellationToken ct)
    {
        if (request is null || request.RequestId == Guid.Empty || request.Version < 0 || string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Length > 500) return null;
        var kind = revoke ? "MandateRevoke" : "MandateReauthorize";
        var auditId = HoldRecoveryIdentity.Id(kind, id, credential.ActorUserId, credential.TenantId, request.RequestId);
        var hash = HoldRecoveryIdentity.RequestHash(kind, id, credential.ActorUserId, credential.TenantId, request);
        var prior = await executor.QuerySingleAsync<HoldRecoveryReceipt>(SurrealQuery<HoldRecoveryReceipt>.Key(auditId), ct);
        if (prior is not null) return prior.Kind == kind && prior.OperationId == id && prior.RequestHash == hash
            ? JsonSerializer.Deserialize<HoldReleaseMandate>(prior.OutcomeJson) : null;
        var row = await GetAsync(id, ct);
        if (row is null || row.ActorUserId != credential.ActorUserId.ToString("N") || row.Version != request.Version
            || row.Version == long.MaxValue || row.Status != "Active") return null;
        IReadOnlyList<SurrealQuery>? guards;
        if (revoke)
        {
            if (!credential.OwnerAuthority || credential.TenantId is not null) return null;
            guards = SigningAuthorityFence.ForRunAdmission(credential, "hold_mandate_revoke");
        }
        else
        {
            if (authority is null || row.TenantId != credential.TenantId?.ToString("N")) return null;
            guards = SigningAuthorityFence.ForConsent(authority, [AzoaScopes.TransferSign], "hold_mandate_reauthorize");
        }
        if (guards is null) return null;
        var now = DateTimeOffset.UtcNow;
        var outcome = JsonSerializer.Deserialize<HoldReleaseMandate>(JsonSerializer.Serialize(row))!;
        outcome.Version++; outcome.UpdatedAt = now; outcome.CurrentAuthorizationId = auditId;
        if (revoke)
        {
            outcome.Status = "Revoked";
            outcome.Detail = "Further mandate authorization is revoked. Previously authorized signed transactions may still settle and will be reconciled.";
        }
        else outcome.CurrentAuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority));
        var audit = new HoldRecoveryReceipt { Id = auditId, OperationId = id, Kind = kind,
            ActorUserId = credential.ActorUserId.ToString("N"), TenantId = credential.TenantId?.ToString("N"), RequestHash = hash,
            AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(revoke ? (object)credential : authority!)),
            OriginalState = row.Status, OriginalVersion = row.Version, OutcomeJson = JsonSerializer.Serialize(outcome), CreatedAt = now };
        // raw: revocation/renewal and immutable audit serialize with the exact mandate's first-egress authority.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        if (!revoke) statements.Add(SurrealQuery.Of("IF $_mandate_renew_expiry <= time::now() { THROW 'HOLD_MANDATE_CHANGED' }")
            .WithParam("_mandate_renew_expiry", row.ExpiresAt));
        var update = SurrealWriter.UpdateOnly<HoldReleaseMandate>(id)
            .Where(value => value.Version == row.Version && value.Status == "Active" && value.TermsHash == row.TermsHash
                && value.CurrentAuthorityJson == row.CurrentAuthorityJson && value.CurrentAuthorizationId == row.CurrentAuthorizationId)
            .Set(value => value.Version, outcome.Version).Set(value => value.Status, outcome.Status)
            .Set(value => value.CurrentAuthorityJson, outcome.CurrentAuthorityJson).Set(value => value.CurrentAuthorizationId, auditId)
            .Set(value => value.UpdatedAt, now);
        if (revoke) update = update.Set(value => value.Detail, outcome.Detail!);
        statements.Add(SurrealGeneratedStatementScope.Assign(update.Build(), "mandate_change", "_mandate_change"));
        statements.Add(SurrealQuery.Of("IF $_mandate_change = NONE OR $_mandate_change = [] { THROW 'HOLD_MANDATE_CHANGED' }"));
        if (revoke)
        {
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<HoldLedger>(row.HoldRecordId)
                .Where(value => value.ActiveReleaseMandateId == id).Unset(value => value.ActiveReleaseMandateId).Build(), "mandate_release_pointer"));
        }
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(audit), "mandate_change_audit"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var result = await executor.ExecuteAsync(query, ct); result.EnsureAllOk(); return result;
            }, ct);
            return outcome;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains("HOLD_MANDATE_CHANGED", StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            prior = await executor.QuerySingleAsync<HoldRecoveryReceipt>(SurrealQuery<HoldRecoveryReceipt>.Key(auditId), ct);
            return prior is not null && prior.Kind == kind && prior.RequestHash == hash
                ? JsonSerializer.Deserialize<HoldReleaseMandate>(prior.OutcomeJson) : null;
        }
    }
}

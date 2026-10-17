using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed partial class SurrealSwapOperationStore(ISurrealExecutor executor) : ISwapOperationStore
{
    private const string ConflictMarker = "SWAP_ADMISSION_CONFLICT";

    /// <inheritdoc/>
    public Task<SwapOperationRecord?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<SwapOperationRecord>(SurrealQuery<SwapOperationRecord>.Key(id), ct);

    /// <inheritdoc/>
    public Task<SwapOperationRecord?> AdmitAsync(SwapOperationRecord candidate,
        SigningAdmissionAuthority authority, CancellationToken ct = default)
        => ClaimAsync(candidate, new(authority, null), false, ct);

    /// <inheritdoc/>
    public Task<SwapOperationRecord?> CancelBeforePreparationAsync(SwapOperationRecord candidate,
        SigningConsentWitness authority, CancellationToken ct = default)
        => ClaimAsync(candidate, new(null, authority), true, ct);

    private async Task<SwapOperationRecord?> ClaimAsync(SwapOperationRecord candidate,
        StoreAuthority authority, bool cancel, CancellationToken ct)
    {
        var intent = ValidateCandidate(candidate);
        if (intent is null || !SamePrincipal(intent, authority, cancel))
            return null;
        var existing = await GetAsync(candidate.Id, ct);
        if (existing is not null)
            return await ResolveExistingAsync(existing, candidate, intent, authority, cancel, ct);
        var guards = Authority(candidate, intent.Terms, authority, cancel);
        if (guards is null)
            return null;
        var saved = Clone(candidate);
        saved.CostAccountingVersion = 1;
        saved.State = cancel ? "Cancelled" : "Admitted";
        var quoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent.Terms));
        // raw: quote consumption/expiry, current authority and request creation commit together.
        var quoteGuard = SurrealQuery.Of(cancel ? """
            LET $_swap_quote = (UPDATE ONLY type::record('dex_quote', $_swap_quote_id) SET quote_hash = quote_hash
                WHERE actor_user_id = $_swap_actor AND tenant_id = $_swap_tenant AND quote_hash = $_swap_quote_hash
                    AND quote_json = $_swap_quote_json RETURN AFTER)
            """ : """
            LET $_swap_quote = (UPDATE ONLY type::record('dex_quote', $_swap_quote_id)
                SET consumed_by_operation_id = $_swap_operation
                WHERE actor_user_id = $_swap_actor AND tenant_id = $_swap_tenant AND quote_hash = $_swap_quote_hash
                    AND quote_json = $_swap_quote_json AND consumed_by_operation_id = NONE
                    AND expires_at > time::now() RETURN AFTER)
            """).WithParam("_swap_quote_id", candidate.QuoteId).WithParam("_swap_actor", candidate.ActorUserId)
            .WithParam("_swap_tenant", candidate.TenantId).WithParam("_swap_quote_hash", candidate.QuoteHash)
            .WithParam("_swap_quote_json", quoteJson);
        // SQL-11: only the admission branch reads $_swap_operation; the strict validator refuses an unreferenced binding.
        guards.Add(cancel ? quoteGuard : quoteGuard.WithParam("_swap_operation", candidate.Id));
        guards.Add(SurrealQuery.Of("IF $_swap_quote = NONE { THROW 'SWAP_ADMISSION_CONFLICT' }"));
        var create = SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(saved), "swap_create");
        var query = Transaction(guards, create);
        try
        {
            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var result = await executor.ExecuteAsync(query, ct);
                result.EnsureAllOk();
                return result;
            }, ct);
            return response.GetValues<SwapOperationRecord>(guards.Count + 1).Single();
        }
        catch (SurrealStatementException exception) when (ExpectedConflict(exception))
        {
            var winner = await GetAsync(candidate.Id, ct);
            return winner is null ? null : await ResolveExistingAsync(winner, candidate, intent, authority, cancel, ct);
        }
    }

    private async Task<SwapOperationRecord?> ResolveExistingAsync(SwapOperationRecord existing,
        SwapOperationRecord candidate, SwapIntent intent, StoreAuthority authority, bool cancel, CancellationToken ct)
    {
        if (!SameRequest(existing, candidate))
            return null;
        if (!cancel || existing.State == "Cancelled")
            return existing;
        if (existing.State != "Admitted" || existing.CurrentStageId is not null)
            return existing;
        var guards = Authority(existing, intent.Terms, authority, true);
        if (guards is null)
            return null;
        var mutation = SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<SwapOperationRecord>(candidate.Id)
            .Where(row => row.Version == existing.Version && row.State == "Admitted" && row.CurrentStageId == null)
            .Set(row => row.State, "Cancelled").Set(row => row.Version, existing.Version + 1)
            .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build(), "swap_cancel");
        var query = Transaction(guards, mutation);
        try
        {
            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var result = await executor.ExecuteAsync(query, ct);
                result.EnsureAllOk();
                return result;
            }, ct);
            return response.GetValues<SwapOperationRecord>(guards.Count + 1).SingleOrDefault()
                ?? await GetAsync(candidate.Id, ct);
        }
        catch (SurrealStatementException exception) when (ExpectedConflict(exception))
        {
            return null;
        }
    }

    private sealed record StoreAuthority(SigningAdmissionAuthority? Signing, SigningConsentWitness? Consent)
    {
        internal SigningCredentialWitness Credential => Signing?.Credential ?? Consent!.Credential;
    }

    private static List<SurrealQuery>? Authority(SwapOperationRecord candidate, DexQuoteTerms terms,
        SigningAdmissionAuthority authority, bool cancellingUnsent)
        => cancellingUnsent ? null : Authority(candidate, terms, new StoreAuthority(authority, null), false);

    private static List<SurrealQuery>? Authority(SwapOperationRecord candidate, DexQuoteTerms terms,
        StoreAuthority authority, bool cancellingUnsent)
    {
        var signing = cancellingUnsent && authority.Consent is { } consent
            ? DexSigningAuthorityFence.Cancel(terms, consent)
            : !cancellingUnsent && authority.Signing is { } admission
                ? DexSigningAuthorityFence.Create(candidate, terms, admission) : null;
        if (signing is null)
            return null;
        return signing.ToList();
    }

    private static SurrealQuery Transaction(IReadOnlyList<SurrealQuery> guards, SurrealQuery mutation)
        => SurrealQuery.Combine(new[] { SurrealQuery.Of("BEGIN") }.Concat(guards)
            .Append(mutation).Append(SurrealQuery.Of("COMMIT")).ToArray());

    private static bool ExpectedConflict(SurrealStatementException exception)
        => SurrealIdempotencyLedger.IsUniqueViolation(exception.Message) ||
            exception.Message.Contains(ConflictMarker, StringComparison.Ordinal) ||
            exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal) ||
            exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal);

    private static bool SameRequest(SwapOperationRecord left, SwapOperationRecord right)
        => left.ActorUserId == right.ActorUserId && left.TenantId == right.TenantId &&
            left.RequestKeyHash == right.RequestKeyHash && left.RequestHash == right.RequestHash &&
            left.QuoteId == right.QuoteId && left.QuoteHash == right.QuoteHash &&
            left.IntentHash == right.IntentHash && left.IntentJson == right.IntentJson &&
            left.ActionRunId == right.ActionRunId && left.ActionNodeId == right.ActionNodeId &&
            left.ActionIntentHash == right.ActionIntentHash;

    private static SwapOperationRecord Clone(SwapOperationRecord candidate)
        => JsonSerializer.Deserialize<SwapOperationRecord>(JsonSerializer.Serialize(candidate))!;

    private static SwapIntent? ValidateCandidate(SwapOperationRecord candidate)
    {
        if (candidate.Version != 0 || candidate.State != "Admitted" || candidate.CurrentStageId is not null ||
            candidate.ConfirmedEffectNetworkFee != "0" || candidate.ConfirmedCleanupNetworkFee != "0" ||
            candidate.ConfirmedRetainedRent != "0" || candidate.ConfirmedReleasedRent != "0" || candidate.UnresolvedRentReservation != "0" ||
            candidate.SettlementReceiptJson is not null || candidate.CleanupConsentJson is not null ||
            candidate.CleanupReceiptJson is not null || candidate.RecoveryHistoryCursor is not null ||
            candidate.UnresolvedRentStageCount != 0 || candidate.CleanupStarted ||
            string.IsNullOrEmpty(candidate.Id) || string.IsNullOrEmpty(candidate.ActorUserId) ||
            candidate.TenantId is null || string.IsNullOrEmpty(candidate.IntentJson) ||
            candidate.IntentHash is not { Length: 64 } || candidate.QuoteHash is not { Length: 64 } ||
            candidate.RequestKeyHash is not { Length: 64 } || candidate.RequestHash is not { Length: 64 } ||
            !Guid.TryParseExact(candidate.QuoteId, "N", out var quote) || quote == Guid.Empty ||
            (candidate.ActionRunId is null) != (candidate.ActionNodeId is null) ||
            (candidate.ActionRunId is null) != (candidate.ActionIntentHash is null))
            return null;
        try
        {
            using var document = JsonDocument.Parse(candidate.IntentJson);
            if (CanonicalJson.Canonicalize(document.RootElement) != candidate.IntentJson ||
                CanonicalJson.HashCanonical(candidate.IntentJson) != candidate.IntentHash)
                return null;
            var intent = document.RootElement.Deserialize<SwapIntent>();
            if (intent is null || intent.Version != 1 || intent.Terms is null ||
                intent.Terms.ActorUserId == Guid.Empty || intent.Terms.TenantId == Guid.Empty ||
                intent.Terms.ActorUserId.ToString("N") != candidate.ActorUserId ||
                (intent.Terms.TenantId?.ToString("N") ?? "") != candidate.TenantId ||
                intent.OperationId != candidate.Id || intent.QuoteId != candidate.QuoteId ||
                intent.QuoteHash != candidate.QuoteHash || intent.RequestHash != candidate.RequestHash ||
                SwapOperationIdentity.Operation(intent.Terms.ActorUserId, intent.Terms.TenantId, candidate.RequestKeyHash) != candidate.Id ||
                CanonicalJson.Hash(intent.Terms) != candidate.QuoteHash)
                return null;
            return intent;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

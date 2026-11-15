using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

public sealed record FiatPayoutKeyResolution(bool Voided, FiatPayout? Row);

public sealed partial class SurrealFiatPayoutLedger
{
    private const string VoidedCode = "REQUEST_VOIDED";
    private const string KeyInUseCode = "REQUEST_KEY_IN_USE";

    private async Task<AZOAResult<FiatPayoutClaim>> ClaimFencedAsync(FiatPayout candidate, CancellationToken ct)
    {
        var fence = new FiatPayoutRequestFence
        {
            Id = candidate.Id, SubjectUserId = candidate.SubjectUserId, TenantId = candidate.TenantId,
            RequestKey = candidate.RequestKey, Environment = candidate.Environment, ChainNetwork = candidate.ChainNetwork,
            Verdict = "admitted", CreatedAt = candidate.RequestedAt,
        };
        var prior = await GetAsync(candidate.Id, ct);
        if (prior.Result is { } existing) return AZOAResult<FiatPayoutClaim>.Success(new(existing, false));
        // raw: admission and its payout share one create-only fence; a void winner makes all future admission impossible.
        var batch = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"),
            SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(fence), "payout_admit_fence"),
            SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(candidate), "payout_admit_row"), SurrealQuery.Of("COMMIT"));
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(batch, ct); response.EnsureAllOk(); return true;
            }, ct);
            return AZOAResult<FiatPayoutClaim>.Success(new(candidate, true));
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var read = await GetAsync(candidate.Id, ct);
            if (read.Result is { } row) return AZOAResult<FiatPayoutClaim>.Success(new(row, false));
            var saved = await executor.QuerySingleAsync<FiatPayoutRequestFence>(SurrealQuery<FiatPayoutRequestFence>.Key(candidate.Id), ct);
            if (saved?.Verdict == "voided")
                return AZOAResult<FiatPayoutClaim>.FailureWithCode("This request was permanently discarded. Nothing can be sent under it.",
                    saved.SubjectUserId == candidate.SubjectUserId && saved.TenantId == candidate.TenantId ? VoidedCode : KeyInUseCode);
            throw;
        }
    }

    /// <summary>Creates a permanent absent-key tombstone or reveals the original payout, never cancels an admitted saga.</summary>
    public async Task<AZOAResult<FiatPayoutKeyResolution>> VoidAsync(FiatPayoutRequestFence candidate, CancellationToken ct = default)
    {
        if (candidate.Verdict != "voided" || !Guid.TryParse(SurrealLink.FromLink(candidate.SubjectUserId), out var subject)
            || candidate.Id != FiatPayout.RecordIdFor(subject, candidate.RequestKey, candidate.Environment, candidate.ChainNetwork))
            return AZOAResult<FiatPayoutKeyResolution>.FailureWithCode("The cash-out request identity is invalid.", AzoaErrorCodes.InvalidRequest);
        var existing = await GetAsync(candidate.Id, ct);
        if (existing.Result is { } row) return Reveal(row, candidate);
        // raw: the shared create-only fence and absence read linearize void against every new admission.
        var batch = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"),
            SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(candidate), "payout_void_fence"),
            SurrealGeneratedStatementScope.AssignRecord(SurrealQuery<FiatPayout>.Key(candidate.Id), "payout_void_row", "_payout_void_row"),
            SurrealQuery.Of("IF $_payout_void_row != NONE AND $_payout_void_row != [] { THROW 'PAYOUT_ALREADY_ADMITTED' }"),
            SurrealQuery.Of("COMMIT"));
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(batch, ct); response.EnsureAllOk(); return true;
            }, ct);
            return AZOAResult<FiatPayoutKeyResolution>.Success(new(true, null));
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains("PAYOUT_ALREADY_ADMITTED", StringComparison.Ordinal))
        {
            var read = await GetAsync(candidate.Id, ct);
            if (read.Result is { } admitted) return Reveal(admitted, candidate);
            var saved = await executor.QuerySingleAsync<FiatPayoutRequestFence>(SurrealQuery<FiatPayoutRequestFence>.Key(candidate.Id), ct);
            if (saved is { Verdict: "voided" } && saved.SubjectUserId == candidate.SubjectUserId && saved.TenantId == candidate.TenantId
                && saved.RequestKey == candidate.RequestKey && saved.Environment == candidate.Environment && saved.ChainNetwork == candidate.ChainNetwork)
                return AZOAResult<FiatPayoutKeyResolution>.Success(new(true, null));
            return AZOAResult<FiatPayoutKeyResolution>.FailureWithCode("That request key is already in use.", KeyInUseCode);
        }
    }

    private static AZOAResult<FiatPayoutKeyResolution> Reveal(FiatPayout row, FiatPayoutRequestFence candidate)
        => row.SubjectUserId == candidate.SubjectUserId && row.TenantId == candidate.TenantId
            && row.RequestKey == candidate.RequestKey && row.Environment == candidate.Environment && row.ChainNetwork == candidate.ChainNetwork
            ? AZOAResult<FiatPayoutKeyResolution>.Success(new(false, row))
            : AZOAResult<FiatPayoutKeyResolution>.FailureWithCode("That request key is already in use.", KeyInUseCode);
}

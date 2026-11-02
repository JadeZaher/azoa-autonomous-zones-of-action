using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

public sealed record CashOutVoidView(string Outcome, CashOutView? CashOut = null);

public sealed partial class OffRampService
{
    /// <summary>Proves an absent request permanently void, or reveals the admitted cash-out without altering it.</summary>
    public async Task<AZOAResult<CashOutVoidView>> VoidAsync(Guid subjectUserId, Guid? tenantId, string requestKey, CancellationToken ct = default)
    {
        if (!_options.RequestVoidEnabled)
            return AZOAResult<CashOutVoidView>.FailureWithCode("Discarding an unresolved cash-out is not enabled yet. Keep its request key.", AzoaErrorCodes.DependencyUnavailable);
        if (subjectUserId == Guid.Empty || !ValidCashOutKey(requestKey))
            return AZOAResult<CashOutVoidView>.FailureWithCode("Use a valid cash-out request key.", AzoaErrorCodes.InvalidRequest);
        var candidate = new FiatPayoutRequestFence
        {
            Id = FiatPayout.RecordIdFor(subjectUserId, requestKey, _fiat.ActiveEnvironment.Name, _providerFactory.DefaultNetwork.ToString()),
            SubjectUserId = SurrealLink.ToLink("user", subjectUserId.ToString("N"))!,
            TenantId = tenantId is { } tenant ? SurrealLink.ToLink("user", tenant.ToString("N")) : null,
            RequestKey = requestKey.Trim(), Environment = _fiat.ActiveEnvironment.Name,
            ChainNetwork = _providerFactory.DefaultNetwork.ToString(), Verdict = "voided", CreatedAt = _clock.GetUtcNow(),
        };
        var result = await _payouts.VoidAsync(candidate, ct);
        if (result.IsError || result.Result is null)
            return AZOAResult<CashOutVoidView>.FailureWithCode("This request could not be resolved. Keep it and retry.", result.Code ?? AzoaErrorCodes.DependencyUnavailable);
        return result.Result.Voided
            ? AZOAResult<CashOutVoidView>.Success(new("voided"))
            : result.Result.Row is { } row && SubjectOf(row) == subjectUserId && DroveBy(row, tenantId)
                ? AZOAResult<CashOutVoidView>.Success(new("revealed", CashOutView.From(row)))
                : AZOAResult<CashOutVoidView>.FailureWithCode("This request could not be resolved. Keep it and retry.", AzoaErrorCodes.Conflict);
    }

    private static bool ValidCashOutKey(string? key)
        => key is { Length: > 0 and <= 200 } && key is not ("." or "..") && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
}

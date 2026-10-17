namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>Uses connected-account payout events as triggers for authoritative reconciliation.</summary>
public sealed class BankPayoutEventHandler(IOffRamp offRamp) : IFiatEventHandler
{
    /// <inheritdoc />
    public string Name => "fiat-bank-payout";

    /// <inheritdoc />
    public bool Handles(string eventType)
        => eventType is "payout.created" or "payout.updated" or "payout.paid" or "payout.failed" or "payout.canceled";

    /// <inheritdoc />
    public Task<FiatEventOutcome> ApplyAsync(FiatEventContext context, CancellationToken ct = default)
        => context.IntentId is { } payoutId
            && !string.IsNullOrWhiteSpace(context.ProviderAccountId)
            && !string.IsNullOrWhiteSpace(context.ProviderObjectId)
                ? offRamp.ReconcileBankPayoutAsync(
                    payoutId.ToString("N"), context.ProviderAccountId, context.ProviderObjectId, ct)
                : Task.FromResult(FiatEventOutcome.Ignored);
}

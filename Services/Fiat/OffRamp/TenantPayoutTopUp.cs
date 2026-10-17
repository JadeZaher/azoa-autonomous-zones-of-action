// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat.Reversal;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>D-8 tenant top-ups ride the existing Checkout rail; this is their server-side identity.</summary>
public static class TenantPayoutTopUp
{
    /// <summary>Checkout purpose that credits a tenant payout float (must be in <c>Fiat:Checkout:AllowedPurposes</c>).</summary>
    public const string Purpose = "tenant-payout-float";

    private const string AdmissionPrefix = "payout-float:";

    /// <summary>
    /// Server-built admission key <c>payout-float:{tenant}:{key}</c>. Ordinary checkout keys start with the
    /// subject's 32-hex id, so this prefix can never be produced by a caller-chosen request key.
    /// </summary>
    public static string AdmissionKeyFor(Guid tenantId, string requestKey) => $"{AdmissionPrefix}{tenantId:N}:{requestKey.Trim()}";

    /// <summary>True when a hosted session was claimed as a tenant top-up.</summary>
    public static bool IsTopUpAdmission(string? admissionKey)
        => admissionKey?.StartsWith(AdmissionPrefix, StringComparison.Ordinal) == true;

    /// <summary>The tenant named in a top-up admission key.</summary>
    public static bool TryReadTenant(string? admissionKey, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        if (!IsTopUpAdmission(admissionKey))
            return false;
        var rest = admissionKey![AdmissionPrefix.Length..];
        var separator = rest.IndexOf(':');
        return separator == 32 && Guid.TryParseExact(rest[..separator], "N", out tenantId) && tenantId != Guid.Empty;
    }
}

/// <summary>
/// Credits a settled tenant top-up to its float as a Pending credit, net of Stripe fees, maturing after the
/// rail's reversal window. Bookkeeping only for units, so it is an <see cref="IFiatEventHandler"/>, never an
/// <see cref="IFiatSettlementHandler"/>.
/// </summary>
public sealed class TenantPayoutTopUpHandler(
    FiatOptions fiat,
    IHostedSessionStore sessions,
    IStripeGateway gateway,
    IReversalFactSource facts,
    IValueRailProviderFactory rails,
    ITenantPayoutLedger ledger,
    ITenantPayoutFunding funding,
    TimeProvider clock,
    ILogger<TenantPayoutTopUpHandler> logger) : IFiatEventHandler
{
    private static readonly string[] HandledEventTypes =
        ["checkout.session.completed", "checkout.session.async_payment_succeeded"];

    /// <inheritdoc />
    public string Name => "tenant-payout-top-up";

    /// <inheritdoc />
    public bool Handles(string eventType) => HandledEventTypes.Contains(eventType, StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<FiatEventOutcome> ApplyAsync(FiatEventContext context, CancellationToken ct = default)
    {
        if (context.IntentId is not { } intentId)
            return FiatEventOutcome.Ignored;
        var read = await sessions.GetByIdAsync(intentId, ct);
        if (read.IsError)
            return FiatEventOutcome.Failed;
        if (read.Result is not { } intent || intent.FlowKind != HostedSessionFlowKinds.FiatCheckout
            || !TenantPayoutTopUp.IsTopUpAdmission(intent.AdmissionKey))
            return FiatEventOutcome.Ignored;

        // The tenant is bound from the server-recorded intent (admission key + subject), never from metadata.
        if (!TenantPayoutTopUp.TryReadTenant(intent.AdmissionKey, out var tenant)
            || SurrealRecordGuid.ParseOptional(intent.SubjectUserId) != tenant
            || string.IsNullOrWhiteSpace(intent.ProviderSessionId))
        {
            logger.LogError("Top-up intent {IntentId} does not bind one tenant; nothing was credited.", intent.Id);
            return FiatEventOutcome.Failed;
        }

        var credentials = fiat.CredentialsFor(context.Environment);
        var fetched = await gateway.GetCheckoutSessionAsync(credentials, intent.ProviderSessionId!, ct);
        if (fetched.IsError || fetched.Result is not { } session)
            return FiatEventOutcome.Failed;
        if (session.ClientReferenceId != intent.Id
            || session.Livemode != (context.Environment.Kind == RailEnvironmentKind.Live)
            || string.IsNullOrWhiteSpace(session.PaymentIntentId)
            || session.AmountTotalMinorUnits is not > 0)
            return FiatEventOutcome.Failed;
        if (FiatRailProvider.ClassifySession(session) != ChainConfirmation.Confirmed)
            return FiatEventOutcome.AcceptedPending;

        // Owner decision (a): the float is credited with balance_transaction.net (the tenant bears fees).
        var balance = await facts.GetPaymentBalanceAsync(credentials, session.PaymentIntentId!, ct);
        if (balance.IsError || balance.Result is not { } settled
            || !string.Equals(settled.Currency, session.Currency, StringComparison.OrdinalIgnoreCase)
            || settled.AmountMinorUnits != session.AmountTotalMinorUnits || settled.Livemode != session.Livemode)
            return FiatEventOutcome.Failed;

        var now = clock.GetUtcNow();
        var floatRow = await ledger.GetOrCreateFloatAsync(tenant, settled.Currency, context.Environment.Name, ct);
        await funding.RecordTopUpAsync(new TenantPayoutCredit
        {
            Id = TenantPayoutCredit.RecordIdFor(intent.Id),
            IntentId = intent.Id,
            FloatId = floatRow.Id,
            TenantId = floatRow.TenantId,
            CurrencyCode = floatRow.CurrencyCode,
            Environment = floatRow.Environment,
            PaymentIntentId = session.PaymentIntentId!,
            ChargeId = settled.ChargeId,
            GrossMinorUnits = settled.AmountMinorUnits,
            NetMinorUnits = settled.NetMinorUnits,
            ReversedMinorUnits = 0,
            State = TenantPayoutCreditStates.Pending,
            AvailableAt = MaturesAt(context.Environment, now),
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);
        return FiatEventOutcome.Applied;
    }

    /// <summary>
    /// Critic rule 6: the rail's declared reversal window, and only when it covers the dispute horizon
    /// (<c>Fiat:ChargebackWindowDays</c>). Null means the credit never matures on its own.
    /// </summary>
    private DateTimeOffset? MaturesAt(RailEnvironment environment, DateTimeOffset settledAt)
    {
        if (!rails.TryGetRailProvider(FiatRailIds.UsdRail, environment, out var rail)
            || !rail.SettlementProfile.TryGetReleaseTimeUtc(settledAt, out var releaseAt))
            return null;
        return releaseAt - settledAt >= TimeSpan.FromDays(Math.Max(1, fiat.ChargebackWindowDays)) ? releaseAt : null;
    }
}

/// <summary>Re-reads a reversed top-up charge and sets its credit's authoritative reversed total.</summary>
public sealed class TenantPayoutTopUpReversalHandler(
    FiatOptions fiat,
    IReversalFactSource facts,
    ITenantPayoutLedger ledger,
    ITenantPayoutFunding funding) : IFiatEventHandler
{
    /// <inheritdoc />
    public string Name => "tenant-payout-top-up-reversal";

    /// <inheritdoc />
    public bool Handles(string eventType) => FiatReversalEventTypes.IsHandled(eventType);

    /// <inheritdoc />
    public async Task<FiatEventOutcome> ApplyAsync(FiatEventContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context.PaymentIntentId))
            return FiatEventOutcome.Ignored;
        var credit = await ledger.GetCreditByPaymentAsync(context.PaymentIntentId, ct);
        if (credit is null || credit.ChargeId is null)
            return FiatEventOutcome.Ignored;

        var credentials = fiat.CredentialsFor(context.Environment);
        var charge = await facts.GetChargeAsync(credentials, credit.ChargeId, ct);
        if (charge.IsError || charge.Result is not { } current || current.PaymentIntentId != credit.PaymentIntentId)
            return FiatEventOutcome.Failed;

        // Refunds return the gross; a withdrawn dispute costs its amount plus its fee (owner decision a).
        var reversed = current.AmountRefundedMinorUnits;
        if (current.Disputed)
        {
            var dispute = await facts.GetDisputeForPaymentAsync(credentials, credit.PaymentIntentId, ct);
            if (dispute.IsError || dispute.Result is not { } standing)
                return FiatEventOutcome.Failed;
            if (standing.FundsWithdrawn)
                reversed += -standing.NetBalanceImpactMinorUnits;
        }

        return await funding.ApplyCreditReversalAsync(credit.Id, reversed, ct) == TenantFundingSync.Retry
            ? FiatEventOutcome.Failed
            : FiatEventOutcome.Applied;
    }
}

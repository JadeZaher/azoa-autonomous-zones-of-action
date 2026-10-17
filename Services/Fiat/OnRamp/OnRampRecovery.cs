using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using SurrealForge.Client;

namespace AZOA.WebAPI.Services.Fiat.OnRamp;

/// <summary>Audited recovery of uncredited payments with mutually exclusive issuance and refunds.</summary>
public sealed class OnRampRecovery(IOnRampLotLedger lots, IIdempotencyStore idempotency,
    IStripeGateway gateway, IStripeRefundGateway refunds, FiatOptions options,
    OnRampSettlementHandler settlement, TimeProvider clock)
{
    public const string RefundReservationType = SurrealIdempotencyStore.RefundReservationType;

    public static string IssuanceLedgerKey(string intentId)
        => AllocationIdempotency.CreateFromClientKey(OnRampSystemIdentity.SettlementAuthorityId,
            OnRampSettlementHandler.IssuanceIdempotencyKey(intentId)).LedgerKey;

    public async Task<AZOAResult<OnRampLot>> ResumeAsync(Guid intentId, Guid actorId, string reason, CancellationToken ct)
    {
        if (!ValidAction(actorId, reason)) return AZOAResult<OnRampLot>.Failure("An operator and a reason of 1–512 characters are required.");
        if (!options.Enabled) return AZOAResult<OnRampLot>.Failure("The payment rail is disabled.");
        var key = intentId.ToString("N");
        var read = await lots.GetByIntentAsync(key, ct);
        if (read.IsError || read.Result is not { } row) return AZOAResult<OnRampLot>.Failure("The lot could not be read.");
        if (row.Environment != options.ActiveEnvironment.Name) return AZOAResult<OnRampLot>.Failure("This lot belongs to a different payment environment.");
        if (row.Status == OnRampLotStatuses.Issued) return AZOAResult<OnRampLot>.Success(row);
        if (row.Status is not (OnRampLotStatuses.Refused or OnRampLotStatuses.Stuck or OnRampLotStatuses.Pending))
            return AZOAResult<OnRampLot>.Failure("This lot cannot resume issuance.");
        var claim = await idempotency.GetAsync(IssuanceLedgerKey(key), ct);
        if (claim?.OperationType == RefundReservationType)
            return AZOAResult<OnRampLot>.Failure("A refund owns this payment; issuance cannot resume.");
        if (row.Status != OnRampLotStatuses.Pending)
        {
            Audit(row, actorId, "resume", reason);
            row.Status = OnRampLotStatuses.Pending;
            row.AttemptCount = 0;
            row.StatusReason = null;
            var saved = await SaveAsync(row, ct);
            if (saved.IsError) return saved;
            row = saved.Result!;
        }
        var outcome = await settlement.ApplyAsync(new FiatEventContext(new FiatEvent(),
            "checkout.session.completed", row.SourceCheckoutSessionId, row.SourcePaymentIntentId,
            row.SourceChargeId, intentId, Guid.Parse(SurrealLink.FromLink(row.SubjectUserId)!), options.ActiveEnvironment), ct);
        var current = await lots.GetByIntentAsync(key, ct);
        return current.Result is null ? AZOAResult<OnRampLot>.Failure("The recovery outcome could not be read.")
            : AZOAResult<OnRampLot>.Success(current.Result, outcome.ToString());
    }

    public async Task<AZOAResult<OnRampLot>> RefundAsync(Guid intentId, Guid actorId, string reason, CancellationToken ct,
        string? providerRefundId = null, bool retryFailed = false, long? expectedRefundAttempt = null)
    {
        if (!ValidAction(actorId, reason)) return AZOAResult<OnRampLot>.Failure("An operator and a reason of 1–512 characters are required.");
        if (!options.Enabled) return AZOAResult<OnRampLot>.Failure("The payment rail is disabled.");
        var key = intentId.ToString("N");
        var read = await lots.GetByIntentAsync(key, ct);
        if (read.IsError || read.Result is not { } row) return AZOAResult<OnRampLot>.Failure("The lot could not be read.");
        if (row.Environment != options.ActiveEnvironment.Name) return AZOAResult<OnRampLot>.Failure("This lot belongs to a different payment environment.");
        if (row.Status == OnRampLotStatuses.Refunded) return AZOAResult<OnRampLot>.Success(row);
        if (row.Status is not (OnRampLotStatuses.Refused or OnRampLotStatuses.Stuck or OnRampLotStatuses.Pending or OnRampLotStatuses.RefundPending)
            || !string.IsNullOrWhiteSpace(row.OperationId) || row.IssuedAt is not null)
            return AZOAResult<OnRampLot>.Failure("Issued units must be reconciled before this payment can be refunded.");
        var claim = await idempotency.TryClaimAsync(IssuanceLedgerKey(key), RefundReservationType, ct);
        if (!claim.Won && claim.Record.OperationType != RefundReservationType)
        {
            var reserved = idempotency is IAllocationRecoveryStore recovery
                && await recovery.TryReserveRefundAfterNoEffectAsync(IssuanceLedgerKey(key), ct);
            if (!reserved)
                return AZOAResult<OnRampLot>.Failure("An issuance has started or has an uncertain outcome. Reconcile its durable allocation before refunding.");
        }
        if (claim.Won)
            await idempotency.FailAsync(IssuanceLedgerKey(key), "Issuance permanently reserved for an operator refund.", ct);
        if (row.Status != OnRampLotStatuses.RefundPending)
        {
            Audit(row, actorId, "refund", reason);
            row.Status = OnRampLotStatuses.RefundPending;
            row.StatusReason = "Refund requested; provider confirmation is pending.";
            var saved = await SaveAsync(row, ct);
            if (saved.IsError) return saved;
            row = saved.Result!;
        }
        if (!string.IsNullOrWhiteSpace(providerRefundId))
            Audit(row, actorId, "reconcile-refund-reference", reason);
        if (retryFailed)
        {
            if (row.ProviderRefundId is null || row.RefundedMinorUnits != 0 || expectedRefundAttempt != row.RefundAttempt)
                return AZOAResult<OnRampLot>.Failure("Only a known terminal failed refund can start a new attempt.");
            var failed = await refunds.GetRefundAsync(options.CredentialsFor(options.ActiveEnvironment), row.ProviderRefundId, ct);
            if (failed.IsError || failed.Result is not { } proof || proof.Id != row.ProviderRefundId
                || proof.PaymentIntentId != row.SourcePaymentIntentId || proof.IntentId != key
                || proof.Amount != row.FiatMinorUnits || !string.Equals(proof.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase)
                || proof.Status is not ("failed" or "canceled"))
                return AZOAResult<OnRampLot>.Failure("The previous refund is not authoritatively failed; a new attempt is forbidden.");
            Audit(row, actorId, "retry-failed-refund", reason);
            row.RefundAttempt++;
            row.ProviderRefundId = null;
            row.ProviderRefundStatus = null;
            row.RefundStartedAt = null;
            var saved = await SaveAsync(row, ct);
            if (saved.IsError) return saved;
            row = saved.Result!;
            providerRefundId = null;
        }
        return await ReconcileAsync(row, ct, providerRefundId);
    }

    public async Task<AZOAResult<OnRampLot>> ReconcileAsync(OnRampLot row, CancellationToken ct, string? providerRefundId = null)
    {
        if (!options.Enabled || row.Status != OnRampLotStatuses.RefundPending || row.Environment != options.ActiveEnvironment.Name)
            return AZOAResult<OnRampLot>.Failure("This refund is not pending.");
        var intent = SurrealLink.FromLink(row.IntentId)!;
        var reservation = await idempotency.GetAsync(IssuanceLedgerKey(intent), ct);
        if (reservation?.OperationType != RefundReservationType)
            return AZOAResult<OnRampLot>.Failure("The refund has no issuance exclusion reservation.");
        var credentials = options.CredentialsFor(options.ActiveEnvironment);
        var fetched = await gateway.GetCheckoutSessionAsync(credentials, row.SourceCheckoutSessionId ?? string.Empty, ct);
        if (fetched.IsError || fetched.Result is not { } session)
            return AZOAResult<OnRampLot>.Failure("The original payment could not be verified.");
        if (session.SessionId != row.SourceCheckoutSessionId || session.ClientReferenceId != intent
            || session.Livemode != (options.ActiveEnvironment.Kind == RailEnvironmentKind.Live)
            || session.PaymentStatus != "paid" || session.AmountTotalMinorUnits != row.FiatMinorUnits
            || !string.Equals(session.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(session.PaymentIntentId)
            || (row.SourcePaymentIntentId is { } knownPayment && knownPayment != session.PaymentIntentId))
            return AZOAResult<OnRampLot>.Failure("The original payment does not match this lot.");
        if (row.ProviderRefundId is null && providerRefundId is null && row.RefundStartedAt is { } started
            && clock.GetUtcNow() - started >= TimeSpan.FromHours(23))
            return AZOAResult<OnRampLot>.Failure("The refund outcome is unknown beyond its safe retry window; reconcile its provider reference.");
        if (row.RefundStartedAt is null)
        {
            row.RefundStartedAt = clock.GetUtcNow();
            row.SourcePaymentIntentId = session.PaymentIntentId;
            var saved = await SaveAsync(row, ct);
            if (saved.IsError) return saved;
            row = saved.Result!;
        }
        var refunded = (row.ProviderRefundId ?? providerRefundId) is { } refundId
            ? await refunds.GetRefundAsync(credentials, refundId, ct)
            : await refunds.CreateRefundAsync(credentials, session.PaymentIntentId!, row.FiatMinorUnits,
                intent, $"azoa-on-ramp-refund-{intent}-{row.RefundAttempt}", ct);
        if (refunded.IsError || refunded.Result is not { } refund)
            return AZOAResult<OnRampLot>.Failure("The refund is awaiting a confirmed provider outcome.");
        if (string.IsNullOrWhiteSpace(refund.Id) || refund.IntentId != intent || refund.PaymentIntentId != session.PaymentIntentId
            || refund.Amount != row.FiatMinorUnits || !string.Equals(refund.Currency, row.CurrencyCode, StringComparison.OrdinalIgnoreCase)
            || (row.ProviderRefundId is { } expected && expected != refund.Id))
            return AZOAResult<OnRampLot>.Failure("The refund facts do not match this payment.");
        row.ProviderRefundId = refund.Id;
        row.ProviderRefundStatus = refund.Status;
        if (refund.Status == "succeeded") row.RefundedMinorUnits = Math.Max(row.RefundedMinorUnits, refund.Amount);
        if (row.RefundedMinorUnits > row.FiatMinorUnits)
            return AZOAResult<OnRampLot>.Failure("Refund evidence exceeds the original payment.");
        row.Status = refund.Status == "succeeded" ? OnRampLotStatuses.Refunded : OnRampLotStatuses.RefundPending;
        row.StatusReason = refund.Status switch
        {
            "succeeded" => "The original payment was refunded; no units were issued.",
            "pending" or "requires_action" => "The provider is still processing the refund.",
            "failed" or "canceled" => "The refund did not complete; reconcile the provider refund before further action.",
            _ => "The provider returned an unrecognized refund state; operator reconciliation is required.",
        };
        return await SaveAsync(row, ct);
    }

    private void Audit(OnRampLot row, Guid actor, string action, string reason)
    {
        var entries = JsonSerializer.Deserialize<List<RecoveryAudit>>(row.RecoveryAuditJson) ?? [];
        entries.Add(new(actor, action, reason.Trim(), clock.GetUtcNow(), row.Status));
        row.RecoveryAuditJson = JsonSerializer.Serialize(entries);
    }

    private async Task<AZOAResult<OnRampLot>> SaveAsync(OnRampLot row, CancellationToken ct)
    {
        var expected = row.Version++;
        row.UpdatedAt = clock.GetUtcNow();
        return await lots.UpdateIfVersionAsync(row, expected, ct);
    }

    private static bool ValidAction(Guid actor, string reason)
        => actor != Guid.Empty && !string.IsNullOrWhiteSpace(reason) && reason.Length <= 512;

    private sealed record RecoveryAudit(Guid ActorId, string Action, string Reason, DateTimeOffset At, string PriorStatus);
}

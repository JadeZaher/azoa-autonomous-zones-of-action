// SPDX-License-Identifier: UNLICENSED

using System.Text.Json.Serialization;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Fiat;

/// <summary>The two 200 answers a request-key void can give. Wire values; never rename.</summary>
public static class FiatVoidOutcomes
{
    /// <summary>Nothing was or can be paid under the key; the caller may discard it.</summary>
    public const string Voided = "voided";

    /// <summary>Something was or may be paid under the key; follow <see cref="FiatCheckoutVoidResult.Intent"/>.</summary>
    public const string Revealed = "revealed";
}

/// <summary>Body of a 200 from <c>POST /api/fiat/checkout/{userId}/requests/{requestKey}/void</c>, and (<c>revealed</c>) the <c>result</c> of its 409 INTENT_NEEDS_REVIEW.</summary>
public sealed class FiatCheckoutVoidResult
{
    /// <summary>One of <see cref="FiatVoidOutcomes"/>.</summary>
    public string Outcome { get; init; } = FiatVoidOutcomes.Voided;

    /// <summary>The intent admitted under the key; null when nothing ever was.</summary>
    public Guid? IntentId { get; init; }

    /// <summary>On <c>revealed</c> only: the same projection <c>GET .../{intentId}</c> returns.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FiatCheckoutIntent? Intent { get; init; }
}

/// <summary>Durable markers and keys of the request-key void; see <c>Providers/Fiat/AGENTS.md</c> §request-void.</summary>
public static class FiatCheckoutVoid
{
    /// <summary><c>authoritative_outcome</c> on every voided row (tombstone or abandoned intent).</summary>
    public const string VoidedOutcome = "VOIDED";

    /// <summary><c>outcome_detail</c> of a tombstone: the key was voided before anything was admitted.</summary>
    public const string VoidedBeforeAdmission = "VOIDED_BEFORE_ADMISSION";

    /// <summary><c>outcome_detail</c> of an intent abandoned by a void.</summary>
    public const string VoidedAfterAdmission = "VOIDED_AFTER_ADMISSION";

    /// <summary>Prefix of the intent-derived Stripe idempotency key for the Checkout Session expire.</summary>
    public const string ExpireIdempotencyKeyPrefix = "azoa-fiat-checkout-expire-";

    /// <summary>True for a row a void closed; no begin may resume it.</summary>
    public static bool IsVoided(HostedSession session) =>
        string.Equals(session.State, HostedSessionStates.Abandoned, StringComparison.Ordinal)
        && string.Equals(session.AuthoritativeOutcome, VoidedOutcome, StringComparison.Ordinal);

    /// <summary>True for a void's own placeholder row: no intent ever existed under the key.</summary>
    public static bool IsTombstone(HostedSession session) =>
        IsVoided(session) && string.Equals(session.OutcomeDetail, VoidedBeforeAdmission, StringComparison.Ordinal);

    /// <summary>One key per (intent, Stripe session), so every retried expire replays the same request at Stripe.</summary>
    public static string ExpireIdempotencyKey(string intentId, string providerSessionId) =>
        ExpireIdempotencyKeyPrefix + intentId + "-" + providerSessionId;
}

public sealed partial class FiatRailProvider
{
    /// <summary>Re-reads after a lost version CAS before the void answers busy.</summary>
    private const int VoidAttempts = 4;

    /// <inheritdoc />
    public async Task<AZOAResult<FiatCheckoutVoidResult>> VoidRequestAsync(
        Guid subjectUserId, Guid? tenantId, string admissionKey, CancellationToken ct = default)
    {
        if (!GetCapabilities().Available)
            return FiatConsumerErrors.PaymentsNotConfigured<FiatCheckoutVoidResult>();
        if (subjectUserId == Guid.Empty)
            return AZOAResult<FiatCheckoutVoidResult>.FailureWithCode(
                "An authenticated account is required.", AzoaErrorCodes.InvalidRequest);
        if (string.IsNullOrWhiteSpace(admissionKey))
            return AZOAResult<FiatCheckoutVoidResult>.FailureWithCode(
                "A stable request key is required.", AzoaErrorCodes.InvalidRequest);

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostedSessionStore>();
        var now = _clock.GetUtcNow();

        // The tombstone IS the admission candidate: the same create-if-absent claim begin makes, on the
        // same unique (flow, provider, admission key), so a void and a begin racing on an unclaimed key
        // serialize on one row. Whoever creates it decides what the key is.
        var tombstone = new HostedSession
        {
            Id = Guid.NewGuid().ToString("N"),
            FlowKind = HostedSessionFlowKinds.FiatCheckout,
            ProviderKey = FiatRailIds.StripeProviderKey,
            AdmissionKey = admissionKey.Trim(),
            SubjectUserId = subjectUserId.ToString("N"),
            TenantId = tenantId?.ToString("N"),
            State = HostedSessionStates.Abandoned,
            StateVersion = 0,
            AuthoritativeOutcome = FiatCheckoutVoid.VoidedOutcome,
            OutcomeDetail = FiatCheckoutVoid.VoidedBeforeAdmission,
            CreatedDate = now,
            ModifiedDate = now,
            CompletedAt = now,
        };

        var claimed = await store.ClaimAdmissionAsync(tombstone, ct);
        if (claimed.IsError || claimed.Result is null)
            return VoidBusy();

        var session = claimed.Result;
        if (string.Equals(session.Id, tombstone.Id, StringComparison.Ordinal))
        {
            _logger.LogInformation("A checkout request key was voided before anything was admitted under it.");
            return Voided(intentId: null);
        }

        if (!OwnedBy(session, subjectUserId) || !DrivenBy(session, tenantId))
        {
            _logger.LogWarning("A checkout void named an admission key claimed by another account.");
            return AZOAResult<FiatCheckoutVoidResult>.FailureWithCode(
                "That request key is already in use.", FiatConsumerErrors.RequestKeyInUseCode);
        }

        if (!TryParseIntentId(session.Id, out var intentId))
            return VoidBusy();

        for (var attempt = 0; attempt < VoidAttempts; attempt++)
        {
            var answer = await TryVoidAdmittedAsync(scope, store, session, intentId, ct);
            if (answer is not null)
                return answer;

            // Lost the version CAS to a begin, a webhook or another void: decide again on the new row.
            var reread = await store.GetByIdAsync(intentId, ct);
            if (reread.IsError || reread.Result is null)
                return VoidBusy();
            session = reread.Result;
        }

        return VoidBusy();
    }

    /// <summary>
    /// Decides one admitted row. Null means a version CAS was lost and the row must be re-read.
    /// Voids only when this node can prove nothing was or can be paid; see the decision table in
    /// <c>Providers/Fiat/AGENTS.md</c> §request-void.
    /// </summary>
    private async Task<AZOAResult<FiatCheckoutVoidResult>?> TryVoidAdmittedAsync(
        IServiceScope scope, IHostedSessionStore store, HostedSession session, Guid intentId, CancellationToken ct)
    {
        if (FiatCheckoutVoid.IsVoided(session))
            return Voided(FiatCheckoutVoid.IsTombstone(session) ? null : intentId);

        // Finished, failed, expired, clearing or settling: the projection, never a void.
        if (HostedSessionStates.IsTerminal(session.State)
            || session.State is HostedSessionStates.AwaitingOutcome or HostedSessionStates.Resuming)
            return Revealed(await ProjectAsync(scope, session, resumed: true, ct));

        if (string.IsNullOrWhiteSpace(session.ProviderSessionId))
        {
            if (!string.Equals(session.State, HostedSessionStates.Claimed, StringComparison.Ordinal)
                || FiatHostedCreation.IsUnreplayable(session, _clock.GetUtcNow()))
                return await NeedsReviewAsync(scope, session, ct);

            // No page attached: a page URL leaves this node only after the attach CAS, and that CAS
            // needs this row CLAIMED at its current version -- which the abandon below takes away.
            return await AbandonAsync(store, session, intentId, ct);
        }

        var read = await ReadProviderSessionAsync(session, ct);
        if (read.IsError || read.Result is null)
            return Unconfirmed(read.Message);

        // The same expire-first classifier the same-key resume uses (§stripe-session-verdict).
        var settled = await SettleCheckoutSessionAsync(session, read.Result, ct);
        return settled.Verdict switch
        {
            CheckoutSessionVerdict.Dead => await AbandonAsync(store, session, intentId, ct),
            CheckoutSessionVerdict.PaidOrClearing => Revealed(await ProjectAsync(scope, session, settled.Provider, resumed: true, ct)),
            _ => Unconfirmed(settled.Message),
        };
    }

    /// <summary>The void's only write: a state-version CAS to ABANDONED, so exactly one of it and begin's fence/attach lands.</summary>
    private async Task<AZOAResult<FiatCheckoutVoidResult>?> AbandonAsync(
        IHostedSessionStore store, HostedSession session, Guid intentId, CancellationToken ct)
    {
        var abandoned = await store.TryTransitionAsync(
            intentId,
            session.StateVersion,
            HostedSessionStates.Abandoned,
            FiatCheckoutVoid.VoidedOutcome,
            FiatCheckoutVoid.VoidedAfterAdmission,
            resumeTargetId: null,
            ct);
        if (abandoned.IsError)
            return VoidBusy();
        if (abandoned.Result is null)
            return null;

        _logger.LogInformation("Funding intent {IntentId} was voided by its request key.", session.Id);
        return Voided(intentId);
    }

    /// <summary>Begin's cleanup when a void abandoned its claim mid-create: expire the page nobody was handed.</summary>
    private async Task ExpireOrphanedPageAsync(string intentId, string providerSessionId, CancellationToken ct)
    {
        var expired = await _gateway.ExpireCheckoutSessionAsync(
            _options.CredentialsFor(_environment), providerSessionId,
            FiatCheckoutVoid.ExpireIdempotencyKey(intentId, providerSessionId), ct);
        if (expired.IsError)
            _logger.LogWarning(
                "Funding intent {IntentId} was voided mid-create; its unhanded payment page could not be expired and will lapse unpaid.",
                intentId);
    }

    private static AZOAResult<FiatCheckoutVoidResult> Voided(Guid? intentId) =>
        AZOAResult<FiatCheckoutVoidResult>.Success(
            new FiatCheckoutVoidResult { Outcome = FiatVoidOutcomes.Voided, IntentId = intentId },
            "Nothing can be paid under this request key.");

    private static AZOAResult<FiatCheckoutVoidResult> Revealed(AZOAResult<FiatCheckoutIntent> projection) =>
        projection.IsError || projection.Result is null
            ? AZOAResult<FiatCheckoutVoidResult>.FailureWithCode(
                projection.Message, projection.Code ?? AzoaErrorCodes.DependencyUnavailable)
            : AZOAResult<FiatCheckoutVoidResult>.Success(
                new FiatCheckoutVoidResult
                {
                    Outcome = FiatVoidOutcomes.Revealed,
                    IntentId = projection.Result.IntentId,
                    Intent = projection.Result,
                },
                "A payment under this request key may already have been made; follow it instead of starting a new one.");

    /// <summary>409 INTENT_NEEDS_REVIEW whose <c>result</c> reveals the intent (never <c>voided</c>), so the caller keeps the key and can name it.</summary>
    private async Task<AZOAResult<FiatCheckoutVoidResult>> NeedsReviewAsync(
        IServiceScope scope, HostedSession session, CancellationToken ct)
    {
        _logger.LogError(
            "Funding intent {IntentId} could not be voided: its unattached payment page needs operator reconciliation.",
            session.Id);
        var projection = await ProjectAsync(scope, session, provider: null, resumed: true, ct);
        return AZOAResult<FiatCheckoutVoidResult>.FailureWithCode(
            NeedsReviewMessage, FiatConsumerErrors.IntentNeedsReviewCode,
            projection.Result is { } intent
                ? new FiatCheckoutVoidResult { Outcome = FiatVoidOutcomes.Revealed, IntentId = intent.IntentId, Intent = intent }
                : null);
    }

    /// <summary>RAIL_BUSY without the begin path's "nothing was charged", which a contended void cannot prove.</summary>
    private static AZOAResult<FiatCheckoutVoidResult> VoidBusy() =>
        AZOAResult<FiatCheckoutVoidResult>.FailureWithCode(
            "This request could not be checked right now. Keep it and try again shortly.", FiatConsumerErrors.RailBusyCode);

    private static AZOAResult<FiatCheckoutVoidResult> Unconfirmed(string? message = null) =>
        AZOAResult<FiatCheckoutVoidResult>.FailureWithCode(
            string.IsNullOrWhiteSpace(message)
                ? "Your payment status could not be confirmed right now. Keep this request and try again shortly."
                : message,
            AzoaErrorCodes.DependencyUnavailable);
}

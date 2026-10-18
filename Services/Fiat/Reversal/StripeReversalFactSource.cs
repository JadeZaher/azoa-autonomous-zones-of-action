// SPDX-License-Identifier: UNLICENSED

using System.Net.Http.Headers;
using System.Text.Json;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Fiat;

namespace AZOA.WebAPI.Services.Fiat.Reversal;

/// <summary>The server-fetched facts about one charge. The ONLY source of reversal-amount truth.</summary>
/// <param name="ChargeId">Provider charge id.</param>
/// <param name="PaymentIntentId">Payment this charge belongs to, when the provider names one.</param>
/// <param name="AmountMinorUnits">What was originally charged.</param>
/// <param name="AmountRefundedMinorUnits">CUMULATIVE amount refunded so far; partial refunds accumulate here.</param>
/// <param name="Currency">Currency the charge settled in, lower-cased by the provider.</param>
/// <param name="Refunded">Whether the whole charge has been refunded.</param>
/// <param name="Disputed">Whether a dispute exists against this charge.</param>
/// <param name="Livemode">Whether the charge belongs to the provider's live mode.</param>
/// <param name="IntentId">The node's own funding intent, from the charge's provider metadata.</param>
/// <param name="SubjectUserId">The payer, from the charge's provider metadata.</param>
public sealed record ChargeReversalFacts(
    string ChargeId,
    string? PaymentIntentId,
    long AmountMinorUnits,
    long AmountRefundedMinorUnits,
    string Currency,
    bool Refunded,
    bool Disputed,
    bool Livemode,
    Guid? IntentId,
    Guid? SubjectUserId);

/// <summary>The server-fetched facts about one dispute.</summary>
/// <param name="DisputeId">Provider dispute id.</param>
/// <param name="ChargeId">The charge the dispute is against.</param>
/// <param name="AmountMinorUnits">The money in dispute.</param>
/// <param name="Currency">Currency the dispute is denominated in.</param>
/// <param name="Status">Provider-native dispute status, e.g. <c>needs_response</c> or <c>won</c>.</param>
/// <param name="Livemode">Whether the dispute belongs to the provider's live mode.</param>
/// <param name="NetBalanceImpactMinorUnits">
/// Sum of the dispute's balance transactions. Negative means the provider has actually taken the
/// money; back to zero or above means it has been given back.
/// </param>
public sealed record DisputeReversalFacts(
    string DisputeId,
    string ChargeId,
    long AmountMinorUnits,
    string Currency,
    string Status,
    bool Livemode,
    long NetBalanceImpactMinorUnits)
{
    /// <summary>True while the provider is actually holding the money out of the node's balance.</summary>
    public bool FundsWithdrawn => NetBalanceImpactMinorUnits < 0;
}

/// <summary>The server-fetched settlement of one payment: its charge and the balance transaction it produced.</summary>
/// <param name="ChargeId">The payment's latest charge.</param>
/// <param name="AmountMinorUnits">What the payer was charged.</param>
/// <param name="NetMinorUnits">What reached the platform balance after Stripe's fees (<c>balance_transaction.net</c>).</param>
/// <param name="Currency">Charge currency; the balance transaction must settle in the same currency.</param>
/// <param name="Livemode">Whether the charge belongs to the provider's live mode.</param>
/// <param name="AmountRefundedMinorUnits">Cumulative refunds already on the charge (M3: a refund can precede the credit).</param>
/// <param name="Disputed">Whether a dispute already stands against the charge.</param>
/// <param name="CreatedAt">When the charge was created (L3: the maturity clock starts here, not at handler time).</param>
public sealed record PaymentBalanceFacts(
    string ChargeId, long AmountMinorUnits, long NetMinorUnits, string Currency, bool Livemode,
    long AmountRefundedMinorUnits = 0, bool Disputed = false, DateTimeOffset? CreatedAt = null);

/// <summary>
/// Re-reads what a reversal event merely announced. Separate from
/// <see cref="IStripeGateway"/> because the checkout surface and the reversal surface have
/// different lifetimes and different owners; the interface is what the pipeline depends on, so a
/// later move into the gateway is a file move.
/// </summary>
public interface IReversalFactSource
{
    /// <summary>The charge as the provider currently holds it, including cumulative refunds.</summary>
    Task<AZOAResult<ChargeReversalFacts>> GetChargeAsync(
        StripeCredentials credentials, string chargeId, CancellationToken ct = default);

    /// <summary>The dispute as the provider currently holds it, including its balance impact.</summary>
    Task<AZOAResult<DisputeReversalFacts>> GetDisputeAsync(
        StripeCredentials credentials, string disputeId, CancellationToken ct = default);

    /// <summary>
    /// The dispute standing against one payment. A dispute webhook names the dispute object and
    /// the payment it belongs to, but the durable correlation this node keeps is the payment —
    /// so the dispute is looked up FROM the payment rather than trusted from the body.
    /// </summary>
    Task<AZOAResult<DisputeReversalFacts>> GetDisputeForPaymentAsync(
        StripeCredentials credentials, string paymentIntentId, CancellationToken ct = default);

    /// <summary>The payment's charge and its balance transaction net (D-8: the float is credited net of fees).</summary>
    Task<AZOAResult<PaymentBalanceFacts>> GetPaymentBalanceAsync(
        StripeCredentials credentials, string paymentIntentId, CancellationToken ct = default);
}

/// <summary>
/// Form-encoded Stripe REST reads for the two objects a reversal is about.
///
/// <para><b>Why this exists at all.</b> The webhook body for <c>charge.refunded</c> carries
/// <c>amount_refunded</c> and the body for a dispute carries <c>amount</c>. Reading either of
/// them would be the on-ramp's money-printer run backwards: a forged (or merely stale) body would
/// decide how many units this node takes OUT of somebody's account. So the delivery is a trigger
/// and every figure below is re-read from the provider, server-side, exactly as
/// <c>OnRampSettlementHandler</c> re-reads the checkout session.</para>
/// </summary>
public sealed class StripeReversalFactSource : IReversalFactSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StripeReversalFactSource> _logger;

    public StripeReversalFactSource(
        IHttpClientFactory httpClientFactory, ILogger<StripeReversalFactSource> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<AZOAResult<ChargeReversalFacts>> GetChargeAsync(
        StripeCredentials credentials, string chargeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chargeId))
            return Task.FromResult(AZOAResult<ChargeReversalFacts>.Failure("A charge id is required."));

        return SendAsync(
            credentials,
            "/v1/charges/" + Uri.EscapeDataString(chargeId.Trim()),
            "read a charge",
            ParseCharge,
            ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<DisputeReversalFacts>> GetDisputeAsync(
        StripeCredentials credentials, string disputeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(disputeId))
            return Task.FromResult(AZOAResult<DisputeReversalFacts>.Failure("A dispute id is required."));

        return SendAsync(
            credentials,
            "/v1/disputes/" + Uri.EscapeDataString(disputeId.Trim()),
            "read a dispute",
            ParseDispute,
            ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<DisputeReversalFacts>> GetDisputeForPaymentAsync(
        StripeCredentials credentials, string paymentIntentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paymentIntentId))
            return Task.FromResult(AZOAResult<DisputeReversalFacts>.Failure("A payment id is required."));

        return SendAsync(
            credentials,
            "/v1/disputes?limit=10&payment_intent=" + Uri.EscapeDataString(paymentIntentId.Trim()),
            "read the disputes against a payment",
            SelectDispute,
            ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<PaymentBalanceFacts>> GetPaymentBalanceAsync(
        StripeCredentials credentials, string paymentIntentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paymentIntentId))
            return Task.FromResult(AZOAResult<PaymentBalanceFacts>.Failure("A payment id is required."));

        return SendAsync(
            credentials,
            "/v1/payment_intents/" + Uri.EscapeDataString(paymentIntentId.Trim())
                + "?expand%5B%5D=latest_charge.balance_transaction",
            "read a payment's settlement",
            ParsePaymentBalance,
            ct);
    }

    /// <summary>Reads <c>latest_charge</c> and its expanded balance transaction; null unless both settled in one currency.</summary>
    public static PaymentBalanceFacts? ParsePaymentBalance(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("latest_charge", out var charge) || charge.ValueKind != JsonValueKind.Object
                || !charge.TryGetProperty("balance_transaction", out var balance) || balance.ValueKind != JsonValueKind.Object)
                return null;
            var chargeId = ReadString(charge, "id");
            var currency = ReadString(charge, "currency");
            if (string.IsNullOrWhiteSpace(chargeId) || string.IsNullOrWhiteSpace(currency)
                || ReadInt64(charge, "amount") is not { } amount || ReadInt64(balance, "net") is not { } net
                || !string.Equals(ReadString(balance, "currency"), currency, StringComparison.OrdinalIgnoreCase))
                return null;
            return new PaymentBalanceFacts(chargeId!, amount, net, currency!, ReadBool(charge, "livemode"),
                ReadInt64(charge, "amount_refunded") ?? 0L, ReadBool(charge, "disputed"),
                ReadInt64(charge, "created") is { } created ? DateTimeOffset.FromUnixTimeSeconds(created) : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<AZOAResult<T>> SendAsync<T>(
        StripeCredentials credentials,
        string path,
        string attemptedAction,
        Func<string, T?> parse,
        CancellationToken ct)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(credentials.SecretKey) || string.IsNullOrWhiteSpace(credentials.ApiBaseUrl))
            return FiatConsumerErrors.RailUnavailable<T>();

        // One opaque id ties the refusal to the server-side log line that carries the real
        // provider text; the text itself never crosses the boundary.
        var correlationId = Guid.NewGuid().ToString("N");

        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Get, credentials.ApiBaseUrl.TrimEnd('/') + path);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.SecretKey);
            message.Headers.TryAddWithoutValidation("Stripe-Version", StripeGateway.ApiVersion);

            var client = _httpClientFactory.CreateClient(StripeGateway.HttpClientName);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(
                credentials.Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : credentials.Timeout);

            using var response = await client.SendAsync(message, timeoutSource.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Stripe refused an attempt to {AttemptedAction} ({Status}). correlationId={CorrelationId}",
                    attemptedAction, (int)response.StatusCode, correlationId);

                return (int)response.StatusCode is 429 or >= 500
                    ? FiatConsumerErrors.RailBusy<T>(correlationId)
                    : FiatConsumerErrors.RailRefused<T>(correlationId);
            }

            var parsed = parse(body);
            if (parsed is null)
            {
                _logger.LogError(
                    "Stripe returned an object this node could not read while trying to {AttemptedAction}. "
                    + "correlationId={CorrelationId}",
                    attemptedAction, correlationId);
                return FiatConsumerErrors.RailBusy<T>(correlationId);
            }

            return AZOAResult<T>.Success(parsed, "Success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "The payment rail could not be reached to {AttemptedAction}. correlationId={CorrelationId}",
                attemptedAction, correlationId);
            return FiatConsumerErrors.RailBusy<T>(correlationId);
        }
    }

    /// <summary>Reads the charge fields the reversal pipeline uses. Unknown fields are ignored.</summary>
    public static ChargeReversalFacts? ParseCharge(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var chargeId = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(chargeId))
                return null;

            return new ChargeReversalFacts(
                ChargeId: chargeId!,
                PaymentIntentId: ReadString(root, "payment_intent"),
                AmountMinorUnits: ReadInt64(root, "amount") ?? 0L,
                AmountRefundedMinorUnits: ReadInt64(root, "amount_refunded") ?? 0L,
                Currency: ReadString(root, "currency") ?? string.Empty,
                Refunded: ReadBool(root, "refunded"),
                Disputed: ReadBool(root, "disputed"),
                Livemode: ReadBool(root, "livemode"),
                IntentId: ParseCompactGuid(ReadMetadata(root, "azoa_intent_id")),
                SubjectUserId: ParseCompactGuid(ReadMetadata(root, "azoa_user_id")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the dispute fields the reversal pipeline uses. Unknown fields are ignored.</summary>
    public static DisputeReversalFacts? ParseDispute(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var disputeId = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(disputeId))
                return null;

            // `charge` is a bare id string on an unexpanded dispute and an object when the caller
            // asked for it expanded. Reading both shapes costs two lines and stops an expansion
            // setting elsewhere from silently detaching a dispute from its charge.
            var charge = ReadString(root, "charge");
            if (string.IsNullOrWhiteSpace(charge)
                && root.TryGetProperty("charge", out var chargeObject)
                && chargeObject.ValueKind == JsonValueKind.Object)
            {
                charge = ReadString(chargeObject, "id");
            }

            if (string.IsNullOrWhiteSpace(charge))
                return null;

            return new DisputeReversalFacts(
                DisputeId: disputeId!,
                ChargeId: charge!,
                AmountMinorUnits: ReadInt64(root, "amount") ?? 0L,
                Currency: ReadString(root, "currency") ?? string.Empty,
                Status: ReadString(root, "status") ?? string.Empty,
                Livemode: ReadBool(root, "livemode"),
                NetBalanceImpactMinorUnits: SumBalanceTransactions(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the dispute a payment's reversal is about out of the provider's list.
    /// </summary>
    /// <remarks>
    /// A payment normally has exactly one. When it has several (a re-presented dispute followed
    /// by a second one), the one whose money is CURRENTLY out of the node's balance is the one
    /// that has to be unwound; otherwise the first entry, which the provider returns newest
    /// first. Choosing on the balance impact rather than on the status word keeps the choice tied
    /// to where the money is.
    /// </remarks>
    public static DisputeReversalFacts? SelectDispute(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            DisputeReversalFacts? first = null;
            foreach (var entry in data.EnumerateArray())
            {
                var parsed = ParseDispute(entry.GetRawText());
                if (parsed is null)
                    continue;

                first ??= parsed;
                if (parsed.FundsWithdrawn)
                    return parsed;
            }

            return first;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The dispute's net effect on the node's balance. A withdrawal is a negative entry and a
    /// reinstatement is the matching positive one, so the SUM is the honest answer to "is the
    /// money currently gone" -- rather than the dispute's own status word, which says who is
    /// winning the argument and not who is holding the funds.
    /// </summary>
    private static long SumBalanceTransactions(JsonElement root)
    {
        if (!root.TryGetProperty("balance_transactions", out var transactions)
            || transactions.ValueKind != JsonValueKind.Array)
        {
            return 0L;
        }

        var total = 0L;
        foreach (var entry in transactions.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object && ReadInt64(entry, "amount") is { } amount)
                total += amount;
        }

        return total;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed)
                ? parsed
                : null;

    private static bool ReadBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadMetadata(JsonElement element, string name)
        => element.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            ? ReadString(metadata, name)
            : null;

    private static Guid? ParseCompactGuid(string? value)
        => Guid.TryParseExact(value, "N", out var parsed) ? parsed : null;
}

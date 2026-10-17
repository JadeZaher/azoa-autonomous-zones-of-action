// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Fiat;

/// <summary>Bound credentials for one Stripe environment; never logged, never projected.</summary>
public readonly record struct StripeCredentials(string ApiBaseUrl, string SecretKey, TimeSpan Timeout);

/// <summary>Everything a Checkout session needs; carries identity and routing, never a unit quantity.</summary>
public sealed class StripeCheckoutSessionRequest
{
    /// <summary>Durable AZOA intent id (the <c>hosted_session</c> record id).</summary>
    public string IntentId { get; init; } = string.Empty;

    /// <summary>Caller-stable admission key; also the return page's lookup key.</summary>
    public string AdmissionKey { get; init; } = string.Empty;

    public string SubjectUserId { get; init; } = string.Empty;

    public string? TenantId { get; init; }

    /// <summary>Allowlisted routing value, e.g. <c>funds-top-up</c>.</summary>
    public string Purpose { get; init; } = string.Empty;

    public string Currency { get; init; } = "usd";

    /// <summary>The FIAT amount the person is paying, in minor units. Never a unit quantity.</summary>
    public long AmountMinorUnits { get; init; }

    /// <summary>Line-item label the person sees on the payment page.</summary>
    public string ProductName { get; init; } = string.Empty;

    public string SuccessUrl { get; init; } = string.Empty;

    public string CancelUrl { get; init; } = string.Empty;

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Stripe request idempotency key; a retried create returns the same session.</summary>
    public string IdempotencyKey { get; init; } = string.Empty;
}

/// <summary>The server-fetched facts about one Checkout session. The ONLY source of amount truth.</summary>
public sealed class StripeCheckoutSession
{
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Hosted payment page URL. Present until the session is completed or expired.</summary>
    public string? Url { get; init; }

    public string? PaymentIntentId { get; init; }

    /// <summary><c>open</c> | <c>complete</c> | <c>expired</c>.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary><c>unpaid</c> | <c>paid</c> | <c>no_payment_required</c>.</summary>
    public string PaymentStatus { get; init; } = string.Empty;

    /// <summary>Stripe-confirmed total in minor units. The only figure a price schedule may multiply.</summary>
    public long? AmountTotalMinorUnits { get; init; }

    public string? Currency { get; init; }

    /// <summary>Whether this session belongs to Stripe's live mode.</summary>
    public bool Livemode { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The AZOA intent id echoed back in <c>client_reference_id</c>.</summary>
    public string? ClientReferenceId { get; init; }
}

/// <summary>The Stripe HTTP surface the fiat rail uses. See <c>docs/FIAT-RAIL.md</c>.</summary>
public interface IStripeGateway
{
    /// <summary>Creates a hosted Checkout session. Idempotent on <see cref="StripeCheckoutSessionRequest.IdempotencyKey"/>.</summary>
    Task<AZOAResult<StripeCheckoutSession>> CreateCheckoutSessionAsync(
        StripeCredentials credentials, StripeCheckoutSessionRequest request, CancellationToken ct = default);

    /// <summary>Re-reads one Checkout session server-side. Never trust a webhook body for these facts.</summary>
    Task<AZOAResult<StripeCheckoutSession>> GetCheckoutSessionAsync(
        StripeCredentials credentials, string sessionId, CancellationToken ct = default);
}

/// <summary>
/// Form-encoded Stripe REST client over <see cref="HttpClient"/>. Deliberately not the Stripe
/// SDK: the surface used here is two endpoints and one HMAC, and the house rule is to minimise
/// external dependencies on a value path. See <c>docs/FIAT-RAIL.md</c>.
/// </summary>
public sealed class StripeGateway : IStripeGateway, IStripeRefundGateway
{
    /// <summary>
    /// Pinned Stripe API version. An unpinned client silently follows the account's default
    /// version, so a dashboard change could reshape <c>amount_total</c> under a running node.
    /// </summary>
    public const string ApiVersion = "2024-06-20";

    /// <summary>Named <see cref="IHttpClientFactory"/> client for the Stripe REST surface.</summary>
    public const string HttpClientName = "stripe";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StripeGateway> _logger;

    public StripeGateway(IHttpClientFactory httpClientFactory, ILogger<StripeGateway> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<AZOAResult<StripeCheckoutSession>> CreateCheckoutSessionAsync(
        StripeCredentials credentials, StripeCheckoutSessionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            new("client_reference_id", request.IntentId),
            new("success_url", request.SuccessUrl),
            new("cancel_url", request.CancelUrl),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", request.Currency),
            new("line_items[0][price_data][unit_amount]",
                request.AmountMinorUnits.ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][price_data][product_data][name]", request.ProductName),
        };

        // Metadata carries IDENTITY and ROUTING only. There is deliberately no quantity, rate
        // or unit field here: the only amount that may ever be converted is Stripe's own
        // confirmed amount_total, re-read server-side. A client able to put a quantity in
        // metadata is the money-printer the retired sidecar closed (its AGENTS.md "pricing"
        // section), and that closure has to be structural, not a review note.
        AddMetadata(form, "azoa_intent_id", request.IntentId);
        AddMetadata(form, "azoa_admission_key", request.AdmissionKey);
        AddMetadata(form, "azoa_user_id", request.SubjectUserId);
        AddMetadata(form, "azoa_purpose", request.Purpose);
        if (!string.IsNullOrWhiteSpace(request.TenantId))
            AddMetadata(form, "azoa_tenant_id", request.TenantId!);

        // The same identity is stamped on the PaymentIntent so a later charge, refund or
        // dispute event — which names a charge, not a checkout session — still resolves back
        // to this intent without a second lookup table.
        form.Add(new("payment_intent_data[metadata][azoa_intent_id]", request.IntentId));
        form.Add(new("payment_intent_data[metadata][azoa_user_id]", request.SubjectUserId));

        if (request.ExpiresAt is { } expiresAt)
            form.Add(new("expires_at", expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

        return SendAsync(
            credentials,
            HttpMethod.Post,
            "/v1/checkout/sessions",
            form,
            request.IdempotencyKey,
            "create a payment session",
            ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<StripeCheckoutSession>> GetCheckoutSessionAsync(
        StripeCredentials credentials, string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult(AZOAResult<StripeCheckoutSession>.Failure("A payment session id is required."));

        return SendAsync(
            credentials,
            HttpMethod.Get,
            "/v1/checkout/sessions/" + Uri.EscapeDataString(sessionId.Trim()),
            form: null,
            idempotencyKey: null,
            "read a payment session",
            ct);
    }

    private Task<AZOAResult<StripeCheckoutSession>> SendAsync(
        StripeCredentials credentials, HttpMethod method, string path,
        IReadOnlyCollection<KeyValuePair<string, string>>? form, string? idempotencyKey,
        string attemptedAction, CancellationToken ct)
        => SendAsync(credentials, method, path, form, idempotencyKey, attemptedAction, ct, ParseCheckoutSession);

    /// <inheritdoc/>
    public Task<AZOAResult<StripeRefund>> CreateRefundAsync(StripeCredentials credentials,
        string paymentIntentId, long amount, string intentId, string idempotencyKey, CancellationToken ct = default)
        => SendAsync(credentials, HttpMethod.Post, "/v1/refunds", new Dictionary<string, string>
        {
            ["payment_intent"] = paymentIntentId,
            ["amount"] = amount.ToString(CultureInfo.InvariantCulture),
            ["metadata[azoa_intent_id]"] = intentId,
        }, idempotencyKey, "refund a payment", ct, StripeRefund.Parse);

    /// <inheritdoc/>
    public Task<AZOAResult<StripeRefund>> GetRefundAsync(StripeCredentials credentials, string refundId, CancellationToken ct = default)
        => SendAsync(credentials, HttpMethod.Get, "/v1/refunds/" + Uri.EscapeDataString(refundId),
            null, null, "read a refund", ct, StripeRefund.Parse);

    private async Task<AZOAResult<T>> SendAsync<T>(
        StripeCredentials credentials,
        HttpMethod method,
        string path,
        IReadOnlyCollection<KeyValuePair<string, string>>? form,
        string? idempotencyKey,
        string attemptedAction,
        CancellationToken ct, Func<string, T?> parse) where T : class
    {
        if (string.IsNullOrWhiteSpace(credentials.SecretKey) || string.IsNullOrWhiteSpace(credentials.ApiBaseUrl))
            return FiatConsumerErrors.RailUnavailable<T>();

        // One opaque id ties the consumer-visible refusal to the server-side log line that
        // carries the real Stripe text; the text itself never crosses the boundary.
        var correlationId = Guid.NewGuid().ToString("N");

        try
        {
            using var message = new HttpRequestMessage(method, credentials.ApiBaseUrl.TrimEnd('/') + path);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.SecretKey);
            message.Headers.TryAddWithoutValidation("Stripe-Version", ApiVersion);
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
                message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            if (form is not null)
                message.Content = new FormUrlEncodedContent(form);

            var client = _httpClientFactory.CreateClient(HttpClientName);

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

                // 429 and 5xx are the rail being busy; anything else is a request this node
                // built wrongly, which a consumer cannot fix by retrying.
                return (int)response.StatusCode is 429 or >= 500
                    ? FiatConsumerErrors.RailBusy<T>(correlationId)
                    : FiatConsumerErrors.RailRefused<T>(correlationId);
            }

            var parsed = parse(body);
            if (parsed is null)
            {
                _logger.LogError(
                    "Stripe returned an unreadable response. correlationId={CorrelationId}",
                    correlationId);
                return FiatConsumerErrors.RailBusy<T>(correlationId);
            }

            return AZOAResult<T>.Success(parsed, "Success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "The payment rail could not be reached to {AttemptedAction}. correlationId={CorrelationId}",
                attemptedAction, correlationId);
            return FiatConsumerErrors.RailBusy<T>(correlationId);
        }
    }

    /// <summary>Reads the Checkout-session fields this node uses. Unknown fields are ignored.</summary>
    public static StripeCheckoutSession? ParseCheckoutSession(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var sessionId = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(sessionId))
                return null;

            return new StripeCheckoutSession
            {
                SessionId = sessionId!,
                Url = ReadString(root, "url"),
                PaymentIntentId = ReadString(root, "payment_intent"),
                Status = ReadString(root, "status") ?? string.Empty,
                PaymentStatus = ReadString(root, "payment_status") ?? string.Empty,
                AmountTotalMinorUnits = ReadInt64(root, "amount_total"),
                Currency = ReadString(root, "currency"),
                Livemode = root.TryGetProperty("livemode", out var livemode)
                    && livemode.ValueKind == JsonValueKind.True,
                ExpiresAt = ReadUnixSeconds(root, "expires_at"),
                ClientReferenceId = ReadString(root, "client_reference_id"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddMetadata(ICollection<KeyValuePair<string, string>> form, string key, string value)
        => form.Add(new KeyValuePair<string, string>($"metadata[{key}]", value));

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed)
                ? parsed
                : null;

    private static DateTimeOffset? ReadUnixSeconds(JsonElement root, string name)
    {
        var seconds = ReadInt64(root, name);
        return seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
    }
}

/// <summary>The verdict of checking one inbound <c>Stripe-Signature</c> header.</summary>
public enum StripeSignatureVerdict
{
    /// <summary>The body was signed by the configured webhook secret, inside the tolerance.</summary>
    Valid = 0,

    /// <summary>No webhook secret is configured, so nothing can be verified. Fail closed.</summary>
    NotConfigured = 1,

    /// <summary>The header is absent or does not parse.</summary>
    Malformed = 2,

    /// <summary>The header parsed, but no <c>v1</c> signature matched.</summary>
    Mismatch = 3,

    /// <summary>The header's timestamp is outside the replay tolerance.</summary>
    Stale = 4,
}

/// <summary>
/// Stripe's documented webhook signature scheme, verified in constant time.
///
/// <para>The header is <c>t=&lt;unix seconds&gt;,v1=&lt;hex&gt;[,v1=&lt;hex&gt;...]</c> and the
/// signed material is <c>"{t}.{rawBody}"</c>. Both halves matter: without the timestamp check a
/// captured delivery replays forever, and without the constant-time compare the hex comparison
/// leaks the expected digest one byte at a time.</para>
///
/// <para>The RAW body bytes are what is signed. Any re-serialisation of the JSON — even one that
/// round-trips to an equal object — changes the bytes and invalidates the signature, which is why
/// the webhook controller reads the request body as a string before anything parses it.</para>
/// </summary>
public static class StripeWebhookSignature
{
    /// <summary>Default replay tolerance; Stripe's own libraries use five minutes.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Header name carrying the timestamp and signatures.</summary>
    public const string HeaderName = "Stripe-Signature";

    /// <summary>
    /// Verifies <paramref name="signatureHeader"/> over <paramref name="rawBody"/>. Returns a
    /// verdict rather than a bool so the caller can log WHY without telling the sender.
    /// </summary>
    public static StripeSignatureVerdict Verify(
        string? rawBody, string? signatureHeader, string? webhookSecret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(webhookSecret))
            return StripeSignatureVerdict.NotConfigured;
        if (rawBody is null || string.IsNullOrWhiteSpace(signatureHeader))
            return StripeSignatureVerdict.Malformed;

        long? timestamp = null;
        var candidates = new List<string>();
        foreach (var part in signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (key == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                timestamp = parsed;
            else if (key == "v1" && value.Length > 0)
                candidates.Add(value);
        }

        if (timestamp is null || candidates.Count == 0)
            return StripeSignatureVerdict.Malformed;

        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(ComputeSignature(rawBody, timestamp.Value, webhookSecret!));

        var matched = false;
        foreach (var candidate in candidates)
        {
            var candidateBytes = System.Text.Encoding.UTF8.GetBytes(candidate.ToLowerInvariant());
            // FixedTimeEquals returns false for a length mismatch without comparing, which is
            // the same answer a wrong digest gives; the length of a hex SHA-256 is not a secret.
            // Every candidate is compared — no early exit — so the work is signature-count
            // shaped rather than match-position shaped.
            if (candidateBytes.Length == expectedBytes.Length
                && CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes))
            {
                matched = true;
            }
        }

        if (!matched)
            return StripeSignatureVerdict.Mismatch;

        // The freshness check runs AFTER the signature check on purpose: a caller who cannot
        // produce a valid signature learns nothing about our clock or our tolerance.
        var age = now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        var effectiveTolerance = tolerance <= TimeSpan.Zero ? DefaultTolerance : tolerance;
        return age.Duration() > effectiveTolerance ? StripeSignatureVerdict.Stale : StripeSignatureVerdict.Valid;
    }

    /// <summary>The lowercase-hex HMAC-SHA256 of <c>"{timestamp}.{rawBody}"</c> under the secret.</summary>
    public static string ComputeSignature(string rawBody, long unixTimestampSeconds, string webhookSecret)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookSecret);

        var preimage = System.Text.Encoding.UTF8.GetBytes(
            unixTimestampSeconds.ToString(CultureInfo.InvariantCulture) + "." + rawBody);
        using var hmac = new HMACSHA256(System.Text.Encoding.UTF8.GetBytes(webhookSecret));
        return AZOA.WebAPI.Helpers.Encoding.ToLowerHex(hmac.ComputeHash(preimage));
    }
}

/// <summary>
/// The fixed refusals the fiat rail is allowed to return. Every message is a compile-time
/// constant in the vocabulary of the thesis — funds, payment, account — so no Stripe internal,
/// no chain word and no unit name can escape through this seam. The optional correlation id is
/// the ONLY variable part, and it is opaque.
/// </summary>
public static class FiatConsumerErrors
{
    /// <summary>Wire code for a rail this node cannot use at all right now.</summary>
    public const string RailUnavailableCode = "RAIL_UNAVAILABLE";

    /// <summary>Wire code for a reachable-but-busy rail.</summary>
    public const string RailBusyCode = "RAIL_BUSY";

    /// <summary>Wire code for a request the rail rejected outright.</summary>
    public const string RailRefusedCode = "RAIL_REFUSED";

    private const string UnavailableMessage =
        "Adding funds is not available on this node right now.";

    private const string BusyMessage =
        "The payment service is busy. Nothing was charged; try again shortly.";

    private const string RefusedMessage =
        "This payment could not be started. Nothing was charged.";

    public static AZOAResult<T> RailUnavailable<T>() =>
        AZOAResult<T>.FailureWithCode(UnavailableMessage, RailUnavailableCode);

    public static AZOAResult<T> RailBusy<T>(string? correlationId = null) =>
        AZOAResult<T>.FailureWithCode(WithReference(BusyMessage, correlationId), RailBusyCode);

    public static AZOAResult<T> RailRefused<T>(string? correlationId = null) =>
        AZOAResult<T>.FailureWithCode(WithReference(RefusedMessage, correlationId), RailRefusedCode);

    private static string WithReference(string message, string? correlationId) =>
        string.IsNullOrWhiteSpace(correlationId) ? message : $"{message} Reference: {correlationId}.";
}

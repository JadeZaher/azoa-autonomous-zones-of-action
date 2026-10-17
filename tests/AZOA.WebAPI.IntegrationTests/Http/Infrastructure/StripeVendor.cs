using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>One Checkout Session held by the fake Stripe API.</summary>
public sealed class StripeCheckoutSessionState
{
    public required string Id { get; init; }
    public required string ClientReferenceId { get; init; }
    public required long AmountTotal { get; init; }
    public required string Currency { get; init; }
    public required long ExpiresAt { get; init; }
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }
    public string Status { get; set; } = "open";
    public string PaymentStatus { get; set; } = "unpaid";
    public string? PaymentIntentId { get; set; }

    /// <summary>The Checkout Session object as Stripe serializes it (subset the node reads).</summary>
    public JsonObject ToJson()
    {
        var metadata = new JsonObject();
        foreach (var (key, value) in Metadata) metadata[key] = value;
        return new JsonObject
        {
            ["id"] = Id,
            ["object"] = "checkout.session",
            ["url"] = Status == "open" ? $"https://checkout.stripe.vendor.test/c/pay/{Id}" : null,
            ["status"] = Status,
            ["payment_status"] = PaymentStatus,
            ["payment_intent"] = PaymentIntentId,
            ["amount_total"] = AmountTotal,
            ["currency"] = Currency,
            ["livemode"] = false,
            ["expires_at"] = ExpiresAt,
            ["client_reference_id"] = ClientReferenceId,
            ["metadata"] = metadata,
        };
    }
}

/// <summary>
/// Behavioral fake of the two Stripe endpoints the rail calls, plus a signer for the webhook
/// deliveries Stripe would make. See <c>Http/AGENTS.md</c> §stripe-fake.
/// </summary>
public sealed class StripeVendor
{
    public const string ApiHost = "stripe-api.vendor.test";

    private readonly ConcurrentDictionary<string, StripeCheckoutSessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Body, string SessionId)> _idempotentCreates = new(StringComparer.Ordinal);

    public StripeVendor(VendorEdge edge, string secretKey, string webhookSecret)
    {
        SecretKey = secretKey;
        WebhookSecret = webhookSecret;
        edge.Register(ApiHost, Respond);
    }

    public string SecretKey { get; }
    public string WebhookSecret { get; }

    public StripeCheckoutSessionState SessionFor(string clientReferenceId)
        => _sessions.Values.Single(session => session.ClientReferenceId == clientReferenceId);

    /// <summary>Puts a session Stripe already holds (created before the test's arranged state) on the API.</summary>
    public void Seed(StripeCheckoutSessionState session) => _sessions[session.Id] = session;

    /// <summary>The payer completed the hosted page and the card cleared.</summary>
    public void CompletePayment(StripeCheckoutSessionState session)
    {
        session.Status = "complete";
        session.PaymentStatus = "paid";
        session.PaymentIntentId ??= "pi_flow_" + Guid.NewGuid().ToString("N");
    }

    /// <summary>The hosted page expired without payment.</summary>
    public void Expire(StripeCheckoutSessionState session) => session.Status = "expired";

    /// <summary>
    /// A Stripe event envelope around a Checkout Session, as delivered to webhooks.
    /// <paramref name="claimedPaymentStatus"/> overrides only the body's copy, to prove the node
    /// re-reads Stripe's own session instead of trusting the delivery.
    /// </summary>
    public static string CheckoutEvent(string eventId, string eventType, StripeCheckoutSessionState session,
        bool livemode = false, string? claimedPaymentStatus = null)
    {
        var dataObject = session.ToJson();
        if (claimedPaymentStatus is not null)
            dataObject["payment_status"] = claimedPaymentStatus;
        return new JsonObject
        {
            ["id"] = eventId,
            ["object"] = "event",
            ["api_version"] = "2024-06-20",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["livemode"] = livemode,
            ["type"] = eventType,
            ["data"] = new JsonObject { ["object"] = dataObject },
        }.ToJsonString();
    }

    /// <summary>
    /// <c>Stripe-Signature</c> header value per Stripe's documented scheme: HMAC-SHA256 over
    /// <c>"{t}.{payload}"</c> keyed by the endpoint secret. Implemented here independently of the
    /// node's verifier so a shared bug cannot make both sides agree.
    /// </summary>
    public string SignatureHeader(string payload, DateTimeOffset? signedAt = null, string? secret = null)
    {
        var timestamp = (signedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var digest = HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret ?? WebhookSecret),
            System.Text.Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return $"t={timestamp},v1={Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private HttpResponseMessage Respond(VendorRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var authorization) || authorization != $"Bearer {SecretKey}")
            return VendorEdge.Json(HttpStatusCode.Unauthorized, """{"error":{"type":"invalid_request_error"}}""");

        const string sessionsPath = "/v1/checkout/sessions";
        if (request.Method == HttpMethod.Post && request.Path == sessionsPath)
            return Create(request);
        if (request.Method == HttpMethod.Get && request.Path.StartsWith(sessionsPath + "/", StringComparison.Ordinal)
            && _sessions.TryGetValue(Uri.UnescapeDataString(request.Path[(sessionsPath.Length + 1)..]), out var found))
            return VendorEdge.Json(HttpStatusCode.OK, found.ToJson().ToJsonString());
        return VendorEdge.Json(HttpStatusCode.NotFound, """{"error":{"type":"invalid_request_error","code":"resource_missing"}}""");
    }

    private HttpResponseMessage Create(VendorRequest request)
    {
        var form = request.Body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : string.Empty);

        // Stripe idempotency: the same key with the same parameters returns the original session;
        // the same key with different parameters is refused, never silently reinterpreted.
        if (request.Headers.TryGetValue("Idempotency-Key", out var key) && _idempotentCreates.TryGetValue(key, out var existing))
        {
            return existing.Body == request.Body
                ? VendorEdge.Json(HttpStatusCode.OK, _sessions[existing.SessionId].ToJson().ToJsonString())
                : VendorEdge.Json(HttpStatusCode.BadRequest,
                    """{"error":{"type":"idempotency_error","message":"Keys for idempotent requests can only be used with the same parameters they were first used with."}}""");
        }

        var session = new StripeCheckoutSessionState
        {
            Id = "cs_flow_" + Guid.NewGuid().ToString("N"),
            ClientReferenceId = form.GetValueOrDefault("client_reference_id", string.Empty),
            AmountTotal = long.Parse(form.GetValueOrDefault("line_items[0][price_data][unit_amount]", "0"), CultureInfo.InvariantCulture),
            Currency = form.GetValueOrDefault("line_items[0][price_data][currency]", "usd"),
            ExpiresAt = form.TryGetValue("expires_at", out var expires)
                ? long.Parse(expires, CultureInfo.InvariantCulture)
                : DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
            Metadata = form.Where(pair => pair.Key.StartsWith("metadata[", StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key["metadata[".Length..^1], pair => pair.Value),
        };
        _sessions[session.Id] = session;
        if (!string.IsNullOrEmpty(key)) _idempotentCreates[key] = (request.Body, session.Id);
        return VendorEdge.Json(HttpStatusCode.OK, session.ToJson().ToJsonString());
    }
}

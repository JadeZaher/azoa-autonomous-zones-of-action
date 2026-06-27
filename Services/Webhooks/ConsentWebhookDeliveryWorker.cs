// SPDX-License-Identifier: UNLICENSED

// ─── DI registration (orchestrator applies to Program.cs) ───────────────────────────
//   // stores (scoped — resolved inside the worker's per-tick scope):
//   builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IConsentWebhookOutboxStore,
//       AZOA.WebAPI.Providers.Stores.Surreal.SurrealConsentWebhookOutboxStore>();
//   builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IWebhookRegistrationStore,
//       AZOA.WebAPI.Providers.Stores.Surreal.SurrealWebhookRegistrationStore>();
//   // security helpers (singletons — stateless):
//   builder.Services.AddSingleton<AZOA.WebAPI.Core.Webhooks.WebhookSsrfGuard>();
//   builder.Services.AddSingleton<AZOA.WebAPI.Core.Webhooks.WebhookHmacSigner>();
//   // options:
//   builder.Services.AddOptions<AZOA.WebAPI.Services.Webhooks.WebhookOptions>()
//       .Bind(builder.Configuration.GetSection(AZOA.WebAPI.Services.Webhooks.WebhookOptions.SectionName));
//   // HttpClient (named, via IHttpClientFactory):
//   builder.Services.AddHttpClient(AZOA.WebAPI.Services.Webhooks.WebhookOptions.HttpClientName);
//   // hosted worker:
//   builder.Services.AddHostedService<AZOA.WebAPI.Services.Webhooks.ConsentWebhookDeliveryWorker>();
// (IConsentWebhookEmitter + its outbox store are also listed in ConsentWebhookEmitter.cs.)
// ────────────────────────────────────────────────────────────────────────────────────

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core.Webhooks;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Services.Webhooks;

/// <summary>
/// The consent webhook delivery worker (tenant-consent-delegation §4, AC7) — a hosted
/// <see cref="BackgroundService"/> that drains the consent-webhook transactional outbox
/// and POSTs each due event to its tenant's registered endpoint. Mirrors the
/// <c>SagaProcessorHostedService</c> / <c>ReconciliationHostedService</c> shape exactly:
/// a singleton hosted loop, a config-driven interval, a fresh DI scope per tick (the
/// stores are scoped), and a last-ditch guard so a bad tick never tears down the app.
///
/// <para><b>Transactional outbox (AC7).</b> The worker is the SECOND half of the outbox
/// pattern — the first half (<c>ConsentWebhookEmitter</c>) wrote the row in the same
/// transaction as the grant state change. The worker only ever reads due rows and
/// transitions them; it never participates in the grant write.</para>
///
/// <para><b>Per-tenant isolation (H5).</b> Each event resolves ONLY its own tenant's
/// <see cref="WebhookRegistration"/> (<c>GetByTenantAsync(evt.TenantId)</c>) and signs
/// with ONLY that tenant's secret. A tenant's event can never be delivered with another
/// tenant's url or secret.</para>
///
/// <para><b>SSRF (H5).</b> The registered url is re-validated by
/// <see cref="WebhookSsrfGuard"/> immediately before each POST — a blocked url is
/// dead-lettered and NEVER POSTed (so a DNS rebind to a private address after
/// registration is still caught).</para>
///
/// <para><b>Replay-resistant signature (H5).</b> Each POST carries a timestamped
/// HMAC over the length-prefixed <c>(be32(len(timestamp)) || timestamp || body)</c>
/// preimage (<see cref="WebhookHmacSigner"/>), the signed timestamp, and the stable
/// idempotency id.</para>
///
/// <para><b>Observe-only boundary (AC8).</b> The worker NEVER writes back to
/// <c>consent_grant</c> and never overrides a revocation — it only transitions the
/// outbox row's own delivery state. Consent validity is owned entirely by the signing
/// seam.</para>
/// </summary>
public sealed class ConsentWebhookDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebhookSsrfGuard _ssrfGuard;
    private readonly WebhookHmacSigner _signer;
    private readonly ILogger<ConsentWebhookDeliveryWorker> _logger;
    private readonly WebhookOptions _options;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ConsentWebhookDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        WebhookSsrfGuard ssrfGuard,
        WebhookHmacSigner signer,
        ILogger<ConsentWebhookDeliveryWorker> logger,
        IOptions<WebhookOptions> options)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _ssrfGuard = ssrfGuard;
        _signer = signer;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Consent webhook delivery worker is DISABLED (Webhooks:Enabled=false). " +
                "The scoped outbox/registration stores can still be exercised directly.");
            return;
        }

        // Clamp to a 1s floor so a misconfigured tiny interval can't hot-loop.
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds));
        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, _options.StartupDelaySeconds));

        _logger.LogInformation(
            "Consent webhook delivery worker starting. StartupDelay={StartupDelay}s " +
            "Poll={Poll}s BatchSize={BatchSize} MaxAttempts={MaxAttempts}.",
            startupDelay.TotalSeconds, interval.TotalSeconds, _options.BatchSize, _options.MaxAttempts);

        try
        {
            if (startupDelay > TimeSpan.Zero)
                await Task.Delay(startupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunTickSafelyAsync(stoppingToken);

                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break; // host stopping — exit cleanly
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown during the startup delay or a scan await.
        }
        catch (Exception ex)
        {
            // Last-ditch guard: the loop must never bubble out of the hosted service.
            _logger.LogError(ex,
                "Consent webhook delivery worker terminated unexpectedly — it will NOT restart " +
                "until the app is recycled. Investigate.");
        }

        _logger.LogInformation("Consent webhook delivery worker stopped.");
    }

    /// <summary>
    /// One tick: fresh scope, resolve the scoped stores, scan due events, deliver each.
    /// All non-cancellation exceptions are contained here so a bad tick never escapes.
    /// </summary>
    private async Task RunTickSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IConsentWebhookOutboxStore>();
            var registrations = scope.ServiceProvider.GetRequiredService<IWebhookRegistrationStore>();

            var dueResult = await outbox.ListDueAsync(DateTime.UtcNow, _options.BatchSize, stoppingToken);
            if (dueResult.IsError || dueResult.Result is null)
            {
                if (dueResult.IsError)
                    _logger.LogWarning("Consent webhook due-scan failed: {Error}", dueResult.Message);
                return;
            }

            foreach (var evt in dueResult.Result)
            {
                stoppingToken.ThrowIfCancellationRequested();
                await DeliverOneAsync(evt, outbox, registrations, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw; // propagate so ExecuteAsync exits the loop cleanly
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consent webhook delivery tick failed — swallowed; will retry next interval.");
        }
    }

    /// <summary>
    /// Deliver a single outbox event. Resolves the tenant's OWN registration (H5),
    /// SSRF-guards the url (AC7), signs the body with the tenant's secret (timestamped
    /// HMAC — H5), POSTs, and transitions the outbox row: 2xx ⇒ Delivered; otherwise
    /// reschedule with exponential backoff until MaxAttempts, then dead-letter.
    /// </summary>
    private async Task DeliverOneAsync(
        ConsentWebhookEvent evt,
        IConsentWebhookOutboxStore outbox,
        IWebhookRegistrationStore registrations,
        CancellationToken ct)
    {
        // ── Resolve the tenant's OWN registration (strict per-tenant isolation H5) ──
        var regResult = await registrations.GetByTenantAsync(evt.TenantId, ct);
        var registration = regResult.Result;

        if (regResult.IsError)
        {
            // A transient store error — retry later (do NOT dead-letter on a read fault).
            await RescheduleAsync(evt, outbox, $"registration lookup failed: {regResult.Message}", ct);
            return;
        }

        if (registration is null || !registration.IsActive)
        {
            // No active registration ⇒ permanently undeliverable for now — dead-letter
            // rather than spin retries against a tenant that has not (re)registered.
            await DeadLetterAsync(evt, outbox,
                "no active webhook registration for tenant", ct);
            return;
        }

        // ── SSRF guard (AC7) — NEVER POST to a blocked url ──────────────────────────
        if (!_ssrfGuard.IsAllowed(registration.Url, out var ssrfReason))
        {
            await DeadLetterAsync(evt, outbox, $"SSRF-blocked url: {ssrfReason}", ct);
            return;
        }

        // ── Serialize the payload + sign (timestamped HMAC, tenant's own secret) ────
        var body = SerializePayload(evt);
        // Fresh UTC delivery timestamp on EVERY attempt so a legitimately retried event
        // stays inside the receiver's freshness window; only a held replay falls outside.
        var timestampIso = DateTime.UtcNow.ToString("o");
        string signature;
        try
        {
            signature = _signer.Sign(body, timestampIso, registration.Secret);
        }
        catch (Exception ex)
        {
            // A missing/blank secret is a config fault, not a transient — dead-letter.
            await DeadLetterAsync(evt, outbox, $"signing failed: {ex.Message}", ct);
            return;
        }

        // ── POST via IHttpClientFactory ─────────────────────────────────────────────
        try
        {
            var client = _httpClientFactory.CreateClient(WebhookOptions.HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.HttpTimeoutSeconds));

            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, registration.Url) { Content = content };
            request.Headers.TryAddWithoutValidation("X-Azoa-Signature", signature);
            request.Headers.TryAddWithoutValidation("X-Azoa-Timestamp", timestampIso);
            request.Headers.TryAddWithoutValidation("X-Azoa-Idempotency-Id", evt.IdempotencyId);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);

            // Redirect following is DISABLED on the named client's primary handler
            // (SocketsHttpHandler.AllowAutoRedirect=false, see Program.cs) so a 3xx is NOT
            // transparently chased — it surfaces here as a non-success status. Combined
            // with IsSuccessStatusCode (2xx only), a 302 to an internal address (the SSRF
            // redirect-bypass) is treated as a DELIVERY FAILURE: it is rescheduled /
            // dead-lettered rather than silently followed to a blocked host.
            if (response.IsSuccessStatusCode)
            {
                var marked = await outbox.MarkDeliveredAsync(evt.Id, ct);
                if (marked.IsError)
                    _logger.LogWarning("Consent webhook {EventId} delivered (HTTP {Status}) but mark-delivered failed: {Error}",
                        evt.Id, (int)response.StatusCode, marked.Message);
                else
                    _logger.LogInformation("Consent webhook {EventId} delivered to tenant {TenantId} (HTTP {Status}).",
                        evt.Id, evt.TenantId, (int)response.StatusCode);
                return;
            }

            // Non-2xx ⇒ a delivery failure — reschedule (or dead-letter on exhaustion).
            await RescheduleAsync(evt, outbox, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host stopping — leave the row Pending, the next run retries it
        }
        catch (Exception ex)
        {
            // Network / timeout / DNS — transient. Reschedule with backoff.
            await RescheduleAsync(evt, outbox, $"delivery exception: {ex.Message}", ct);
        }
    }

    /// <summary>
    /// Reschedule a failed delivery with exponential backoff, or dead-letter once the
    /// attempt budget is exhausted. The attempt about to be recorded is
    /// <c>evt.AttemptCount + 1</c>.
    /// </summary>
    private async Task RescheduleAsync(
        ConsentWebhookEvent evt, IConsentWebhookOutboxStore outbox, string error, CancellationToken ct)
    {
        var nextAttempt = evt.AttemptCount + 1;
        if (nextAttempt >= _options.MaxAttempts)
        {
            await DeadLetterAsync(evt, outbox,
                $"exhausted {_options.MaxAttempts} attempts; last error: {error}", ct);
            return;
        }

        var backoff = ComputeBackoff(nextAttempt);
        var nextAttemptAt = DateTime.UtcNow + backoff;
        var result = await outbox.RescheduleAsync(evt.Id, nextAttempt, nextAttemptAt, error, ct);
        if (result.IsError)
            _logger.LogWarning("Consent webhook {EventId} reschedule failed: {Error}", evt.Id, result.Message);
        else
            _logger.LogInformation(
                "Consent webhook {EventId} delivery failed (attempt {Attempt}/{Max}); retry in {Backoff}s. Reason: {Reason}",
                evt.Id, nextAttempt, _options.MaxAttempts, backoff.TotalSeconds, error);
    }

    private async Task DeadLetterAsync(
        ConsentWebhookEvent evt, IConsentWebhookOutboxStore outbox, string reason, CancellationToken ct)
    {
        var result = await outbox.DeadLetterAsync(evt.Id, reason, ct);
        if (result.IsError)
            _logger.LogWarning("Consent webhook {EventId} dead-letter failed: {Error}", evt.Id, result.Message);
        else
            _logger.LogWarning(
                "Consent webhook {EventId} (tenant {TenantId}) DEAD-LETTERED — observe-only, consent state unaffected. Reason: {Reason}",
                evt.Id, evt.TenantId, reason);
    }

    /// <summary>
    /// Exponential backoff: <c>BaseBackoffSeconds * 2^(attempt-1)</c>, capped at
    /// <c>MaxBackoffSeconds</c>. attempt is 1-based (the attempt about to be scheduled).
    /// </summary>
    private TimeSpan ComputeBackoff(int attempt)
    {
        var baseSeconds = Math.Max(1, _options.BaseBackoffSeconds);
        var maxSeconds = Math.Max(baseSeconds, _options.MaxBackoffSeconds);
        // 2^(attempt-1) as a double; clamp the exponent so it can't overflow.
        var exponent = Math.Min(Math.Max(0, attempt - 1), 30);
        var seconds = baseSeconds * Math.Pow(2, exponent);
        if (seconds > maxSeconds || double.IsInfinity(seconds)) seconds = maxSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Serialize the webhook POST body: the event payload the spec mandates —
    /// <c>eventType</c> (wire form), <c>grantId</c>, <c>avatarId</c>, <c>tenantId</c>,
    /// <c>scopes</c> (array), <c>participationRef</c>, <c>occurredAt</c>. Stable shape;
    /// the HMAC is computed over EXACTLY this string.
    /// </summary>
    private static string SerializePayload(ConsentWebhookEvent evt)
    {
        var payload = new WebhookPayload
        {
            EventType        = evt.WireEventType,
            GrantId          = evt.GrantId.ToString(),
            AvatarId         = evt.AvatarId.ToString(),
            TenantId         = evt.TenantId.ToString(),
            Scopes           = string.IsNullOrWhiteSpace(evt.Scopes)
                               ? Array.Empty<string>()
                               : evt.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ParticipationRef = evt.ParticipationRef,
            OccurredAt       = DateTime.SpecifyKind(evt.OccurredAt, DateTimeKind.Utc).ToString("o"),
            IdempotencyId    = evt.IdempotencyId,
        };
        return JsonSerializer.Serialize(payload, PayloadJsonOptions);
    }

    /// <summary>Wire shape of the webhook POST body (spec §4 payload).</summary>
    private sealed class WebhookPayload
    {
        [JsonPropertyName("eventType")]        public string EventType { get; set; } = string.Empty;
        [JsonPropertyName("grantId")]          public string GrantId { get; set; } = string.Empty;
        [JsonPropertyName("avatarId")]         public string AvatarId { get; set; } = string.Empty;
        [JsonPropertyName("tenantId")]         public string TenantId { get; set; } = string.Empty;
        [JsonPropertyName("scopes")]           public string[] Scopes { get; set; } = Array.Empty<string>();
        [JsonPropertyName("participationRef")] public string? ParticipationRef { get; set; }
        [JsonPropertyName("occurredAt")]       public string OccurredAt { get; set; } = string.Empty;
        [JsonPropertyName("idempotencyId")]    public string IdempotencyId { get; set; } = string.Empty;
    }
}

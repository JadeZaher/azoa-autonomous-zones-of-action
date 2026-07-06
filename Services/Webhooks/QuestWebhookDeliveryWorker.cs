// SPDX-License-Identifier: UNLICENSED

// ─── DI registration (orchestrator applies to Program.cs) ───────────────────────────
//   // outbox store (scoped — resolved inside the worker's per-tick scope):
//   builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IQuestWebhookOutboxStore,
//       AZOA.WebAPI.Providers.Stores.Surreal.SurrealQuestWebhookOutboxStore>();
//   // emitter (scoped — the Emit node handler's enqueue seam):
//   builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IQuestWebhookEmitter,
//       AZOA.WebAPI.Services.Quest.QuestWebhookEmitter>();
//   // HttpClient (named — same SSRF-guarded handler as the consent client):
//   builder.Services.AddHttpClient(AZOA.WebAPI.Services.Webhooks.WebhookOptions.QuestHttpClientName)
//       .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
//       {
//           AllowAutoRedirect = false,
//           ConnectCallback = AZOA.WebAPI.Core.Webhooks.WebhookSsrfGuard.CreateGuardedConnectCallback(),
//       });
//   // hosted worker:
//   builder.Services.AddHostedService<AZOA.WebAPI.Services.Webhooks.QuestWebhookDeliveryWorker>();
//   // (the shared WebhookRegistration store + WebhookSsrfGuard + WebhookHmacSigner +
//   //  WebhookOptions are ALREADY registered for the consent path — reused as-is.)
// ────────────────────────────────────────────────────────────────────────────────────

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core.Webhooks;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Services.Webhooks;

/// <summary>
/// The GENERIC quest webhook delivery worker (final-hardening F3 — the generalized
/// mirror of <see cref="ConsentWebhookDeliveryWorker"/>). A hosted
/// <see cref="BackgroundService"/> that drains the quest-webhook transactional outbox and
/// POSTs each due <c>quest.emit</c> event to its tenant's registered endpoint. Same shape
/// as the consent worker exactly: a singleton hosted loop, a config-driven interval, a
/// fresh DI scope per tick (the stores are scoped), and a last-ditch guard so a bad tick
/// never tears down the app.
///
/// <para><b>Shared infra, parallel outbox.</b> The worker reuses the SAME
/// <see cref="IWebhookRegistrationStore"/> (a tenant's ONE endpoint receives both consent
/// and quest.emit events), the SAME <see cref="WebhookSsrfGuard"/>, the SAME
/// <see cref="WebhookHmacSigner"/>, and the SAME <see cref="WebhookOptions"/> as the
/// consent path. Only the outbox table + the payload shape differ. See
/// <c>Services/Webhooks/AGENTS.md</c> §quest-webhook.</para>
///
/// <para><b>Per-tenant isolation + SSRF + replay-resistant signature (H5).</b> Identical
/// to the consent worker: each event resolves ONLY its own tenant's registration + secret;
/// the url is re-SSRF-guarded immediately before each POST (DNS-rebind defence); the POST
/// carries a timestamped HMAC over the length-prefixed <c>(be32(len(ts)) || ts || body)</c>
/// preimage, the signed timestamp, and the stable idempotency id.</para>
///
/// <para><b>Best-effort boundary.</b> The worker NEVER writes back to <c>quest_run</c> /
/// <c>quest_node_execution</c> — it only transitions the outbox row's own delivery state.
/// A dead-lettered event does not fail or roll back the quest run.</para>
/// </summary>
public sealed class QuestWebhookDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebhookSsrfGuard _ssrfGuard;
    private readonly WebhookHmacSigner _signer;
    private readonly ILogger<QuestWebhookDeliveryWorker> _logger;
    private readonly WebhookOptions _options;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public QuestWebhookDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        WebhookSsrfGuard ssrfGuard,
        WebhookHmacSigner signer,
        ILogger<QuestWebhookDeliveryWorker> logger,
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
                "Quest webhook delivery worker is DISABLED (Webhooks:Enabled=false). " +
                "The scoped outbox/registration stores can still be exercised directly.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds));
        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, _options.StartupDelaySeconds));

        _logger.LogInformation(
            "Quest webhook delivery worker starting. StartupDelay={StartupDelay}s " +
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
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown during the startup delay or a scan await.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Quest webhook delivery worker terminated unexpectedly — it will NOT restart " +
                "until the app is recycled. Investigate.");
        }

        _logger.LogInformation("Quest webhook delivery worker stopped.");
    }

    /// <summary>One tick: fresh scope, resolve the scoped stores, scan due events, deliver
    /// each. All non-cancellation exceptions are contained here.</summary>
    private async Task RunTickSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IQuestWebhookOutboxStore>();
            var registrations = scope.ServiceProvider.GetRequiredService<IWebhookRegistrationStore>();

            var dueResult = await outbox.ListDueAsync(DateTime.UtcNow, _options.BatchSize, stoppingToken);
            if (dueResult.IsError || dueResult.Result is null)
            {
                if (dueResult.IsError)
                    _logger.LogWarning("Quest webhook due-scan failed: {Error}", dueResult.Message);
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
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quest webhook delivery tick failed — swallowed; will retry next interval.");
        }
    }

    /// <summary>Deliver a single outbox event. Resolves the tenant's OWN registration (H5),
    /// SSRF-guards the url, signs the body with the tenant's secret (timestamped HMAC),
    /// POSTs, and transitions the outbox row: 2xx ⇒ Delivered; otherwise reschedule with
    /// exponential backoff until MaxAttempts, then dead-letter.</summary>
    private async Task DeliverOneAsync(
        QuestWebhookEvent evt,
        IQuestWebhookOutboxStore outbox,
        IWebhookRegistrationStore registrations,
        CancellationToken ct)
    {
        var regResult = await registrations.GetByTenantAsync(evt.TenantId, ct);
        var registration = regResult.Result;

        if (regResult.IsError)
        {
            await RescheduleAsync(evt, outbox, $"registration lookup failed: {regResult.Message}", ct);
            return;
        }

        if (registration is null || !registration.IsActive)
        {
            await DeadLetterAsync(evt, outbox, "no active webhook registration for tenant", ct);
            return;
        }

        if (!_ssrfGuard.IsAllowed(registration.Url, out var ssrfReason))
        {
            await DeadLetterAsync(evt, outbox, $"SSRF-blocked url: {ssrfReason}", ct);
            return;
        }

        var body = SerializePayload(evt);
        var timestampIso = DateTime.UtcNow.ToString("o");
        string signature;
        try
        {
            signature = _signer.Sign(body, timestampIso, registration.Secret);
        }
        catch (Exception ex)
        {
            await DeadLetterAsync(evt, outbox, $"signing failed: {ex.Message}", ct);
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(WebhookOptions.QuestHttpClientName);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.HttpTimeoutSeconds));

            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, registration.Url) { Content = content };
            request.Headers.TryAddWithoutValidation("X-Azoa-Signature", signature);
            request.Headers.TryAddWithoutValidation("X-Azoa-Timestamp", timestampIso);
            request.Headers.TryAddWithoutValidation("X-Azoa-Idempotency-Id", evt.IdempotencyId);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);

            // AllowAutoRedirect=false on the named client's primary handler ⇒ a 3xx to an
            // internal address surfaces as a non-success status and is treated as a
            // delivery failure, not silently followed (the SSRF redirect-bypass defence).
            if (response.IsSuccessStatusCode)
            {
                var marked = await outbox.MarkDeliveredAsync(evt.Id, ct);
                if (marked.IsError)
                    _logger.LogWarning("Quest webhook {EventId} delivered (HTTP {Status}) but mark-delivered failed: {Error}",
                        evt.Id, (int)response.StatusCode, marked.Message);
                else
                    _logger.LogInformation("Quest webhook {EventId} delivered to tenant {TenantId} (HTTP {Status}).",
                        evt.Id, evt.TenantId, (int)response.StatusCode);
                return;
            }

            await RescheduleAsync(evt, outbox, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RescheduleAsync(evt, outbox, $"delivery exception: {ex.Message}", ct);
        }
    }

    private async Task RescheduleAsync(
        QuestWebhookEvent evt, IQuestWebhookOutboxStore outbox, string error, CancellationToken ct)
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
            _logger.LogWarning("Quest webhook {EventId} reschedule failed: {Error}", evt.Id, result.Message);
        else
            _logger.LogInformation(
                "Quest webhook {EventId} delivery failed (attempt {Attempt}/{Max}); retry in {Backoff}s. Reason: {Reason}",
                evt.Id, nextAttempt, _options.MaxAttempts, backoff.TotalSeconds, error);
    }

    private async Task DeadLetterAsync(
        QuestWebhookEvent evt, IQuestWebhookOutboxStore outbox, string reason, CancellationToken ct)
    {
        var result = await outbox.DeadLetterAsync(evt.Id, reason, ct);
        if (result.IsError)
            _logger.LogWarning("Quest webhook {EventId} dead-letter failed: {Error}", evt.Id, result.Message);
        else
            _logger.LogWarning(
                "Quest webhook {EventId} (tenant {TenantId}) DEAD-LETTERED — best-effort, quest-run state unaffected. Reason: {Reason}",
                evt.Id, evt.TenantId, reason);
    }

    /// <summary>Exponential backoff: <c>BaseBackoffSeconds * 2^(attempt-1)</c>, capped at
    /// <c>MaxBackoffSeconds</c>. attempt is 1-based.</summary>
    private TimeSpan ComputeBackoff(int attempt)
    {
        var baseSeconds = Math.Max(1, _options.BaseBackoffSeconds);
        var maxSeconds = Math.Max(baseSeconds, _options.MaxBackoffSeconds);
        var exponent = Math.Min(Math.Max(0, attempt - 1), 30);
        var seconds = baseSeconds * Math.Pow(2, exponent);
        if (seconds > maxSeconds || double.IsInfinity(seconds)) seconds = maxSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Serialize the webhook POST body: <c>eventType</c> (the tenant-defined
    /// name), <c>runId</c>, <c>nodeId</c>, <c>questId</c>, <c>payload</c> (the tenant's
    /// opaque object, re-embedded as JSON), <c>occurredAt</c>, <c>idempotencyId</c>. The
    /// HMAC is computed over EXACTLY this string.</summary>
    private static string SerializePayload(QuestWebhookEvent evt)
    {
        // Re-embed the stored payload JSON as a nested object (not a string) so the
        // receiver sees the tenant's original shape under `payload`. A malformed stored
        // payload degrades to an empty object rather than failing the whole delivery.
        JsonElement payloadElement;
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(evt.PayloadJson) ? "{}" : evt.PayloadJson);
            payloadElement = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse("{}");
            payloadElement = doc.RootElement.Clone();
        }

        var payload = new WebhookPayload
        {
            EventType     = evt.EventType,
            RunId         = evt.RunId.ToString(),
            NodeId        = evt.NodeId.ToString(),
            QuestId       = evt.QuestId.ToString(),
            Payload       = payloadElement,
            OccurredAt    = DateTime.SpecifyKind(evt.OccurredAt, DateTimeKind.Utc).ToString("o"),
            IdempotencyId = evt.IdempotencyId,
        };
        return JsonSerializer.Serialize(payload, PayloadJsonOptions);
    }

    /// <summary>Wire shape of the quest.emit webhook POST body.</summary>
    private sealed class WebhookPayload
    {
        [JsonPropertyName("eventType")]     public string EventType { get; set; } = string.Empty;
        [JsonPropertyName("runId")]         public string RunId { get; set; } = string.Empty;
        [JsonPropertyName("nodeId")]        public string NodeId { get; set; } = string.Empty;
        [JsonPropertyName("questId")]       public string QuestId { get; set; } = string.Empty;
        [JsonPropertyName("payload")]       public JsonElement Payload { get; set; }
        [JsonPropertyName("occurredAt")]    public string OccurredAt { get; set; } = string.Empty;
        [JsonPropertyName("idempotencyId")] public string IdempotencyId { get; set; } = string.Empty;
    }
}

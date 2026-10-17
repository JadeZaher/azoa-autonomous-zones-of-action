// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Inbound payment-rail webhook. The node's only authority on whether a card payment cleared.
///
/// <para><b>Authentication is the signature, and nothing else.</b> The delivery carries no
/// principal, so the endpoint is anonymous to ASP.NET and gets its own rate-limit policy — a
/// per-user partition is meaningless here. The <c>Stripe-Signature</c> HMAC is verified against
/// the raw body in constant time, and a node with no webhook secret configured verifies NOTHING
/// and therefore accepts nothing.</para>
///
/// <para><b>Status codes are a control, not cosmetics</b> (the retired sidecar's webhook-mapping
/// section, ported). A 4xx stops the provider retrying, a 5xx makes it retry with backoff, and a
/// 2xx tells it we own the rest. Getting this wrong is how a person ends up charged with nothing
/// credited: return an error for work that is merely still in flight and the provider retries
/// until it gives up. See <see cref="FiatEventDispositions.ToStatusCode"/>.</para>
///
/// <para><b>The body is never an authority.</b> It is stored only as a digest, and every figure a
/// handler acts on is re-read from the provider server-side.</para>
/// </summary>
[ApiController]
[Route("api/fiat/webhook")]
[AllowAnonymous]
[EnableRateLimiting(StripeWebhookController.RateLimitPolicy)]
public sealed class StripeWebhookController : ControllerBase
{
    /// <summary>Dedicated rate-limit policy name; registered in <c>Program.cs</c>.</summary>
    public const string RateLimitPolicy = "fiat-webhook";

    /// <summary>Body cap. Provider events are a few kilobytes of JSON.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    private readonly FiatOptions _options;
    private readonly IStripeWebhookInbox _inbox;
    private readonly IEnumerable<IFiatEventHandler> _handlers;
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly TimeProvider _clock;

    public StripeWebhookController(
        FiatOptions options,
        IStripeWebhookInbox inbox,
        IEnumerable<IFiatEventHandler> handlers,
        ILogger<StripeWebhookController> logger,
        TimeProvider? clock = null)
    {
        _options = options;
        _inbox = inbox;
        _handlers = handlers;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Accepts one signed provider event.</summary>
    [HttpPost("stripe")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Stripe(CancellationToken ct)
    {
        var environment = _options.ActiveEnvironment;
        var secrets = _options.ForEnvironment(environment);

        if (!_options.Enabled || !secrets.IsEnabled || string.IsNullOrWhiteSpace(secrets.WebhookSecret))
        {
            // 500, not 202. A node that cannot verify a signature must not tell the provider the
            // event was delivered — that would silently discard a payment. A 5xx keeps the event
            // in the provider's retry queue while the operator fixes the configuration, and shows
            // up in its dashboard as a failing endpoint.
            _logger.LogError(
                "A payment webhook arrived while the fiat rail is not configured for environment {Environment}.",
                environment);
            return StatusCode(StatusCodes.Status500InternalServerError, Acknowledgement());
        }

        var rawBody = await ReadBoundedBodyAsync(ct);
        if (rawBody is null)
            return BadRequest(Acknowledgement());

        // F-M2: platform and Connect endpoints sign with different secrets; see Providers/Fiat/AGENTS.md §webhook-secrets.
        var signatureHeader = Request.Headers[StripeWebhookSignature.HeaderName].ToString();
        var tolerance = TimeSpan.FromSeconds(_options.WebhookToleranceSeconds <= 0 ? 300 : _options.WebhookToleranceSeconds);
        var verdict = StripeWebhookSignature.Verify(
            rawBody, signatureHeader, secrets.WebhookSecret, _clock.GetUtcNow(), tolerance);
        var distinctConnectSecret = !string.IsNullOrWhiteSpace(secrets.ConnectWebhookSecret)
            && !string.Equals(secrets.ConnectWebhookSecret, secrets.WebhookSecret, StringComparison.Ordinal);
        var signedByConnectEndpoint = false;
        if (verdict != StripeSignatureVerdict.Valid && distinctConnectSecret)
        {
            var connectVerdict = StripeWebhookSignature.Verify(
                rawBody, signatureHeader, secrets.ConnectWebhookSecret, _clock.GetUtcNow(), tolerance);
            if (connectVerdict == StripeSignatureVerdict.Valid)
            {
                verdict = connectVerdict;
                signedByConnectEndpoint = true;
            }
        }

        if (verdict != StripeSignatureVerdict.Valid)
        {
            // The verdict is logged, never returned: an unauthenticated caller must not learn
            // whether it got the secret wrong, the timestamp wrong, or the format wrong.
            _logger.LogWarning("A payment webhook failed signature verification: {Verdict}.", verdict);
            return BadRequest(Acknowledgement());
        }

        var envelope = StripeEventEnvelope.TryParse(rawBody);
        if (envelope is null)
        {
            _logger.LogWarning("A signed payment webhook could not be parsed and was rejected.");
            return BadRequest(Acknowledgement());
        }

        // With a distinct Connect secret configured, each secret is valid only for its own
        // endpoint's events: connected-account events carry `account`, platform events never do.
        if (distinctConnectSecret
            && signedByConnectEndpoint != !string.IsNullOrWhiteSpace(envelope.ProviderAccountId))
        {
            _logger.LogWarning("A signed payment webhook arrived on the wrong endpoint secret and was rejected.");
            return BadRequest(Acknowledgement());
        }

        // A signed event from the WRONG mode is an operator misconfiguration, not an attack: the
        // signature proved the sender. Refusing it is what stops a test-mode event from settling
        // live value (and a live one from being swallowed by a test node). A retry cannot fix a
        // mode mismatch, so this is a 4xx.
        if (envelope.Livemode != (environment.Kind == RailEnvironmentKind.Live))
        {
            _logger.LogError(
                "A payment webhook with livemode={Livemode} arrived on a rail bound to {Environment}; refused.",
                envelope.Livemode, environment);
            return BadRequest(Acknowledgement());
        }

        var admission = await _inbox.AdmitAsync(
            FiatEventFactory.Create(envelope, rawBody, environment, _clock.GetUtcNow()), ct);
        if (admission.IsError || admission.Result is null)
        {
            _logger.LogError("A payment webhook could not be recorded: {Message}", admission.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, Acknowledgement());
        }

        var row = admission.Result.Row;

        // A redelivery of an event already settled is answered from the row. Re-running handlers
        // would be safe (they are idempotent) but pointless, and re-running an Ignored event would
        // churn the provider's retry budget for nothing.
        if (!admission.Result.Admitted
            && (row.Status == FiatEventStatuses.Applied || row.Status == FiatEventStatuses.Ignored))
        {
            return StatusCode(
                row.Status == FiatEventStatuses.Applied
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status202Accepted,
                Acknowledgement());
        }

        var recorded = await FiatEventDispatcher.ApplyAsync(
            row, environment, _inbox, _handlers, _logger, ct);
        if (recorded.IsError)
        {
            // The work may have happened; the bookkeeping did not. A 5xx brings the delivery back,
            // and the handlers are idempotent, so a retry converges. Reporting success here would
            // leave a row that says nothing happened next to effects that did.
            _logger.LogError("A payment webhook outcome could not be recorded: {Message}", recorded.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, Acknowledgement());
        }

        return StatusCode(recorded.Result!.Status switch
        {
            FiatEventStatuses.Applied => StatusCodes.Status200OK,
            FiatEventStatuses.Ignored or FiatEventStatuses.Received => StatusCodes.Status202Accepted,
            _ => StatusCodes.Status500InternalServerError,
        }, Acknowledgement());
    }

    /// <summary>Fixed acknowledgement; identical on every path, so the endpoint is not an oracle.</summary>
    private static AZOAResult<string> Acknowledgement()
        => AZOAResult<string>.Success("accepted", "Accepted.");

    /// <summary>
    /// Reads at most <see cref="MaxBodyBytes"/> of the RAW body. It has to be the raw bytes: the
    /// signature covers them, so any re-serialisation would invalidate it.
    /// </summary>
    private async Task<string?> ReadBoundedBodyAsync(CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(
                Request.Body,
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            var buffer = new char[MaxBodyBytes];
            var read = await reader.ReadBlockAsync(buffer, ct);
            if (read == 0 || read >= MaxBodyBytes)
                return null;

            return new string(buffer, 0, read);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "An inbound payment webhook body could not be read.");
            return null;
        }
    }
}

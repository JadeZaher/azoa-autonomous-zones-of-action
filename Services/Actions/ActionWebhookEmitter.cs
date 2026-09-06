// SPDX-License-Identifier: UNLICENSED

// ─── DI registration (orchestrator applies to Program.cs) ───────────────────────────
//   builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IActionWebhookOutboxStore,
//       AZOA.WebAPI.Providers.Stores.Surreal.SurrealActionWebhookOutboxStore>();
//   builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IActionWebhookEmitter,
//       AZOA.WebAPI.Services.Actions.ActionWebhookEmitter>();
//   // (the shared WebhookRegistration store + SSRF guard + HMAC signer + WebhookOptions
//   //  are ALREADY registered for the consent path; the hosted ActionWebhookDeliveryWorker
//   //  is listed in ActionWebhookDeliveryWorker.cs.)
// ────────────────────────────────────────────────────────────────────────────────────

using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>
/// The thin enqueue seam for the GENERIC action webhook bridge (final-hardening F3 — the
/// generalized mirror of <c>ConsentWebhookEmitter</c>). The <c>EmitNodeHandler</c> calls
/// <see cref="EmitAsync"/> after serializing an <c>Emit</c> node's payload; this builds a
/// <see cref="ActionWebhookEvent"/> and writes it to the outbox via
/// <see cref="IActionWebhookOutboxStore.EnqueueAsync"/>.
///
/// <para><b>No dual-write.</b> This ONLY writes an outbox row — no outbound HTTP. The
/// <c>ActionWebhookDeliveryWorker</c> delivers later out of band with retry + a timestamped
/// per-tenant HMAC.</para>
///
/// <para><b>Best-effort (never throws).</b> A failed enqueue is logged, NOT thrown — the
/// <c>Emit</c> node already produced its authoritative output; a webhook plumbing failure
/// must not fail the node. See <c>Services/Actions/AGENTS.md</c> §action-webhook-emit.</para>
/// </summary>
public sealed class ActionWebhookEmitter : IActionWebhookEmitter
{
    private readonly IActionWebhookOutboxStore _outbox;
    private readonly ILogger<ActionWebhookEmitter> _logger;

    public ActionWebhookEmitter(
        IActionWebhookOutboxStore outbox,
        ILogger<ActionWebhookEmitter> logger)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EmitAsync(
        Guid tenantId,
        string eventType,
        Guid runId,
        Guid nodeId,
        Guid actionId,
        string payloadJson,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var evt = new ActionWebhookEvent
        {
            Id            = Guid.NewGuid(),
            TenantId      = tenantId,
            EventType     = string.IsNullOrWhiteSpace(eventType) ? "action.emit" : eventType,
            RunId         = runId,
            NodeId        = nodeId,
            ActionId       = actionId,
            PayloadJson   = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            OccurredAt    = now,
            Status        = ActionWebhookDeliveryStatus.Pending,
            AttemptCount  = 0,
            NextAttemptAt = now, // due immediately — the worker picks it up on the next scan
            IdempotencyId = Guid.NewGuid().ToString("N"),
            CreatedAt     = now,
        };

        try
        {
            var result = await _outbox.EnqueueAsync(evt, ct);
            if (result.IsError)
            {
                _logger.LogWarning(
                    "Action webhook enqueue failed for run {RunId} node {NodeId} tenant {TenantId} ({EventType}): {Error}. " +
                    "Action-run state is unaffected; the Emit node output remains the authoritative settlement surface.",
                    runId, nodeId, tenantId, evt.EventType, result.Message);
            }
            else
            {
                _logger.LogInformation(
                    "Action webhook enqueued: {EventType} run {RunId} node {NodeId} tenant {TenantId} idempotency {IdempotencyId}.",
                    evt.EventType, runId, nodeId, tenantId, evt.IdempotencyId);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a plumbing exception must never bubble out of the Emit node.
            _logger.LogWarning(ex,
                "Action webhook enqueue threw for run {RunId} node {NodeId} tenant {TenantId} — swallowed; the Emit node still succeeds.",
                runId, nodeId, tenantId);
        }
    }
}

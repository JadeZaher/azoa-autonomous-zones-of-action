// SPDX-License-Identifier: UNLICENSED

// ─── DI registration (orchestrator applies to Program.cs) ───────────────────────────
//   builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IQuestWebhookOutboxStore,
//       AZOA.WebAPI.Providers.Stores.Surreal.SurrealQuestWebhookOutboxStore>();
//   builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IQuestWebhookEmitter,
//       AZOA.WebAPI.Services.Quest.QuestWebhookEmitter>();
//   // (the shared WebhookRegistration store + SSRF guard + HMAC signer + WebhookOptions
//   //  are ALREADY registered for the consent path; the hosted QuestWebhookDeliveryWorker
//   //  is listed in QuestWebhookDeliveryWorker.cs.)
// ────────────────────────────────────────────────────────────────────────────────────

using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// The thin enqueue seam for the GENERIC quest webhook bridge (final-hardening F3 — the
/// generalized mirror of <c>ConsentWebhookEmitter</c>). The <c>EmitNodeHandler</c> calls
/// <see cref="EmitAsync"/> after serializing an <c>Emit</c> node's payload; this builds a
/// <see cref="QuestWebhookEvent"/> and writes it to the outbox via
/// <see cref="IQuestWebhookOutboxStore.EnqueueAsync"/>.
///
/// <para><b>No dual-write.</b> This ONLY writes an outbox row — no outbound HTTP. The
/// <c>QuestWebhookDeliveryWorker</c> delivers later out of band with retry + a timestamped
/// per-tenant HMAC.</para>
///
/// <para><b>Best-effort (never throws).</b> A failed enqueue is logged, NOT thrown — the
/// <c>Emit</c> node already produced its authoritative output; a webhook plumbing failure
/// must not fail the node. See <c>Services/Quest/AGENTS.md</c> §quest-webhook-emit.</para>
/// </summary>
public sealed class QuestWebhookEmitter : IQuestWebhookEmitter
{
    private readonly IQuestWebhookOutboxStore _outbox;
    private readonly ILogger<QuestWebhookEmitter> _logger;

    public QuestWebhookEmitter(
        IQuestWebhookOutboxStore outbox,
        ILogger<QuestWebhookEmitter> logger)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EmitAsync(
        Guid tenantId,
        string eventType,
        Guid runId,
        Guid nodeId,
        Guid questId,
        string payloadJson,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var evt = new QuestWebhookEvent
        {
            Id            = Guid.NewGuid(),
            TenantId      = tenantId,
            EventType     = string.IsNullOrWhiteSpace(eventType) ? "quest.emit" : eventType,
            RunId         = runId,
            NodeId        = nodeId,
            QuestId       = questId,
            PayloadJson   = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            OccurredAt    = now,
            Status        = QuestWebhookDeliveryStatus.Pending,
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
                    "Quest webhook enqueue failed for run {RunId} node {NodeId} tenant {TenantId} ({EventType}): {Error}. " +
                    "Quest-run state is unaffected; the Emit node output remains the authoritative settlement surface.",
                    runId, nodeId, tenantId, evt.EventType, result.Message);
            }
            else
            {
                _logger.LogInformation(
                    "Quest webhook enqueued: {EventType} run {RunId} node {NodeId} tenant {TenantId} idempotency {IdempotencyId}.",
                    evt.EventType, runId, nodeId, tenantId, evt.IdempotencyId);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a plumbing exception must never bubble out of the Emit node.
            _logger.LogWarning(ex,
                "Quest webhook enqueue threw for run {RunId} node {NodeId} tenant {TenantId} — swallowed; the Emit node still succeeds.",
                runId, nodeId, tenantId);
        }
    }
}

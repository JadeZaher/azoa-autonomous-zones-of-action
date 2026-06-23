// SPDX-License-Identifier: UNLICENSED

// ─── DI registration (orchestrator applies to Program.cs) ───────────────────────────
//   builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IConsentWebhookOutboxStore,
//       AZOA.WebAPI.Providers.Stores.Surreal.SurrealConsentWebhookOutboxStore>();
//   builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IWebhookRegistrationStore,
//       AZOA.WebAPI.Providers.Stores.Surreal.SurrealWebhookRegistrationStore>();
//   builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IConsentWebhookEmitter,
//       AZOA.WebAPI.Managers.ConsentWebhookEmitter>();
// (The outbox store + the hosted delivery worker + HttpClient registration are listed
//  in ConsentWebhookDeliveryWorker.cs.)
// ────────────────────────────────────────────────────────────────────────────────────

using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// The thin enqueue seam for the consent webhook bridge (tenant-consent-delegation §4,
/// AC7). ConsentManager calls <see cref="EmitAsync"/> right after a grant/revoke state
/// change; this builds a <see cref="ConsentWebhookEvent"/> from the grant and writes it
/// to the outbox via <see cref="IConsentWebhookOutboxStore.EnqueueAsync"/>.
///
/// <para><b>No dual-write (AC7).</b> This ONLY writes an outbox row — no outbound HTTP.
/// Invoked in the SAME logical transaction as the grant Upsert, so the event and the
/// state change land together; the <c>ConsentWebhookDeliveryWorker</c> delivers later
/// out of band.</para>
///
/// <para><b>Observe-only (AC8).</b> The emit result NEVER changes consent validity — the
/// signing seam is the sole enforcement path. A failed enqueue does not resurrect or
/// invalidate a grant; it simply means the (best-effort) notification was not queued,
/// which AC10's independent audit row covers.</para>
/// </summary>
public sealed class ConsentWebhookEmitter : IConsentWebhookEmitter
{
    private readonly IConsentWebhookOutboxStore _outbox;
    private readonly ILogger<ConsentWebhookEmitter> _logger;

    public ConsentWebhookEmitter(
        IConsentWebhookOutboxStore outbox,
        ILogger<ConsentWebhookEmitter> logger)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EmitAsync(ConsentWebhookEventType type, ConsentGrant grant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);

        var now = DateTime.UtcNow;
        var evt = new ConsentWebhookEvent
        {
            Id               = Guid.NewGuid(),
            TenantId         = grant.TenantId,
            EventType        = type,
            GrantId          = grant.Id,
            // avatarId = the grantor (the USER who granted) — the payload's `avatarId`.
            AvatarId         = grant.GrantorAvatarId,
            // Scopes serialized as CSV to mirror the consent_grant store's column shape.
            Scopes           = string.Join(',', grant.Scopes ?? new List<string>()),
            ParticipationRef = grant.ParticipationRef,
            OccurredAt       = now,
            Status           = ConsentWebhookDeliveryStatus.Pending,
            AttemptCount     = 0,
            NextAttemptAt    = now, // due immediately — the worker picks it up on the next scan
            // A fresh stable per-event idempotency id sent to the receiver for dedup.
            IdempotencyId    = Guid.NewGuid().ToString("N"),
            CreatedAt        = now,
        };

        var result = await _outbox.EnqueueAsync(evt, ct);
        if (result.IsError)
        {
            // Best-effort notification: a failed enqueue is logged, NOT thrown — the
            // grant state change (and its independent AC10 audit row) already happened
            // and MUST NOT be undone by a webhook plumbing failure (AC8 observe-only).
            _logger.LogWarning(
                "Consent webhook enqueue failed for grant {GrantId} ({EventType}) tenant {TenantId}: {Error}. " +
                "Consent state is unaffected; the signing seam remains the enforcement path.",
                grant.Id, type, grant.TenantId, result.Message);
        }
        else
        {
            _logger.LogInformation(
                "Consent webhook enqueued: {EventType} grant {GrantId} tenant {TenantId} idempotency {IdempotencyId}.",
                evt.WireEventType, grant.Id, grant.TenantId, evt.IdempotencyId);
        }
    }
}

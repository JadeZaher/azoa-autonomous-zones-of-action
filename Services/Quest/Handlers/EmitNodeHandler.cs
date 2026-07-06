using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Handles <see cref="QuestNodeType.Emit"/>. Serializes the tenant-shaped
/// <see cref="EmitNodeConfig.Payload"/> directly to
/// <see cref="QuestNodeExecution.Output"/> so the consuming tenant system
/// can read it and settle tenant-side.
/// </summary>
/// <remarks>
/// <para>The node output is the authoritative settlement surface — no fiat/payout math
/// runs in AZOA. <c>RequiresChainCapability</c> stays <see langword="false"/> (D8).</para>
///
/// <para><b>quest.emit webhook (final-hardening F3).</b> When an
/// <see cref="IQuestWebhookEmitter"/> is registered AND the run carries an
/// <c>ActingTenantId</c>, the handler ALSO enqueues a best-effort webhook outbox event
/// (the generalized mirror of the consent outbox). The emit is fire-and-forget from the
/// node's perspective: it never fails the node, and the pure pass-through output is
/// unchanged. The emitter is an OPTIONAL constructor dependency (nullable, default
/// <see langword="null"/>) so the handler stays constructable with no dependencies and
/// the pure-passthrough path is preserved when no tenant/emitter is present. See
/// <c>Services/Quest/AGENTS.md</c> §quest-webhook-emit.</para>
/// </remarks>
public sealed class EmitNodeHandler : IQuestNodeHandler
{
    private readonly IQuestWebhookEmitter? _webhookEmitter;

    public EmitNodeHandler(IQuestWebhookEmitter? webhookEmitter = null)
    {
        _webhookEmitter = webhookEmitter;
    }

    public QuestNodeType NodeType => QuestNodeType.Emit;

    public async Task<QuestNodeHandlerResult> HandleAsync(
        QuestNodeExecutionContext context,
        CancellationToken ct = default)
    {
        // Emit is forward-compatible: unknown config keys are silently skipped at runtime.
        // Definition-time strict validation is still enforced via QuestNodeConfigRegistry.
        if (!QuestNodeConfig.TryDeserialize<EmitNodeConfig>(context.Node.Config, nameof(QuestNodeType.Emit), out var cfg, out var cfgError, strict: false))
            return QuestNodeResults.Fail(cfgError);

        // Gracefully handle a missing or undefined payload — emit empty object.
        var outputJson = cfg.Payload.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : JsonSerializer.Serialize(cfg.Payload, QuestNodeJson.Options);

        // Best-effort quest.emit webhook: only when a tenant drove the run AND an emitter
        // is wired. Never affects the node result — the emitter itself never throws, but
        // the guard keeps the pure path allocation-free when no tenant is present.
        if (_webhookEmitter is not null && context.ActingTenantId is Guid tenantId && tenantId != Guid.Empty)
        {
            await _webhookEmitter.EmitAsync(
                tenantId: tenantId,
                eventType: cfg.EventType ?? "quest.emit",
                runId: context.RunId,
                nodeId: context.NodeId,
                questId: context.Quest.Id,
                payloadJson: outputJson,
                ct: ct);
        }

        return QuestNodeResults.Ok(outputJson);
    }
}

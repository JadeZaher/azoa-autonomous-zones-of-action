using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Actions.Handlers;

/// <summary>
/// Handles <see cref="ActionNodeType.Emit"/>. Serializes the tenant-shaped
/// <see cref="EmitNodeConfig.Payload"/> directly to
/// <see cref="ActionNodeExecution.Output"/> so the consuming tenant system
/// can read it and settle tenant-side.
/// </summary>
/// <remarks>
/// <para>The node output is the authoritative settlement surface — no fiat/payout math
/// runs in AZOA. <c>RequiresChainCapability</c> stays <see langword="false"/> (D8).</para>
///
/// <para><b>action.emit webhook (final-hardening F3).</b> When an
/// <see cref="IActionWebhookEmitter"/> is registered AND the run carries an
/// <c>ActingTenantId</c>, the handler ALSO enqueues a best-effort webhook outbox event
/// (the generalized mirror of the consent outbox). The emit is fire-and-forget from the
/// node's perspective: it never fails the node, and the pure pass-through output is
/// unchanged. The emitter is an OPTIONAL constructor dependency (nullable, default
/// <see langword="null"/>) so the handler stays constructable with no dependencies and
/// the pure-passthrough path is preserved when no tenant/emitter is present. See
/// <c>Services/Actions/AGENTS.md</c> §action-webhook-emit.</para>
/// </remarks>
public sealed class EmitNodeHandler : IActionNodeHandler
{
    private readonly IActionWebhookEmitter? _webhookEmitter;

    public EmitNodeHandler(IActionWebhookEmitter? webhookEmitter = null)
    {
        _webhookEmitter = webhookEmitter;
    }

    public ActionNodeType NodeType => ActionNodeType.Emit;

    public async Task<ActionNodeHandlerResult> HandleAsync(
        ActionNodeExecutionContext context,
        CancellationToken ct = default)
    {
        // Emit is forward-compatible: unknown config keys are silently skipped at runtime.
        // Definition-time strict validation is still enforced via ActionNodeConfigRegistry.
        if (!ActionNodeConfig.TryDeserialize<EmitNodeConfig>(context.Node.Config, nameof(ActionNodeType.Emit), out var cfg, out var cfgError, strict: false))
            return ActionNodeResults.Fail(cfgError);

        // Gracefully handle a missing or undefined payload — emit empty object.
        var outputJson = cfg.Payload.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : JsonSerializer.Serialize(cfg.Payload, ActionNodeJson.Options);

        // Best-effort action.emit webhook: only when a tenant drove the run AND an emitter
        // is wired. Never affects the node result — the emitter itself never throws, but
        // the guard keeps the pure path allocation-free when no tenant is present.
        if (_webhookEmitter is not null && context.ActingTenantId is Guid tenantId && tenantId != Guid.Empty)
        {
            await _webhookEmitter.EmitAsync(
                tenantId: tenantId,
                eventType: cfg.EventType ?? "action.emit",
                runId: context.RunId,
                nodeId: context.NodeId,
                actionId: context.ActionDefinition.Id,
                payloadJson: outputJson,
                ct: ct);
        }

        return ActionNodeResults.Ok(outputJson);
    }
}

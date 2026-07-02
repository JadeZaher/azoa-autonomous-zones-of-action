using System.Text.Json;
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
/// Pure pass-through — no webhook, no settlement, no fiat/payout math.
/// All economic computation stays in the tenant system; AZOA only holds
/// the serialized payload. <c>RequiresChainCapability</c> stays
/// <see langword="false"/> (D8).
/// </remarks>
public sealed class EmitNodeHandler : IQuestNodeHandler
{
    public QuestNodeType NodeType => QuestNodeType.Emit;

    public Task<QuestNodeHandlerResult> HandleAsync(
        QuestNodeExecutionContext context,
        CancellationToken ct = default)
    {
        // Emit is forward-compatible: unknown config keys are silently skipped at runtime.
        // Definition-time strict validation is still enforced via QuestNodeConfigRegistry.
        if (!QuestNodeConfig.TryDeserialize<EmitNodeConfig>(context.Node.Config, nameof(QuestNodeType.Emit), out var cfg, out var cfgError, strict: false))
            return Task.FromResult(QuestNodeResults.Fail(cfgError));

        // Gracefully handle a missing or undefined payload — emit empty object.
        if (cfg.Payload.ValueKind == JsonValueKind.Undefined)
            return Task.FromResult(QuestNodeResults.Ok("{}"));

        var outputJson = JsonSerializer.Serialize(cfg.Payload, QuestNodeJson.Options);
        return Task.FromResult(QuestNodeResults.Ok(outputJson));
    }
}

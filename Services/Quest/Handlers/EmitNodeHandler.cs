using System.Text.Json;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;

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
        var cfg = JsonSerializer.Deserialize<EmitNodeConfig>(
            context.Node.Config, QuestNodeJson.Options);

        // Gracefully handle a missing or undefined payload — emit empty object.
        if (cfg is null || cfg.Payload.ValueKind == JsonValueKind.Undefined)
            return Task.FromResult(QuestNodeResults.Ok("{}"));

        var outputJson = JsonSerializer.Serialize(cfg.Payload, QuestNodeJson.Options);
        return Task.FromResult(QuestNodeResults.Ok(outputJson));
    }
}

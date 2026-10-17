using System.Text.Json;

namespace AZOA.WebAPI.Models.Actions;

/// <summary>Exact effective node input captured before admission.</summary>
public sealed record ActionFrozenNodeInput(int Version, Guid RunId, Guid NodeId, ActionNodeType NodeType,
    Guid ActorUserId, Guid? ActingTenantId, JsonElement Config,
    IReadOnlyDictionary<Guid, ActionNodeExecution> Upstream,
    IReadOnlyDictionary<Guid, ActionNodeExecution> AllExecutions);

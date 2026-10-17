using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("node_governance_policy_lock", Aggregate = "Policy admission serialization", Guardrail = "Singleton transaction lock")]
[Slice("identity")]
public sealed class NodeGovernancePolicyLock : ISurrealRecord
{
    public string SchemaName => "node_governance_policy_lock";
    [Id] public string Id { get; set; } = "local";
    public string Token { get; set; } = string.Empty;
}

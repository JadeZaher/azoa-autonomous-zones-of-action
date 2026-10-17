using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("local_entity_mutation_lock", Aggregate = "Local graph mutation serialization", Guardrail = "Server-owned table locks")]
[Slice("action")]
public sealed class LocalEntityMutationLock : ISurrealRecord
{
    public string SchemaName => "local_entity_mutation_lock";
    [Id] public string Id { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

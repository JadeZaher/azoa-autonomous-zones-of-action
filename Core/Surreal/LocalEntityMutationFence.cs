using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Serializes local entity mutations with bounded graph query witnesses.</summary>
public static class LocalEntityMutationFence
{
    public static SurrealQuery Holons() => Lock("holon");
    public static SurrealQuery Wallets() => Lock("wallet");

    private static SurrealQuery Lock(string entity) => SurrealGeneratedStatementScope.Apply(
        SurrealWriter.Upsert(new LocalEntityMutationLock { Id = entity, Token = Guid.NewGuid().ToString("N") }),
        "local_lock_" + entity);
}

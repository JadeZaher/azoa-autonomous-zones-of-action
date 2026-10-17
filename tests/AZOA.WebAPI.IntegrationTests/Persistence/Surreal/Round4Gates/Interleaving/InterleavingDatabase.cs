using SurrealForge.Client;
using SurrealForge.Client.Connection;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;

/// <summary>Per-class SurrealDB namespace with the real generated goldens for the tables an interleaving fixture races on.</summary>
internal sealed class InterleavingDatabase : IAsyncDisposable
{
    private readonly ISurrealConnection _connection;

    private InterleavingDatabase(string ns, ISurrealConnection connection)
    {
        Namespace = ns;
        _connection = connection;
        Executor = new DefaultSurrealExecutor(connection);
    }

    public string Namespace { get; }

    public ISurrealExecutor Executor { get; }

    public static async Task<InterleavingDatabase> CreateAsync(params string[] tables)
    {
        var ns = $"test{Guid.NewGuid():N}";
        await SurrealTestSchema.BootstrapAsync(ns, tables);
        return new(ns, new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = ns, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        }));
    }

    public async ValueTask DisposeAsync()
    {
        try { await SurrealTestSchema.DropAsync(Namespace); }
        finally { _connection.Dispose(); }
    }
}

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.DataCompatibility;

/// <summary>P2 sandbox data-compatibility rehearsal: one model-valid row per table is accepted, read and re-written under the generated schema, and every declared UNIQUE index exists; see Fixtures/UpgradeRehearsal/AGENTS.md.</summary>
public sealed class GeneratedSchemaDataCompatibilityTests(GeneratedSchemaDataCompatibilityTests.Sandbox sandbox)
    : IClassFixture<GeneratedSchemaDataCompatibilityTests.Sandbox>
{
    private ISurrealExecutor Executor => sandbox.Executor;

    public static IEnumerable<object[]> Tables() => WritableTables().Select(type => new object[] { TableName(type) });

    /// <summary>One namespace for the class: the rehearsal sandbox (deploy path already applied) or every golden.</summary>
    public sealed class Sandbox : IAsyncLifetime
    {
        // The rehearsal script points these at its sandbox; CI bootstraps the goldens.
        private static readonly string Endpoint = Environment.GetEnvironmentVariable("AZOA_REHEARSAL_ENDPOINT") ?? SurrealTestDefaults.Endpoint;
        private static readonly string? SandboxNamespace = Environment.GetEnvironmentVariable("AZOA_REHEARSAL_NAMESPACE");
        private string _namespace = "";
        private ISurrealConnection? _connection;

        public ISurrealExecutor Executor { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            if (SandboxNamespace is null)
            {
                _namespace = $"test{Guid.NewGuid():N}";
                await SurrealTestSchema.BootstrapAsync(_namespace, Pocos().Select(TableName).ToArray());
            }
            else _namespace = SandboxNamespace;
            _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
            {
                Endpoint = Endpoint, Namespace = _namespace,
                Database = SandboxNamespace is null ? "test" : Environment.GetEnvironmentVariable("AZOA_REHEARSAL_DATABASE") ?? "azoa",
                User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
            });
            Executor = new DefaultSurrealExecutor(_connection);
        }

        public async Task DisposeAsync()
        {
            if (SandboxNamespace is null) await SurrealTestSchema.DropAsync(_namespace);
            _connection?.Dispose();
        }
    }

    [Fact]
    public void Every_table_has_a_committed_golden()
        => Pocos().Select(TableName).Where(table => !File.Exists(Path.Combine(SchemaDirectory(), table + ".surql")))
            .Should().BeEmpty("the rehearsal applies exactly the generated set the deploy path applies");

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task A_model_valid_row_is_accepted_read_back_and_rewritten(string table)
    {
        var type = WritableTables().Single(candidate => TableName(candidate) == table);
        var check = typeof(GeneratedSchemaDataCompatibilityTests).GetMethod(nameof(RoundTripAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(type);
        await (Task)check.Invoke(this, [])!;
    }

    [Fact]
    public async Task Every_declared_unique_index_exists()
    {
        var missing = new List<string>();
        foreach (var type in Pocos())
            foreach (var index in type.GetCustomAttributes<IndexAttribute>().Concat(type.GetProperties().SelectMany(property => property.GetCustomAttributes<IndexAttribute>()))
                         .Where(index => index.Unique))
            {
                var info = await Executor.ExecuteAsync(SurrealQuery.Of("INFO FOR TABLE $_table").WithParam("_table", TableName(type)));
                info.EnsureAllOk();
                if (!JsonSerializer.Serialize(info.GetValues<JsonElement>(0)).Contains($"\"{index.Name}\"", StringComparison.Ordinal))
                    missing.Add($"{TableName(type)}.{index.Name}");
            }
        missing.Should().BeEmpty("every single-winner invariant in the unique-index audit is enforced by its generated UNIQUE index");
    }

    private async Task RoundTripAsync<T>() where T : class, ISurrealRecord, new()
    {
        var row = ModelValidRow.Fill(new T());
        var id = IdProperty(typeof(T)).GetValue(row)!.ToString()!;
        (await Executor.ExecuteAsync(SurrealWriter.Create(row))).EnsureAllOk();
        var stored = await Executor.QuerySingleAsync<T>(SurrealQuery<T>.Key(id));
        stored.Should().NotBeNull($"{typeof(T).Name} written by SurrealWriter must read back by key");
        (await Executor.ExecuteAsync(SurrealWriter.Upsert(stored!))).EnsureAllOk();
    }

    private static IEnumerable<Type> Pocos()
        => typeof(Program).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<SurrealTableAttribute>() is not null && typeof(ISurrealRecord).IsAssignableFrom(type)
                && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null
                && File.Exists(Path.Combine(SchemaDirectory(), TableName(type) + ".surql")))
            .OrderBy(TableName);

    /// <summary>Tables a POCO alone can write: not index-only ([VirtualTable]), not a RELATE edge, no CLR-less [ExtraSurrealField] column (stores write those through their own wire rows).</summary>
    private static IEnumerable<Type> WritableTables()
        => Pocos().Where(type => !type.GetCustomAttributes().Any(attribute => attribute.GetType().Name is "VirtualTableAttribute" or "ExtraSurrealFieldAttribute")
            && !File.ReadAllText(Path.Combine(SchemaDirectory(), TableName(type) + ".surql")).Contains("TYPE RELATION", StringComparison.Ordinal));

    private static string TableName(Type type) => type.GetCustomAttribute<SurrealTableAttribute>()?.Name ?? type.Name;

    private static PropertyInfo IdProperty(Type type)
        => type.GetProperties().FirstOrDefault(property => property.GetCustomAttribute<IdAttribute>() is not null) ?? type.GetProperty("Id")!;

    private static string SchemaDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AZOA.WebAPI.csproj"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "Persistence", "SurrealDb", "Generated", "Schemas");
    }
}

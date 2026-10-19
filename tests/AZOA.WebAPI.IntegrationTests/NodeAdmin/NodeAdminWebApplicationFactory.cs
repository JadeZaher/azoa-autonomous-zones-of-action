using AZOA.WebAPI.IntegrationTests.Persistence.Surreal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace AZOA.WebAPI.IntegrationTests.NodeAdmin;

/// <summary>Real-auth host for node-admin flows: production JWT validation (no TestAuthHandler), a fresh namespace and a generated root key.</summary>
public class NodeAdminWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string Audience = "https://node.example.test";

    /// <summary>The host's <c>Jwt:Key</c>, so a flow can re-sign a captured token.</summary>
    public const string JwtKey = "node-admin-integration-test-signing-key-0123456789";

    private static readonly string[] Tables =
    [
        "admin_bootstrap_state", "saga_steps", "user", "api_key", "node_admin", "node_admin_audit", "node_admin_audit_head", "node_admin_challenge",
        "node_admin_credential", "node_admin_directory", "node_admin_enrollment", "node_admin_role", "node_admin_root_state",
        "node_admin_session",
    ];

    public string TestNamespace { get; } = $"itest{Guid.NewGuid():N}";

    /// <summary>The break-glass key whose public half is configured as <c>NodeAdmin:RootPublicKeys</c>.</summary>
    public NodeAdminTestKey RootKey { get; } = NodeAdminTestKey.Generate();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        SurrealTestSchema.BootstrapAsync(TestNamespace, [.. Tables, .. ExtraTables]).GetAwaiter().GetResult();
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("IntegrationTest");
        builder.UseSetting("OpenTelemetry:ExportEnabled", "false");
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["SurrealDb:Endpoint"] = SurrealTestDefaults.Endpoint,
            ["SurrealDb:User"] = SurrealTestDefaults.User,
            ["SurrealDb:Password"] = SurrealTestDefaults.Password,
            ["SurrealDb:Namespace"] = TestNamespace,
            ["SurrealDb:Database"] = "test",
            ["Jwt:Key"] = JwtKey,
            ["Jwt:Issuer"] = "test",
            ["Jwt:Audience"] = "test",
            ["NodeAdmin:RootPublicKeys"] = RootKey.PublicKey,
            ["NodeAdmin:RootKeyRevision"] = "1",
            ["NodeAdmin:Audience"] = Audience,
            ["RateLimiting:OperatorLogin:PermitLimit"] = "1000",
            ["AZOA:WalletEncryptionKey"] = "node-admin-integration-test-wallet-key-0123456789",
        }.Concat(ExtraSettings))
            builder.UseSetting(key, value);
    }

    /// <summary>Additional table goldens a derived host's flows touch.</summary>
    protected virtual IEnumerable<string> ExtraTables => [];

    /// <summary>Additional host settings a derived host needs.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string>> ExtraSettings => [];

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await SurrealTestSchema.DropAsync(TestNamespace);
    }
}

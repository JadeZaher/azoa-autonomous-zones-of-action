namespace OASIS.WebAPI.IntegrationTests;

/// <summary>
/// Connection defaults for SurrealDB-backed integration tests.
/// Points at the developer's local SurrealDB instance — same endpoint
/// and credentials as <c>appsettings.Development.json</c>'s
/// <c>SurrealDb</c> section. No env-var indirection: a single source
/// of truth keeps test discovery, factory wiring, and direct-HTTP
/// fixtures aligned.
/// </summary>
internal static class SurrealTestDefaults
{
    public const string Endpoint = "http://127.0.0.1:8000";
    public const string User     = "root";
    public const string Password = "root";
}

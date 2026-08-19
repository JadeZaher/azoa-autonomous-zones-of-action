namespace AZOA.WebAPI.IntegrationTests;

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
    public const string Endpoint = "http://127.0.0.1:8020";

    /// <summary>
    /// The same server as <see cref="Endpoint"/>, addressed from INSIDE the
    /// container. backup.ps1 / restore.ps1 run `surreal export|import` as a
    /// subprocess in the container, where the host port publication does not
    /// exist and the server is always on 8000. Passing the host endpoint only
    /// worked while the published port happened to equal the container port.
    /// </summary>
    public const string InContainerEndpoint = "http://localhost:8000";
    public const string User     = "root";
    public const string Password = "root";
}

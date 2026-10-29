namespace AZOA.WebAPI.IntegrationTests;

/// <summary>
/// Connection defaults for SurrealDB-backed integration tests: the CI server on 8020 unless
/// <c>AZOA_TEST_SURREAL_ENDPOINT</c> points the run at an isolated one. See
/// <c>tests/AZOA.WebAPI.IntegrationTests/AGENTS.md</c> §cbor-transport.
/// </summary>
internal static class SurrealTestDefaults
{
    /// <summary>Environment variable that redirects every Surreal-backed test to another server.</summary>
    public const string EndpointVariable = "AZOA_TEST_SURREAL_ENDPOINT";

    /// <summary>The endpoint CI publishes its SurrealDB container on.</summary>
    public const string DefaultEndpoint = "http://127.0.0.1:8020";

    public static readonly string Endpoint =
        Environment.GetEnvironmentVariable(EndpointVariable) is { Length: > 0 } overridden
            ? overridden.Trim()
            : DefaultEndpoint;

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

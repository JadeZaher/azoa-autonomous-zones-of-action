// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Client -- top-level configuration for the toolkit.
//
// Composes:
//   - Connection (runtime: URL, auth, namespace/database, retry knobs)
//   - Generation (build-time: where the generator writes .surql / .mermaid /
//     .dbml + the namespace into which derived twin partial classes land)
//
// Wired through Microsoft.Extensions.Configuration with the canonical key
// `SurrealDb` so consumers register via:
//
//   services.Configure<OasisSurrealDbOptions>(Configuration.GetSection("SurrealDb"));
//
// Environment variable mirrors (see OASIS_SURREAL_* in the CLI) override the
// JSON-config values at runtime; the canonical resolution order is documented
// alongside each field.

#nullable enable

namespace Oasis.SurrealDb.Client.Schema
{
    /// <summary>
    /// Top-level configuration for the Oasis.SurrealDb toolkit. Single entry
    /// point binding both the runtime client (connection + auth) and the
    /// build-time generator (output paths + namespaces).
    /// </summary>
    public sealed class OasisSurrealDbOptions
    {
        /// <summary>
        /// Configuration-binding key under which an <see cref="OasisSurrealDbOptions"/>
        /// is expected to live in <c>appsettings.json</c>. Hard-coded so
        /// consumers and the CLI agree on the location.
        /// </summary>
        public const string ConfigSectionName = "SurrealDb";

        /// <summary>Runtime connection + auth + retry settings.</summary>
        public ConnectionOptions Connection { get; set; } = new();

        /// <summary>Build-time generator output paths + namespaces.</summary>
        public GenerationOptions Generation { get; set; } = new();
    }

    /// <summary>
    /// Connection-level configuration. Mirrors <see cref="SurrealConnectionOptions"/>
    /// (the lower-level transport options) but exposes a slimmer surface so
    /// the consumer's appsettings.json does not need to carry every retry knob.
    /// </summary>
    public sealed class ConnectionOptions
    {
        /// <summary>
        /// Base HTTP endpoint (e.g. <c>http://localhost:8442</c>). Env override:
        /// <c>OASIS_SURREAL_URL</c>.
        /// </summary>
        public string Url { get; set; } = "http://localhost:8442";

        /// <summary>Basic-auth user. Env override: <c>OASIS_SURREAL_USER</c>.</summary>
        public string Username { get; set; } = "";

        /// <summary>Basic-auth password. Env override: <c>OASIS_SURREAL_PASS</c>.</summary>
        public string Password { get; set; } = "";

        /// <summary>SurrealDB namespace. Env override: <c>OASIS_SURREAL_NS</c>.</summary>
        public string Namespace { get; set; } = "oasis";

        /// <summary>SurrealDB database. Env override: <c>OASIS_SURREAL_DB</c>.</summary>
        public string Database { get; set; } = "oasis";

        /// <summary>
        /// Optional JWT bearer token. When supplied, this is sent as
        /// <c>Authorization: Bearer ...</c> in preference to basic auth.
        /// Env override: <c>OASIS_SURREAL_JWT</c>.
        /// </summary>
        public string? JwtToken { get; set; }

        /// <summary>
        /// Optional API key passed as <c>X-Api-Key</c>. Used by the
        /// Oasis.WebAPI surface for service-to-service auth. Env override:
        /// <c>OASIS_SURREAL_API_KEY</c>.
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>
        /// Project this slim shape onto the lower-level transport options
        /// the connection pool consumes. Single place for the mapping so
        /// the toolkit surface stays flat for callers.
        /// </summary>
        public SurrealConnectionOptions ToTransportOptions() => new()
        {
            Endpoint = Url,
            User = Username,
            Password = Password,
            Namespace = Namespace,
            Database = Database,
        };
    }

    /// <summary>
    /// Build-time generator output settings. Read by the
    /// <c>oasis-surreal generate</c> / <c>flowcharts</c> CLI subcommands
    /// (and by the source-gen at compile time) to decide where to drop
    /// derived artifacts.
    /// </summary>
    public sealed class GenerationOptions
    {
        /// <summary>
        /// Root directory under which the generator writes every derived
        /// artifact. Subdirectories: <c>Schemas/</c>, <c>Flowcharts/</c>,
        /// <c>Dbml/</c>. Default: <c>Persistence/SurrealDb/Generated/</c>.
        /// </summary>
        public string GeneratedPath { get; set; } = "Persistence/SurrealDb/Generated";

        /// <summary>
        /// Reserved for future use. If enabled via
        /// <see cref="EmitTwinPartialClasses"/>, the generator emits derived
        /// twin partial classes into this namespace alongside the
        /// hand-authored POCOs. Default empty -- the C#-first authoring
        /// surface is canonical and twin classes are not emitted.
        /// </summary>
        public string GeneratedNamespace { get; set; } = "";

        /// <summary>Emit <c>.surql</c> DDL files. Default true.</summary>
        public bool EmitSurql { get; set; } = true;

        /// <summary>
        /// Emit <c>DEFINE TABLE/FIELD/INDEX IF NOT EXISTS</c> so each
        /// <c>.surql</c> can be re-applied to keep the deployed DB in sync
        /// with the schema (CREATE-or-leave-alone semantics). Default
        /// <c>true</c>; flip to <c>false</c> for first-deploy strictness.
        /// </summary>
        public bool IdempotentSurql { get; set; } = true;

        /// <summary>
        /// Emit per-slice + master <c>graph LR</c> flowchart Mermaid files.
        /// Default true.
        /// </summary>
        public bool EmitFlowcharts { get; set; } = true;

        /// <summary>
        /// Emit a single <c>schema.dbml</c> diff manifest. Default false
        /// (pending the surrealql-drift-detection track).
        /// </summary>
        public bool EmitDbml { get; set; } = false;

        /// <summary>
        /// Reserved for future use. Default false -- the C#-first authoring
        /// surface is canonical and twin classes are not emitted.
        /// </summary>
        public bool EmitTwinPartialClasses { get; set; } = false;
    }
}

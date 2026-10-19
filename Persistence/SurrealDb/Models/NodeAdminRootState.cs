// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_admin_root_state singleton (node-admin-rbac-cli).

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("node_admin_root_state",
    Aggregate = "NodeAdminRootState (Persistence/SurrealDb/Models/NodeAdminRootState.cs)",
    Guardrail = "G6 SCHEMAFULL; break-glass root key revision watermark")]
[SurrealNote("Singleton (id = local). root_key_revision only moves forward with NodeAdmin__RootKeyRevision; a lower env revision disables root login instead of rolling back. root_session_revision ends every root session when bumped. No key material is stored.")]
[Slice("identity")]
public partial class NodeAdminRootState : ISurrealRecord
{
    public const string SchemaNameConst = "node_admin_root_state";
    public const string SingletonId = "local";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = SingletonId;

    [Column(Order = 2)]
    [Default("0")]
    public long RootKeyRevision { get; set; }

    [Column(Order = 3)]
    [Default("0")]
    public long RootSessionRevision { get; set; }

    [Column(Order = 4)]
    public DateTimeOffset UpdatedAt { get; set; }
}

// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_admin_role table (node-admin-rbac-cli).

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("node_admin_role",
    Aggregate = "NodeAdminRole (Persistence/SurrealDb/Models/NodeAdminRole.cs)",
    Guardrail = "G6 SCHEMAFULL; node-admin RBAC role")]
[SurrealNote("Built-in roles are seeded idempotently at boot and immutable. permissions is validated against the closed Core/NodeAdminPermissions catalog on every write.")]
[Slice("identity")]
[Index("node_admin_role_name", Fields = new[] { "name" }, Unique = true)]
public partial class NodeAdminRole : ISurrealRecord
{
    public const string SchemaNameConst = "node_admin_role";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Column(Order = 2)]
    [Required(NotEmpty = true)]
    public string Name { get; set; } = string.Empty;

    [Column(Order = 3)]
    public bool BuiltIn { get; set; }

    [Column(Order = 4)]
    public IReadOnlyList<string> Permissions { get; set; } = [];

    [Column(Order = 5)]
    [Default("1")]
    public long Revision { get; set; } = 1;

    [Column(Order = 6)]
    public DateTimeOffset CreatedAt { get; set; }

    [Column(Order = 7)]
    public DateTimeOffset UpdatedAt { get; set; }
}

// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_admin_directory singleton (node-admin-rbac-cli).

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("node_admin_directory",
    Aggregate = "NodeAdminDirectory (Persistence/SurrealDb/Models/NodeAdminDirectory.cs)",
    Guardrail = "G6 SCHEMAFULL; authorization epoch for node-admin roles and statuses")]
[SurrealNote("Singleton (id = local). Every admin status/role-assignment change and every role edit CASes revision in the same transaction (M1), so the last-owner check is atomic with the write. Also keys the in-process role cache.")]
[Slice("identity")]
public partial class NodeAdminDirectory : ISurrealRecord
{
    public const string SchemaNameConst = "node_admin_directory";
    public const string SingletonId = "local";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = SingletonId;

    [Column(Order = 2)]
    [Default("0")]
    public long Revision { get; set; }
}

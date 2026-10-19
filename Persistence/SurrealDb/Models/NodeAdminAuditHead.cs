// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_admin_audit_head singleton (node-admin-rbac-cli).

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("node_admin_audit_head",
    Aggregate = "NodeAdminAuditHead (Persistence/SurrealDb/Models/NodeAdminAuditHead.cs)",
    Guardrail = "G6 SCHEMAFULL; singleton tail of the node-admin audit chain")]
[SurrealNote("Singleton (id = head). Advanced by a seq CAS in the same transaction as each audit append, which serializes writers; a loser re-reads the head and retries.")]
[Slice("identity")]
public partial class NodeAdminAuditHead : ISurrealRecord
{
    public const string SchemaNameConst = "node_admin_audit_head";
    public const string SingletonId = "head";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = SingletonId;

    [Column(Order = 2)]
    public long Seq { get; set; }

    [Column(Order = 3)]
    [Required(NotEmpty = true)]
    public string Hash { get; set; } = string.Empty;
}

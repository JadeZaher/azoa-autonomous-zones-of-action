// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_admin_audit table (node-admin-rbac-cli).

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("node_admin_audit",
    Aggregate = "NodeAdminAudit (Persistence/SurrealDb/Models/NodeAdminAudit.cs)",
    Guardrail = "G6 SCHEMAFULL; append-only hash-chained node-admin audit")]
[SurrealNote("hash = hex(sha256(prev_hash, seq, at, actor, action, target, outcome, detail joined by newlines)); genesis prev_hash is 64 zeros. Appended in one transaction with the node_admin_audit_head CAS; every field is READONLY and no update/delete path exists. Record id = seq zero-padded to 20 digits.")]
[Slice("identity")]
[Index("node_admin_audit_seq", Fields = new[] { "seq" }, Unique = true)]
public partial class NodeAdminAudit : ISurrealRecord
{
    public const string SchemaNameConst = "node_admin_audit";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Column(Order = 2)]
    [ReadOnly]
    public long Seq { get; set; }

    [Column(Order = 3)]
    [Required(NotEmpty = true)]
    [ReadOnly]
    public string At { get; set; } = string.Empty;

    [Column(Order = 4)]
    [Required(NotEmpty = true)]
    [ReadOnly]
    public string Actor { get; set; } = string.Empty;

    [Column(Order = 5)]
    [ReadOnly]
    public IReadOnlyList<string> Amr { get; set; } = [];

    [Column(Order = 6)]
    [Required(NotEmpty = true)]
    [ReadOnly]
    public string Action { get; set; } = string.Empty;

    [Column(Order = 7)]
    [ReadOnly]
    public string Target { get; set; } = string.Empty;

    [Column(Order = 8)]
    [Inside("intent", "ok", "denied", "failed")]
    [ReadOnly]
    public string Outcome { get; set; } = string.Empty;

    [Column(Order = 9)]
    [ReadOnly]
    public string Detail { get; set; } = string.Empty;

    [Column(Order = 10)]
    [ReadOnly]
    public string Ip { get; set; } = string.Empty;

    [Column(Order = 11)]
    [ReadOnly]
    public string RequestId { get; set; } = string.Empty;

    [Column(Order = 12)]
    [Required(NotEmpty = true)]
    [ReadOnly]
    public string PrevHash { get; set; } = string.Empty;

    [Column(Order = 13)]
    [Required(NotEmpty = true)]
    [ReadOnly]
    public string Hash { get; set; } = string.Empty;
}

// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_admin_session table (node-admin-rbac-cli).

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("node_admin_session",
    Aggregate = "NodeAdminSession (Persistence/SurrealDb/Models/NodeAdminSession.cs)",
    Guardrail = "G6 SCHEMAFULL; server-side node-admin session")]
[SurrealNote("Record id = hex of the 128-bit sid (the token carries the base64url form). Every node-admin request requires an unrevoked, unexpired row whose principal equals the token subject (C2).")]
[Slice("identity")]
[Index("node_admin_session_principal", Fields = new[] { "principal" })]
[Index("node_admin_session_expires", Fields = new[] { "expires_at" })]
public partial class NodeAdminSession : ISurrealRecord
{
    public const string SchemaNameConst = "node_admin_session";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Column(Order = 2)]
    [Required(NotEmpty = true)]
    public string Principal { get; set; } = string.Empty;

    [Column(Order = 3)]
    public DateTimeOffset AuthTime { get; set; }

    [Column(Order = 4)]
    public IReadOnlyList<string> Amr { get; set; } = [];

    [Column(Order = 5)]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column(Order = 6)]
    public DateTimeOffset? RevokedAt { get; set; }

    [Column(Order = 7)]
    public string Ip { get; set; } = string.Empty;

    [Column(Order = 8)]
    public string UserAgent { get; set; } = string.Empty;

    [Column(Order = 9)]
    public string CredentialFingerprint { get; set; } = string.Empty;
}

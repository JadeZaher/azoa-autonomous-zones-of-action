// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_admin_challenge table (node-admin-rbac-cli).

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("node_admin_challenge",
    Aggregate = "NodeAdminChallenge (Persistence/SurrealDb/Models/NodeAdminChallenge.cs)",
    Guardrail = "G6 SCHEMAFULL; single-use Ed25519 login challenge")]
[SurrealNote("Single use: consumed by a conditional UPDATE (consumed_at = NONE AND expires_at > now) before the signature is checked. expires_at_text and audience are the exact strings the client signed; the server rebuilds the message from them.")]
[Slice("identity")]
[Index("node_admin_challenge_nonce", Fields = new[] { "nonce" }, Unique = true)]
[Index("node_admin_challenge_expires", Fields = new[] { "expires_at" })]
public partial class NodeAdminChallenge : ISurrealRecord
{
    public const string SchemaNameConst = "node_admin_challenge";
    public string SchemaName => SchemaNameConst;

    public const string PurposeRootLogin = "root_login";
    public const string PurposeAdminLogin = "admin_login";
    public const string PurposeEnroll = "enroll";

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Column(Order = 2)]
    [Inside("root_login", "admin_login", "enroll")]
    public string Purpose { get; set; } = string.Empty;

    [Column(Order = 3)]
    [Required(NotEmpty = true)]
    public string Nonce { get; set; } = string.Empty;

    [Column(Order = 4)]
    [Required(NotEmpty = true)]
    public string Subject { get; set; } = string.Empty;

    [Column(Order = 5)]
    [Required(NotEmpty = true)]
    public string Audience { get; set; } = string.Empty;

    [Column(Order = 6)]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column(Order = 7)]
    [Required(NotEmpty = true)]
    public string ExpiresAtText { get; set; } = string.Empty;

    [Column(Order = 8)]
    public DateTimeOffset? ConsumedAt { get; set; }

    [Column(Order = 9)]
    public DateTimeOffset CreatedAt { get; set; }
}

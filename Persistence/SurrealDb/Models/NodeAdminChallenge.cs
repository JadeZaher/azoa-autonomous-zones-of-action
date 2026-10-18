// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_admin_challenge table (node-admin-rbac-cli).

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("node_admin_challenge",
    Aggregate = "NodeAdminChallenge (Persistence/SurrealDb/Models/NodeAdminChallenge.cs)",
    Guardrail = "G6 SCHEMAFULL; single-use Ed25519 login challenge")]
[SurrealNote("Consumed-challenge markers only. Challenges are stateless (HMAC-authenticated ids, nothing written on issue); after a valid signature the server CREATEs a marker whose record id is the challenge id, so a second use collides and is refused as a replay. Rows are swept after expires_at.")]
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

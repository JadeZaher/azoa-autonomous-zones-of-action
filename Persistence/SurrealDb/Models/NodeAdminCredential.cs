// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_admin_credential table (node-admin-rbac-cli).

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("node_admin_credential",
    Aggregate = "NodeAdminCredential (Persistence/SurrealDb/Models/NodeAdminCredential.cs)",
    Guardrail = "G6 SCHEMAFULL; Ed25519 public key of a node admin")]
[SurrealNote("Record id = fingerprint (sha256 of the raw 32-byte key, first 16 hex), so a key belongs to at most one admin forever; a revoked row may only be reactivated for the same admin. Only the public key is ever stored.")]
[Slice("identity")]
[Index("node_admin_credential_fingerprint", Fields = new[] { "fingerprint" }, Unique = true)]
[Index("node_admin_credential_admin", Fields = new[] { "admin_id" })]
public partial class NodeAdminCredential : ISurrealRecord
{
    public const string SchemaNameConst = "node_admin_credential";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Column(Order = 2)]
    [Required(NotEmpty = true)]
    public string AdminId { get; set; } = string.Empty;

    [Column(Order = 3)]
    [Inside("ed25519")]
    public string Kind { get; set; } = "ed25519";

    [Column(Order = 4)]
    [Required(NotEmpty = true)]
    public string PublicKey { get; set; } = string.Empty;

    [Column(Order = 5)]
    [Required(NotEmpty = true)]
    public string Fingerprint { get; set; } = string.Empty;

    [Column(Order = 6)]
    public string Label { get; set; } = string.Empty;

    [Column(Order = 7)]
    public DateTimeOffset CreatedAt { get; set; }

    [Column(Order = 8)]
    public DateTimeOffset? RevokedAt { get; set; }
}

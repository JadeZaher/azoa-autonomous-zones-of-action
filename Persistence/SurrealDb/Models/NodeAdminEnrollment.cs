// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_admin_enrollment table (node-admin-rbac-cli).

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("node_admin_enrollment",
    Aggregate = "NodeAdminEnrollment (Persistence/SurrealDb/Models/NodeAdminEnrollment.cs)",
    Guardrail = "G6 SCHEMAFULL; one-time node-admin enrollment")]
[SurrealNote("Only SHA-256 of the 32-byte enrollment token is stored. start binds exactly one public key and a pending TOTP secret (started_at CAS); complete consumes the row (consumed_at CAS). Expired rows are swept hourly.")]
[Slice("identity")]
[Index("node_admin_enrollment_token_hash", Fields = new[] { "token_hash" }, Unique = true)]
[Index("node_admin_enrollment_admin", Fields = new[] { "admin_id" })]
[Index("node_admin_enrollment_expires", Fields = new[] { "expires_at" })]
public partial class NodeAdminEnrollment : ISurrealRecord
{
    public const string SchemaNameConst = "node_admin_enrollment";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Column(Order = 2)]
    [Required(NotEmpty = true)]
    public string AdminId { get; set; } = string.Empty;

    [Column(Order = 3)]
    [Required(NotEmpty = true)]
    public string TokenHash { get; set; } = string.Empty;

    [Column(Order = 4)]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column(Order = 5)]
    public DateTimeOffset? StartedAt { get; set; }

    [Column(Order = 6)]
    public string? PendingPublicKey { get; set; }

    [Column(Order = 7)]
    public string? PendingFingerprint { get; set; }

    [Column(Order = 8)]
    public string? PendingLabel { get; set; }

    [Column(Order = 9)]
    public string? TotpSecretPendingProtected { get; set; }

    [Column(Order = 10)]
    [Default("0")]
    public long FailedAttempts { get; set; }

    [Column(Order = 11)]
    public DateTimeOffset? ConsumedAt { get; set; }

    [Column(Order = 12)]
    [Required(NotEmpty = true)]
    public string CreatedBy { get; set; } = string.Empty;

    [Column(Order = 13)]
    public DateTimeOffset CreatedAt { get; set; }
}

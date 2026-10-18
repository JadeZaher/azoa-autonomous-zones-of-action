// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the node_admin table (node-admin-rbac-cli).

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("node_admin",
    Aggregate = "NodeAdminAccount (Persistence/SurrealDb/Models/NodeAdminAccount.cs)",
    Guardrail = "G6 SCHEMAFULL; named node administrator, never a platform user")]
[SurrealNote("A named node administrator. Separate from user: a node admin cannot act as a user and vice versa. admin_revision is the CAS token for every status/role/credential change and is carried in session tokens, so a bump ends every session of the admin.")]
[SurrealNote("totp_secret_protected is a DataProtection ciphertext bound to the admin id; totp_last_step is the RFC 6238 replay guard advanced by CAS. See Services/NodeAdmin/AGENTS.md.")]
[Slice("identity")]
[Index("node_admin_username", Fields = new[] { "username" }, Unique = true)]
public partial class NodeAdminAccount : ISurrealRecord
{
    public const string SchemaNameConst = "node_admin";
    public string SchemaName => SchemaNameConst;

    public const string StatusPendingEnrollment = "PendingEnrollment";
    public const string StatusPendingActivation = "PendingActivation";
    public const string StatusActive = "Active";
    public const string StatusSuspended = "Suspended";
    public const string StatusRevoked = "Revoked";

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Column(Order = 2)]
    [Required(NotEmpty = true)]
    public string Username { get; set; } = string.Empty;

    [Column(Order = 3)]
    [Required(NotEmpty = true)]
    public string DisplayName { get; set; } = string.Empty;

    [Column(Order = 4)]
    [Inside("PendingEnrollment", "PendingActivation", "Active", "Suspended", "Revoked")]
    [Required(NotEmpty = true)]
    public string Status { get; set; } = StatusPendingEnrollment;

    [Column(Order = 5)]
    public IReadOnlyList<string> RoleIds { get; set; } = [];

    [Column(Order = 6)]
    [Default("0")]
    public long AdminRevision { get; set; }

    [Column(Order = 7)]
    [Default("0")]
    public long SessionRevision { get; set; }

    [Column(Order = 8)]
    public string? TotpSecretProtected { get; set; }

    [Column(Order = 9)]
    [Default("0")]
    public long TotpLastStep { get; set; }

    [Column(Order = 10)]
    [Default("0")]
    public long FailedAttempts { get; set; }

    [Column(Order = 11)]
    public DateTimeOffset? LockedUntil { get; set; }

    [Column(Order = 12)]
    [Required(NotEmpty = true)]
    [ReadOnly]
    public string CreatedBy { get; set; } = string.Empty;

    [Column(Order = 13)]
    [ReadOnly]
    public DateTimeOffset CreatedAt { get; set; }

    [Column(Order = 14)]
    public DateTimeOffset UpdatedAt { get; set; }
}

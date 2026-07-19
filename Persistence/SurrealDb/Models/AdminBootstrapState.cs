// SPDX-License-Identifier: UNLICENSED
// Durable one-time binding for the initial node governor.

#nullable enable

using System;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("admin_bootstrap_state",
    Aggregate = "AdminBootstrapState (Persistence/SurrealDb/Models/AdminBootstrapState.cs)",
    Guardrail = "G6 SCHEMAFULL; one-time bootstrap identity binding")]
[SurrealNote("The local row binds a verified bootstrap to one immutable avatar record. It contains no bootstrap secret.")]
[Slice("identity")]
public partial class AdminBootstrapState : ISurrealRecord
{
    public const string SchemaNameConst = "admin_bootstrap_state";
    public const string LocalId = "local";

    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = LocalId;

    [Column(Order = 2)]
    [References(typeof(Avatar))]
    [ReadOnly]
    public string AvatarId { get; set; } = string.Empty;

    [Column(Order = 3)]
    [Default("0")]
    public long CredentialRevision { get; set; }

    [Column(Order = 4)]
    [Default("0")]
    public long SessionRevision { get; set; }

    [Column(Order = 5)]
    public DateTimeOffset? CredentialUpdatedAt { get; set; }

    [Column(Order = 6)]
    [Default("time::now()")]
    [ReadOnly]
    public DateTimeOffset ActivatedAt { get; set; }
}

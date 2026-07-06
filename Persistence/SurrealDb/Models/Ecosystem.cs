// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the ecosystem table.

#nullable enable

using System;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("ecosystem",
        Aggregate = "Ecosystem (star-odk-ecosystem-tree / final-hardening-cutover D2 -- new entity)",
        Guardrail = "G6 SCHEMAFULL; greenfield entity. An Ecosystem is the ROOT of a tree of EcosystemNodes; each node references a DappSeries or a STARODK. Owned by a STARODK + avatar.")]
    [SurrealNote("An Ecosystem is a tree whose nodes (ecosystem_node table) each attach a DappSeries (or another STARODK) as a composable dApp. The tree is walked by ISTARManager.GetEcosystemAsync/codegen to produce the composed multi-dApp GeneratedCode on the owning STARODK. Cycle safety is enforced app-side (mirrors the holon parent-cycle guard) -- SurrealDB does not enforce acyclicity.")]
    [SurrealNote("star_odk_id is the owning STARODK; avatar_id is the authoritative owner (IDOR-scoped, never trusts caller-supplied owner ids). A STARODK owns at most one ecosystem in practice but the schema does not enforce that.")]
    [Slice("dapp_composition")]
    [Index("ecosystem_by_avatar", Fields = new[] { "avatar_id" })]
    [Index("ecosystem_by_star_odk", Fields = new[] { "star_odk_id" })]
    public partial class Ecosystem : ISurrealRecord
    {
        public const string SchemaNameConst = "ecosystem";
        public string SchemaName => SchemaNameConst;

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of Ecosystem.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Caller-supplied ecosystem name")]
        [Required(NotEmpty = true)]
        public string Name { get; set; } = string.Empty;

        [FieldGroup("Optional description")]
        public string? Description { get; set; }

        [FieldGroup("Owning STARODK")]
        [References(typeof(StarOdk))]
        public string StarOdkId { get; set; } = string.Empty;

        [FieldGroup("Owner avatar (Guid('N') hex) -- authoritative, IDOR-scoped")]
        [References(typeof(Avatar))]
        public string AvatarId { get; set; } = string.Empty;

        [FieldGroup("Deployment target chain (e.g. algorand-mainnet) -- nullable until set")]
        public string? TargetChain { get; set; }

        [FieldGroup("Creation timestamp")]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        [FieldGroup("Last-modified timestamp")]
        public DateTimeOffset? ModifiedDate { get; set; }
    }
}

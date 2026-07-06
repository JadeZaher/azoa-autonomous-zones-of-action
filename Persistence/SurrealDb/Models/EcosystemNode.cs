// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the ecosystem_node table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("ecosystem_node",
        Aggregate = "EcosystemNode (star-odk-ecosystem-tree / final-hardening-cutover D2 -- new entity)",
        Guardrail = "G6 SCHEMAFULL; greenfield entity. A single node in an Ecosystem tree. parent_node_id null = a root child of the ecosystem; non-null = nested under another EcosystemNode. ref_kind + ref_id name the attached DappSeries or STARODK.")]
    [SurrealNote("An EcosystemNode attaches ONE composable dApp (a DappSeries via ref_kind='DappSeries', or a STARODK via ref_kind='StarOdk') into the ecosystem tree. Tree edges are modelled by parent_node_id (self-reference); the ecosystem root is the Ecosystem row, so a node whose parent_node_id is null hangs directly off the ecosystem. Codegen walks this tree parent->children (see ISTARManager tree-walking codegen) with an app-side visited-set cycle guard mirroring the holon parent-cycle precedent.")]
    [SurrealNote("ref_id is the Guid('N') hex of the attached DappSeries.Id or STARODK.Id. It is intentionally NOT a typed record<> FK because it is a polymorphic reference (DappSeries OR star_odk) discriminated by ref_kind -- SurrealDB record<> columns are single-table. Ownership is validated app-side, not by schema FK.")]
    [Slice("dapp_composition")]
    [Index("ecosystem_node_by_ecosystem", Fields = new[] { "ecosystem_id" })]
    [Index("ecosystem_node_by_parent", Fields = new[] { "parent_node_id" })]
    public partial class EcosystemNode : ISurrealRecord
    {
        public const string SchemaNameConst = "ecosystem_node";
        public string SchemaName => SchemaNameConst;

        public enum RefKindValue
        {
            DappSeries,
            StarOdk,
        }

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of EcosystemNode.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Owning ecosystem")]
        [References(typeof(Ecosystem))]
        public string EcosystemId { get; set; } = string.Empty;

        [FieldGroup("Parent node in the tree (null = root child of the ecosystem)")]
        [References(typeof(EcosystemNode), Optional = true)]
        public string? ParentNodeId { get; set; }

        [FieldGroup("Discriminator for the attached reference (DappSeries or StarOdk)")]
        [Inside("DappSeries", "StarOdk")]
        [Default("\"DappSeries\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RefKindValue RefKind { get; set; }

        [FieldGroup("Guid('N') hex of the attached DappSeries.Id or STARODK.Id (polymorphic, app-validated)")]
        [Required(NotEmpty = true)]
        public string RefId { get; set; } = string.Empty;

        [FieldGroup("Optional display label for the node in the tree UI")]
        public string? Label { get; set; }

        [FieldGroup("Creation timestamp")]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }
    }
}

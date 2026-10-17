// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the dapp_series_action table.

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("dapp_series_action",
        Aggregate = "DappSeriesAction (dapp-composition track -- new entity, no hand-written model)",
        Guardrail = "G6 SCHEMAFULL; greenfield entity introduced by the dapp-composition track. Carries the ordering and cross-action data flow within a DappSeries.")]
    [SurrealNote("An ordered action entry inside a DappSeries. Composite key concept: (dapp_series_id, action_id) is logically unique but stored as a real row to allow soft-update of order and input_mappings without delete+reinsert.")]
    [SurrealNote("order is 1-indexed and dense: ReorderActionAsync resequences sibling rows transactionally. input_mappings is a JSON array describing how outputs from prior actions' terminal nodes flow into this action's entry nodes (see DappSeries spec §InputMapping for the shape: sourceActionId/sourceNodeId/targetActionId/targetNodeId/fieldMap).")]
    [Slice("dapp_composition")]
    [Index("dapp_series_action_by_series", Fields = new[] { "dapp_series_id" })]
    [Index("dapp_series_action_by_order", Fields = new[] { "dapp_series_id", "order" })]
    [Index("dapp_series_action_by_action", Fields = new[] { "action_id" })]
    public partial class DappSeriesAction : ISurrealRecord
    {
        public const string SchemaNameConst = "dapp_series_action";
        public string SchemaName => SchemaNameConst;

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of DappSeriesAction.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Parent series")]
        [References(typeof(DappSeries))]
        public string DappSeriesId { get; set; } = string.Empty;

        [FieldGroup("Referenced action")]
        [References(typeof(ActionDefinition))]
        public string ActionId { get; set; } = string.Empty;

        [FieldGroup("1-indexed execution order within the series")]
        [Assert("$value > 0")]
        public long Order { get; set; }

        [FieldGroup("JSON array of InputMapping entries (null when no cross-action flow needed)")]
        public string? InputMappings { get; set; }
    }
}

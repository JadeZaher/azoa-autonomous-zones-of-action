// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the action_dependency table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("action_dependency",
        Aggregate = "ActionDependency (Models/ActionDefinition/ActionDependency.cs)",
        Guardrail = "G6 SCHEMAFULL; cross-action dependency edge. ActionDefinition activation refuses to enter Active until all Required dependencies are satisfied.")]
    [SurrealNote("Optional vs Required is a hint to the dependency-check endpoint -- Required dependencies block activation; Optional ones are reported in DependencyCheckResult for caller visibility but do not block.")]
    [SurrealNote("depends_on_node_id is set when the dependency is on a specific node's output (e.g. 'must have HolonCreate's holonId') rather than the entire action's completion. Null = depend on the whole action reaching Succeeded.")]
    [Slice("action")]
    [Index("action_dependency_by_action", Fields = new[] { "action_id" })]
    [Index("action_dependency_by_target", Fields = new[] { "depends_on_action_id" })]
    public partial class ActionDependency : ISurrealRecord
    {
        public const string SchemaNameConst = "action_dependency";
        public string SchemaName => SchemaNameConst;

        public enum ActionDependencyTypeKind
        {
            Required,
            Optional,
        }

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of ActionDependency.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Dependent action (the one that won't run until the dependency is satisfied)")]
        [References(typeof(ActionDefinition))]
        public string ActionId { get; set; } = string.Empty;

        [FieldGroup("Dependency target action")]
        [References(typeof(ActionDefinition))]
        public string DependsOnActionId { get; set; } = string.Empty;

        [FieldGroup("Optional: depend on a specific node output rather than full action completion")]
        [References(typeof(ActionNode), Optional = true)]
        public string? DependsOnNodeId { get; set; }

        [FieldGroup("ActionDependencyType enum name")]
        [Inside("Required", "Optional")]
        [Default("\"Required\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ActionDependencyTypeKind DependencyType { get; set; }
    }
}

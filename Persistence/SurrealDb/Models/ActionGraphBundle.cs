// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the action_graph_bundle table.

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("action_graph_bundle",
        Aggregate = "ActionGraphBundle (Models/ActionGraphBundle.cs)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Generated code + deployment config can be large strings; SurrealDB has no per-field size limit but operators should monitor row size.")]
    [Slice("identity")]
    [Index("action_graph_bundle_user_id", Fields = new[] { "user_id" })]
    [Index("action_graph_bundle_target_chain", Fields = new[] { "target_chain" })]
    [Index("action_graph_bundle_is_active", Fields = new[] { "is_active" })]
    public partial class ActionGraphBundle : ISurrealRecord
    {
        public const string SchemaNameConst = "action_graph_bundle";
        public string SchemaName => SchemaNameConst;

        [Id]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string? PublicKey { get; set; }

        public string? PrivateKeyHash { get; set; }

        [References(typeof(User), Optional = true)]
        public string? UserId { get; set; }

        [FieldGroup("BoundHolonIds (list of holon-id strings)")]
        public IReadOnlyList<string>? BoundHolonIds { get; set; }

        public string? TargetChain { get; set; }

        public string? GeneratedCode { get; set; }

        public string? DeploymentConfig { get; set; }

        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? ModifiedDate { get; set; }

        [Default("true")]
        public bool IsActive { get; set; }

        [Default("false")]
        public bool IsPublic { get; set; }
    }
}

// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_node table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("quest_node",
        Aggregate = "QuestNode (Models/Quest/QuestNode.cs)",
        Guardrail = "G6 SCHEMAFULL; definition-side step inside a quest DAG. Per-(run,node) runtime state lives on quest_node_execution.")]
    [SurrealNote("Each row is one step inside a quest DAG. config is a JSON-serialized request model (e.g. HolonCreateModel, NftMintRequest) that the matching IQuestNodeHandler deserializes at execution time. node_type asserts INSIDE the full QuestNodeType enum so a schema-drift typo is rejected at INSERT time rather than at the dispatch layer.")]
    [SurrealNote("is_entry/is_terminal are derived from the edge graph (an entry node has no incoming control edges) and cached on the row for fast quest-start lookup. execution_order is the topological position computed by QuestDagValidator on activation; the (quest_id, execution_order) composite index is used by the executor to walk nodes in dependency order.")]
    [SurrealNote("state/output/error were intentionally removed when quest-temporal-fork-model split per-run state into quest_node_execution -- see SURREAL-SCHEMA-HINTS.md §2 'Removed from quest_node'.")]
    [Slice("quest")]
    [Index("quest_node_by_quest", Fields = new[] { "quest_id" })]
    [Index("quest_node_by_order", Fields = new[] { "quest_id", "execution_order" })]
    public partial class QuestNode : ISurrealRecord
    {
        public const string SchemaNameConst = "quest_node";
        public string SchemaName => SchemaNameConst;

        public enum QuestNodeTypeKind
        {
            HolonCreate,
            HolonUpdate,
            HolonDelete,
            HolonGet,
            HolonQuery,
            HolonInteract,
            HolonGetChildren,
            HolonGetPeers,
            HolonGetAncestors,
            HolonGetDescendants,
            HolonPropagate,
            HolonCompose,
            HolonClone,
            HolonMoveSubtree,
            NftMint,
            NftTransfer,
            NftBurn,
            NftGet,
            NftQuery,
            NftGetMetadata,
            WalletCreate,
            WalletUpdate,
            WalletDelete,
            WalletGet,
            WalletQuery,
            WalletSetDefault,
            WalletGetPortfolio,
            StarGenerate,
            StarDeploy,
            Search,
            AvatarNFTGetComposite,
            BlockchainExecute,
            Condition,
            ComposeOutputs,
        }

        [Id, Column(Order = 1, Type = "string")]
        [FieldGroup("Core identity (record id is the Guid('N') of QuestNode.Id)")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("Owning quest")]
        [References(typeof(Quest))]
        [JsonPropertyName("quest_id")]
        public string QuestId { get; set; } = string.Empty;

        [Column(Order = 3)]
        [FieldGroup("Reusable node template this node was instantiated from (null for hand-authored)")]
        [References(typeof(QuestNodeTemplate), Optional = true)]
        [JsonPropertyName("node_template_id")]
        public string? NodeTemplateId { get; set; }

        [Column(Order = 4, Type = "string")]
        [FieldGroup("QuestNodeType enum name (e.g. HolonCreate, NftMint)")]
        [Inside("HolonCreate", "HolonUpdate", "HolonDelete", "HolonGet", "HolonQuery",
                "HolonInteract", "HolonGetChildren", "HolonGetPeers", "HolonGetAncestors",
                "HolonGetDescendants", "HolonPropagate", "HolonCompose", "HolonClone",
                "HolonMoveSubtree",
                "NftMint", "NftTransfer", "NftBurn", "NftGet", "NftQuery", "NftGetMetadata",
                "WalletCreate", "WalletUpdate", "WalletDelete", "WalletGet", "WalletQuery",
                "WalletSetDefault", "WalletGetPortfolio",
                "StarGenerate", "StarDeploy",
                "Search", "AvatarNFTGetComposite", "BlockchainExecute",
                "Condition", "ComposeOutputs")]
        [JsonPropertyName("node_type"), JsonConverter(typeof(JsonStringEnumConverter))]
        public QuestNodeTypeKind NodeType { get; set; }

        [Column(Order = 5, Type = "string")]
        [FieldGroup("Caller-supplied label")]
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [Column(Order = 6, Type = "string")]
        [FieldGroup("Node-specific JSON config -- deserialized to the matching request model at execution time")]
        [Default("\"{}\"")]
        [JsonPropertyName("config")]
        public string Config { get; set; } = string.Empty;

        [Column(Order = 7, Type = "bool")]
        [FieldGroup("Entry point (no incoming control edges) -- cached from DAG analysis")]
        [Default("false")]
        [JsonPropertyName("is_entry")]
        public bool IsEntry { get; set; }

        [Column(Order = 8, Type = "bool")]
        [FieldGroup("Terminal node (no outgoing control edges) -- cached from DAG analysis")]
        [Default("false")]
        [JsonPropertyName("is_terminal")]
        public bool IsTerminal { get; set; }

        [Column(Order = 9, Type = "int")]
        [FieldGroup("Topological position (0-based; computed during DAG validation)")]
        [JsonPropertyName("execution_order")]
        public long ExecutionOrder { get; set; }
    }
}

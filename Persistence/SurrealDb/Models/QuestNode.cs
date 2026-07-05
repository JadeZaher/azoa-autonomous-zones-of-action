// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_node table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
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
            // economic-primitive-nodes track: Tier-1 (chain-free) + Tier-2 (RequiresChainCapability).
            GateCheck,
            Emit,
            Swap,
            Grant,
            Transfer,
            Refund,
            // fungible-token-node track.
            FungibleTokenCreate,
        }

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of QuestNode.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Owning quest")]
        [References(typeof(Quest))]
        public string QuestId { get; set; } = string.Empty;

        [FieldGroup("Reusable node template this node was instantiated from (null for hand-authored)")]
        [References(typeof(QuestNodeTemplate), Optional = true)]
        public string? NodeTemplateId { get; set; }

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
                "Condition", "ComposeOutputs",
                "GateCheck", "Emit", "Swap", "Grant", "Transfer", "Refund",
                "FungibleTokenCreate")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public QuestNodeTypeKind NodeType { get; set; }

        [FieldGroup("Caller-supplied label")]
        public string Name { get; set; } = string.Empty;

        [FieldGroup("Node-specific JSON config -- deserialized to the matching request model at execution time")]
        [Default("\"{}\"")]
        public string Config { get; set; } = string.Empty;

        [FieldGroup("Entry point (no incoming control edges) -- cached from DAG analysis")]
        [Default("false")]
        public bool IsEntry { get; set; }

        [FieldGroup("Terminal node (no outgoing control edges) -- cached from DAG analysis")]
        [Default("false")]
        public bool IsTerminal { get; set; }

        [FieldGroup("Topological position (0-based; computed during DAG validation)")]
        public long ExecutionOrder { get; set; }
    }
}

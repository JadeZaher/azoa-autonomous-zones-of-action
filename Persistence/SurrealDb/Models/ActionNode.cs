// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the action_node table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("action_node",
        Aggregate = "ActionNode (Models/Actions/ActionNode.cs)",
        Guardrail = "G6 SCHEMAFULL; definition-side step inside an action DAG. Per-(run,node) runtime state lives on action_node_execution.")]
    [SurrealNote("Each row is one step inside an action DAG. config is a JSON-serialized request model (e.g. HolonCreateModel, NftMintRequest) that the matching IActionNodeHandler deserializes at execution time. node_type asserts INSIDE the full ActionNodeType enum so a schema-drift typo is rejected at INSERT time rather than at the dispatch layer.")]
    [SurrealNote("is_entry/is_terminal are derived from the edge graph (an entry node has no incoming control edges) and cached on the row for fast action-start lookup. execution_order is the topological position computed by ActionDagValidator on activation; the (action_id, execution_order) composite index is used by the executor to walk nodes in dependency order.")]
    [SurrealNote("state/output/error were intentionally removed when action-temporal-fork-model split per-run state into action_node_execution -- see SURREAL-SCHEMA-HINTS.md §2 'Removed from action_node'.")]
    [Slice("action")]
    [Index("action_node_by_action", Fields = new[] { "action_id" })]
    [Index("action_node_by_order", Fields = new[] { "action_id", "execution_order" })]
    public partial class ActionNode : ISurrealRecord
    {
        public const string SchemaNameConst = "action_node";
        public string SchemaName => SchemaNameConst;

        public enum ActionNodeTypeKind
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
            ActionGraphBundleGenerate,
            ActionGraphBundleDeploy,
            Search,
            UserNFTGetComposite,
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
            // final-hardening D1 fractionalization rails.
            Bridge,
            Back,
            // Phase 5 D19 verifiable-credential rail (Tier-2).
            CredentialRevoke,
            // Phase 5 D20 governance rails (Tier-1).
            GovernanceProposalCreate,
            GovernanceVote,
            GovernanceTally,
            // Phase 5 D21 credit rails (Tier-1).
            LoanOpen,
            LoanRepaymentRecord,
            LoanBalance,
        }

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of ActionNode.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Owning action")]
        [References(typeof(ActionDefinition))]
        public string ActionId { get; set; } = string.Empty;

        [FieldGroup("Reusable node template this node was instantiated from (null for hand-authored)")]
        [References(typeof(ActionNodeTemplate), Optional = true)]
        public string? NodeTemplateId { get; set; }

        [FieldGroup("ActionNodeType enum name (e.g. HolonCreate, NftMint)")]
        [Inside("HolonCreate", "HolonUpdate", "HolonDelete", "HolonGet", "HolonQuery",
                "HolonInteract", "HolonGetChildren", "HolonGetPeers", "HolonGetAncestors",
                "HolonGetDescendants", "HolonPropagate", "HolonCompose", "HolonClone",
                "HolonMoveSubtree",
                "NftMint", "NftTransfer", "NftBurn", "NftGet", "NftQuery", "NftGetMetadata",
                "WalletCreate", "WalletUpdate", "WalletDelete", "WalletGet", "WalletQuery",
                "WalletSetDefault", "WalletGetPortfolio",
                "ActionGraphBundleGenerate", "ActionGraphBundleDeploy",
                "Search", "UserNFTGetComposite", "BlockchainExecute",
                "Condition", "ComposeOutputs",
                "GateCheck", "Emit", "Swap", "Grant", "Transfer", "Refund",
                "FungibleTokenCreate", "Bridge", "Back",
                "CredentialRevoke",
                "GovernanceProposalCreate", "GovernanceVote", "GovernanceTally",
                "LoanOpen", "LoanRepaymentRecord", "LoanBalance")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ActionNodeTypeKind NodeType { get; set; }

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

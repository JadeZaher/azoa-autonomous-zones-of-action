// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the action_node_template table.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("action_node_template",
        Aggregate = "ActionNodeTemplate (Models/Actions/ActionNodeTemplate.cs)",
        Guardrail = "G6 SCHEMAFULL; read-only catalog of reusable node definitions")]
    [SurrealNote("Reusable node definition shared across ActionTemplates. An ActionTemplate's embedded node slot (template_node) references an ActionNodeTemplate by id and supplies ParamOverrides; ActionInstantiator merges DefaultConfig + ParamOverrides + caller parameters into the runtime ActionDefinition's ActionNode.Config.")]
    [SurrealNote("node_type is the ActionNodeType enum serialised as its C# name (e.g. \"HolonCreate\", \"WalletGet\"). Asserted INSIDE the full enum value set so a schema-drift typo is rejected at INSERT time rather than at the dispatch layer.")]
    [Slice("action_templates")]
    [Index("action_node_template_by_author", Fields = new[] { "author_user_id" })]
    [Index("action_node_template_by_type", Fields = new[] { "node_type" })]
    public partial class ActionNodeTemplate : ISurrealRecord
    {
        public const string SchemaNameConst = "action_node_template";
        public string SchemaName => SchemaNameConst;

        /// <summary>
        /// ActionNodeType enum -- keep in lockstep with Models/Actions/ActionEnums.cs:ActionNodeType.
        /// </summary>
        public enum NodeTypeKind
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
            // Hold rails (D14 / W3.4) -- mirrors ActionNodeType; order must match.
            Hold,
            Release,
            HoldRefund,
            ReleaseIntoHold,
        }

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of ActionNodeTemplate.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Caller-supplied label")]
        public string Name { get; set; } = string.Empty;

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
                "LoanOpen", "LoanRepaymentRecord", "LoanBalance",
                "Hold", "Release", "HoldRefund", "ReleaseIntoHold")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public NodeTypeKind NodeType { get; set; }

        [FieldGroup("Optional description")]
        public string? Description { get; set; }

        [FieldGroup("JSON default config blob (string-encoded)")]
        public string DefaultConfig { get; set; } = string.Empty;

        [FieldGroup("JSON-Schema for config validation")]
        public string ConfigSchema { get; set; } = string.Empty;

        [FieldGroup("JSON-Schema for upstream-input contract")]
        public string InputSchema { get; set; } = string.Empty;

        [FieldGroup("JSON-Schema for produced-output contract")]
        public string OutputSchema { get; set; } = string.Empty;

        [FieldGroup("Semantic version (free string)")]
        public string Version { get; set; } = string.Empty;

        [FieldGroup("Owner user (Guid('N') hex)")]
        [Required(NotEmpty = true)]
        public string AuthorUserId { get; set; } = string.Empty;

        [FieldGroup("Marketplace visibility flag")]
        [Default("false")]
        public bool IsPublic { get; set; }

        [FieldGroup("Free-form tags")]
        public IReadOnlyList<string> Tags { get; set; } = System.Array.Empty<string>();
    }
}

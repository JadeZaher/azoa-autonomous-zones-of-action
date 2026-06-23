// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the quest_node_template table.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("quest_node_template",
        Aggregate = "QuestNodeTemplate (Models/Quest/QuestNodeTemplate.cs)",
        Guardrail = "G6 SCHEMAFULL; read-only catalog of reusable node definitions")]
    [SurrealNote("Reusable node definition shared across QuestTemplates. A QuestTemplate's embedded node slot (template_node) references a QuestNodeTemplate by id and supplies ParamOverrides; QuestInstantiator merges DefaultConfig + ParamOverrides + caller parameters into the runtime Quest's QuestNode.Config.")]
    [SurrealNote("node_type is the QuestNodeType enum serialised as its C# name (e.g. \"HolonCreate\", \"WalletGet\"). Asserted INSIDE the full enum value set so a schema-drift typo is rejected at INSERT time rather than at the dispatch layer.")]
    [Slice("quest_templates")]
    [Index("quest_node_template_by_author", Fields = new[] { "author_avatar_id" })]
    [Index("quest_node_template_by_type", Fields = new[] { "node_type" })]
    public partial class QuestNodeTemplate : ISurrealRecord
    {
        public const string SchemaNameConst = "quest_node_template";
        public string SchemaName => SchemaNameConst;

        /// <summary>
        /// QuestNodeType enum -- keep in lockstep with Models/Quest/QuestEnums.cs:QuestNodeType.
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
            StarGenerate,
            StarDeploy,
            Search,
            AvatarNFTGetComposite,
            BlockchainExecute,
            Condition,
            ComposeOutputs,
        }

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of QuestNodeTemplate.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Caller-supplied label")]
        public string Name { get; set; } = string.Empty;

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

        [FieldGroup("Owner avatar (Guid('N') hex)")]
        [Required(NotEmpty = true)]
        public string AuthorAvatarId { get; set; } = string.Empty;

        [FieldGroup("Marketplace visibility flag")]
        [Default("false")]
        public bool IsPublic { get; set; }

        [FieldGroup("Free-form tags")]
        public IReadOnlyList<string> Tags { get; set; } = System.Array.Empty<string>();
    }
}

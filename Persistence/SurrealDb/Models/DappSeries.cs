// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the dapp_series table.

#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("dapp_series",
        Aggregate = "DappSeries (dapp-composition track -- new entity, no hand-written model)",
        Guardrail = "G6 SCHEMAFULL; greenfield entity introduced by the dapp-composition track. Owns the composed-quest-series shape.")]
    [SurrealNote("A DappSeries is a linked series of quest DAGs that compose into a deployable dApp contract via STAR generation. Status transitions: Draft -> Building -> Ready -> Deployed -> Archived. The quest entries are stored separately in dapp_series_quest (table 220) keyed by dapp_series_id; they are queried + reordered independently of the parent.")]
    [SurrealNote("shared_config is a string->string map for cross-quest deployment settings (e.g. chain, provider RPC URL). It is overlaid with each DappSeriesQuest's input mappings to produce the per-quest runtime config at execution time.")]
    [SurrealNote("manifest stores the composed DappManifest as JSON-on-row (string-encoded). DappManifest is a transient artifact produced by ComposeAsync -- it is NOT a separate aggregate root because it is always read whole alongside its parent series. star_odk_id is populated by GenerateAsync after the STARODK record is created via ISTARManager.CreateOrUpdateAsync.")]
    [Slice("dapp_composition")]
    [Index("dapp_series_by_avatar", Fields = new[] { "avatar_id" })]
    [Index("dapp_series_by_status", Fields = new[] { "status" })]
    [Index("dapp_series_by_star_odk", Fields = new[] { "star_odk_id" })]
    public partial class DappSeries : ISurrealRecord
    {
        public const string SchemaNameConst = "dapp_series";
        public string SchemaName => SchemaNameConst;

        public enum StatusKind
        {
            Draft,
            Building,
            Ready,
            Deployed,
            Archived,
        }

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of DappSeries.Id)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Caller-supplied dApp name")]
        [Required(NotEmpty = true)]
        public string Name { get; set; } = string.Empty;

        [FieldGroup("Optional description")]
        public string? Description { get; set; }

        [FieldGroup("Owner avatar (Guid('N') hex)")]
        [References(typeof(Avatar))]
        public string AvatarId { get; set; } = string.Empty;

        [FieldGroup("DappSeriesStatus enum name")]
        [Inside("Draft", "Building", "Ready", "Deployed", "Archived")]
        [Default("\"Draft\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public StatusKind Status { get; set; }

        [FieldGroup("Shared deployment config across all quests in the series (string->string map)")]
        public JsonElement SharedConfig { get; set; }

        [FieldGroup("Linked STARODK.Id, populated by GenerateAsync (null until generation)")]
        [References(typeof(StarOdk), Optional = true)]
        public string? StarOdkId { get; set; }

        [FieldGroup("Deployment target chain (e.g. algorand-mainnet) -- nullable until set")]
        public string? TargetChain { get; set; }

        [FieldGroup("Composed DappManifest as JSON-on-row (null until ComposeAsync runs)")]
        public string? Manifest { get; set; }

        [FieldGroup("Creation timestamp")]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        [FieldGroup("Deployment timestamp (null until Deployed)")]
        public DateTimeOffset? DeployedDate { get; set; }
    }
}

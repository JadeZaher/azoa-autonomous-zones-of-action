// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the data_migration ledger table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("data_migration",
        Aggregate = "IBackfill (Services/Backfill/IBackfill.cs)",
        Guardrail = "G6 SCHEMAFULL, unique index on backfill id (applied-once ledger)")]
    [SurrealNote("Application-level DATA backfill ledger -- distinct from surrealforge's schema_migration DDL ledger. Records which IBackfill units have been applied so re-running the runner is a no-op. Insert-wins: the runner records a row only after a backfill's ApplyAsync succeeds; a present row means 'already applied, skip'.")]
    [SurrealNote("Greenfield pre-launch: zero rows to migrate today. This table + the IBackfill runner exist so no data-rewrite PATH is left unbuilt; the first consumer (Phase C/F6 FK string -> record<table> rewrite) is intentionally NOT written here.")]
    [Slice("_skip")]
    [Index("data_migration_backfill_id_unique", Fields = new[] { "backfill_id" }, Unique = true)]
    public partial class DataMigration : ISurrealRecord
    {
        public const string SchemaNameConst = "data_migration";
        public string SchemaName => SchemaNameConst;

        [Id]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Stable backfill identifier (IBackfill.Id); the applied-once dedup key")]
        [Required(NotEmpty = true)]
        public string BackfillId { get; set; } = string.Empty;

        [FieldGroup("Human-readable backfill name at apply time (IBackfill.Name)")]
        [Required(NotEmpty = true)]
        public string Name { get; set; } = string.Empty;

        [FieldGroup("Content checksum of the backfill unit -- lets a changed body be detected (advisory; ledger keys on backfill_id)")]
        public string? Checksum { get; set; }

        [FieldGroup("Rows affected as reported by ApplyAsync (audit / observability)")]
        public long RowsAffected { get; set; }

        [FieldGroup("Timestamp the backfill was recorded applied (immutable after insert)")]
        [ReadOnly]
        public DateTimeOffset AppliedAt { get; set; }
    }
}

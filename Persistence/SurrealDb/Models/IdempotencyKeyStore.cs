// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the idempotency_key_store table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("idempotency_key_store",
        Aggregate = "IdempotencyRecord (Models/Idempotency/IdempotencyRecord.cs)",
        Guardrail = "G6 SCHEMAFULL, G2 unique key constraint (insert-wins claim)")]
    [SurrealNote("Exactly-once execution of irreversible ops. First InProgress insert wins; concurrent inserts fail UNIQUE and read back the existing row.")]
    [SurrealNote("B3 review: idempotency_key_unique UNIQUE on `key` is NOT NULL (ASSERT != NONE), so SurrealDB NULL-collision semantics are not in play -- correct dedup behaviour by construction.")]
    [Slice("bridge")]
    [Index("idempotency_key_unique", Fields = new[] { "key" }, Unique = true)]
    [Index("idempotency_ttl_expires_at", Fields = new[] { "ttl_expires_at" })]
    [Index("idempotency_operation_type", Fields = new[] { "operation_type" })]
    public partial class IdempotencyKeyStore : ISurrealRecord
    {
        public const string SchemaNameConst = "idempotency_key_store";
        public string SchemaName => SchemaNameConst;

        public enum StateKind
        {
            InProgress,
            Completed,
            Failed,
        }

        [Id]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Idempotency key (caller-supplied or content hash); primary dedup key")]
        [Required(NotEmpty = true)]
        public string Key { get; set; } = string.Empty;

        [FieldGroup("Logical operation type (e.g. bridge_redeem, faucet_dispense)")]
        [Required(NotEmpty = true)]
        public string OperationType { get; set; } = string.Empty;

        [FieldGroup("Lifecycle state")]
        [Inside("InProgress", "Completed", "Failed")]
        [Default("\"InProgress\"")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public StateKind State { get; set; }

        [FieldGroup("Serialized result of completed operation (replayed verbatim to duplicates)")]
        public string? ResultPayload { get; set; }

        [FieldGroup("Failure reason when state == Failed")]
        public string? Error { get; set; }

        [FieldGroup("Timestamps")]
        [ReadOnly]
        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        [FieldGroup("TTL expiry timestamp -- sweep job purges old InProgress rows")]
        public DateTimeOffset? TtlExpiresAt { get; set; }
    }
}

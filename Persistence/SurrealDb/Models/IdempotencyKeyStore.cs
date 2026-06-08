// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the idempotency_key_store table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
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

        [Id, Column(Order = 1, Type = "string")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2, Type = "string")]
        [FieldGroup("Idempotency key (caller-supplied or content hash); primary dedup key")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [Column(Order = 3, Type = "string")]
        [FieldGroup("Logical operation type (e.g. bridge_redeem, faucet_dispense)")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("operation_type")]
        public string OperationType { get; set; } = string.Empty;

        [Column(Order = 4, Type = "string")]
        [FieldGroup("Lifecycle state")]
        [Inside("InProgress", "Completed", "Failed")]
        [Default("\"InProgress\"")]
        [JsonPropertyName("state"), JsonConverter(typeof(JsonStringEnumConverter))]
        public StateKind State { get; set; }

        [Column(Order = 5, Type = "option<string>")]
        [FieldGroup("Serialized result of completed operation (replayed verbatim to duplicates)")]
        [JsonPropertyName("result_payload")]
        public string? ResultPayload { get; set; }

        [Column(Order = 6, Type = "option<string>")]
        [FieldGroup("Failure reason when state == Failed")]
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [Column(Order = 7, Type = "datetime")]
        [FieldGroup("Timestamps")]
        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [Column(Order = 8, Type = "datetime")]
        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }

        [Column(Order = 9, Type = "option<datetime>")]
        [FieldGroup("TTL expiry timestamp -- sweep job purges old InProgress rows")]
        [JsonPropertyName("ttl_expires_at")]
        public DateTimeOffset? TtlExpiresAt { get; set; }
    }
}

// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the kyc_screening_poll_state table.

#nullable enable

using System;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("kyc_screening_poll_state",
        Aggregate = "KycIdentity (node-operator-only person<->avatar binding)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Singleton local policy row keyed 'local', mirroring NodeGovernanceParameters. Holds the ONE date high-water mark for the batched monitoring poll: the whole point of the batched design is a single paginated list call per cycle rather than one call per monitored identity, which would collide with the shared per-IP rate ceiling.")]
    [Slice("identity")]
    public partial class KycScreeningPollState : ISurrealRecord
    {
        public const string SchemaNameConst = "kyc_screening_poll_state";
        public const string LocalId = "local";
        public string SchemaName => SchemaNameConst;

        [Id]
        [FieldGroup("Singleton id; always 'local'")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = LocalId;

        /// <summary>ISO-8601 <c>date</c> filter sent on the next list poll.</summary>
        [FieldGroup("Cold-poll high-water mark")]
        public string? HighWaterMarkDate { get; set; }

        public DateTimeOffset? LastPollStartedAt { get; set; }

        public DateTimeOffset? LastPollCompletedAt { get; set; }

        /// <summary>Records whose <c>lastUpdatedDate</c> advanced on the last cycle.</summary>
        [Default("0")]
        public long LastPollChangedCount { get; set; }

        /// <summary>Consecutive failed cycles; drives bounded backoff.</summary>
        [Default("0")]
        public int ConsecutiveFailures { get; set; }

        /// <summary>Set when a rate-limit ban was observed; polling stays parked until then.</summary>
        public DateTimeOffset? BackoffUntil { get; set; }

        /// <summary>Database-defaulted and read-only; poll upserts omit it by design.</summary>
        [FieldGroup("Timestamps")]
        [Default("time::now()")]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? ModifiedDate { get; set; }
    }
}

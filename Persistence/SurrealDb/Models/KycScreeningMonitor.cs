// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the kyc_screening_monitor table.

#nullable enable

using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;
using AZOA.WebAPI.Models.Kyc;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("kyc_screening_monitor",
        Aggregate = "KycIdentity (node-operator-only person<->user binding)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("NODE-OPERATOR-ONLY. Ongoing AML monitoring has NO push channel of any kind — the provider's callback type list contains no screening event — so this table is driven by a cold batched poll with a date high-water mark.")]
    [SurrealNote("The provider keys a monitor on the QUERY SHAPE (fullName + birthdayTimeFrom), not on a resolved person id, so the exact registered query fields are persisted here. Re-registering the same shape returns argumentError 'already-exists', which is benign idempotent success, not a failure.")]
    [SurrealNote("Uniqueness is (tenant_scope, query_full_name, query_birthday_time_from, lineage_key), mirroring kyc_identity's (tenant_scope, person_key). A monitor key WITHOUT a tenant component would let tenant B's registration resolve to tenant A's row — leaking A's subject user and tenant id — and would make the bare existence of another tenant's monitored person observable as a unique-index violation.")]
    [SurrealNote("kyc_screening_monitor_provider is deliberately NOT unique. One vendor company key serves the whole node, so the vendor dedups a query shape globally and ONE upstream monitor legitimately backs one local row per tenant. A unique index there would refuse the second tenant's registration, which is the same cross-tenant refusal D23 rejects for person_key.")]
    [SurrealNote("B3 nullable-collision review: query_full_name and query_birthday_time_from are option<string> so erasure can tombstone them (D25), and both sit in the UNIQUE index. Verified empirically on SurrealDB v3.1.4: rows whose indexed tuple differs only by a NONE component do not collide, so erased monitors never contend with each other or with live ones. A live registration always writes a non-NONE name and a non-NONE (possibly empty) birthday, so dedup is correct by construction; NONE means erased, \"\" means registered without a birthday.")]
    [Slice("identity")]
    [Index("kyc_screening_monitor_query",
        Fields = new[] { "tenant_scope", "query_full_name", "query_birthday_time_from", "lineage_key" },
        Unique = true)]
    [Index("kyc_screening_monitor_provider", Fields = new[] { "provider_monitor_id" })]
    [Index("kyc_screening_monitor_state", Fields = new[] { "state" })]
    [Index("kyc_screening_monitor_retention", Fields = new[] { "retention_expires_at" })]
    public partial class KycScreeningMonitor : ISurrealRecord
    {
        public const string SchemaNameConst = "kyc_screening_monitor";
        public string SchemaName => SchemaNameConst;

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of the registration)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>Provider-side monitoring id returned by the register call.</summary>
        [FieldGroup("Provider correlation")]
        [Required(NotEmpty = true)]
        public string ProviderMonitorId { get; set; } = string.Empty;

        /// <summary>Uniqueness domain: a tenant Guid('N'), or the node-global sentinel.</summary>
        [FieldGroup("Subject + tenancy (tenant_scope is the uniqueness domain)")]
        [Required(NotEmpty = true)]
        public string TenantScope { get; set; } = string.Empty;

        [References(typeof(User), Optional = true)]
        public string? SubjectUserId { get; set; }

        [References(typeof(User), Optional = true)]
        public string? TenantId { get; set; }

        /// <summary>Exact hosted attempt, session and verified-subject correlation.</summary>
        public string? LineageKey { get; set; }

        /// <summary>Exact <c>query.fullName</c> registered; the monitor key, not a display name.</summary>
        [FieldGroup("Exact registered query shape (the provider's monitor key)")]
        public string? QueryFullName { get; set; }

        /// <summary>Exact <c>query.birthdayTimeFrom</c> registered; empty when omitted, NONE when erased.</summary>
        public string? QueryBirthdayTimeFrom { get; set; } = string.Empty;

        /// <summary>
        /// Threshold used on the FOLLOW-UP search when this monitor fires. The register
        /// call accepts no threshold, so it is an AZOA-side policy value.
        /// </summary>
        [Default("0")]
        public int FollowUpScoreThreshold { get; set; }

        [FieldGroup("Lifecycle")]
        [Inside("ACTIVE", "REMOVED")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public KycMonitorState State { get; set; }

        /// <summary>True when registration returned <c>already-exists</c>; benign idempotency.</summary>
        public bool RegisteredIdempotently { get; set; }

        /// <summary>Last <c>lastUpdatedDate</c> observed for this monitor, ISO-8601.</summary>
        [FieldGroup("Cold-poll high-water mark")]
        public string? LastObservedUpdateDate { get; set; }

        public DateTimeOffset? LastPolledAt { get; set; }

        public DateTimeOffset? LastFollowUpAt { get; set; }

        /// <summary>When the retained query shape becomes erasable (D25).</summary>
        [FieldGroup("Retention + erasure (the query shape is name + date of birth)")]
        public DateTimeOffset? RetentionExpiresAt { get; set; }

        /// <summary>Set when the query shape was tombstoned; the provider id survives.</summary>
        public DateTimeOffset? ErasedAt { get; set; }

        /// <summary>Non-personal operator note explaining the erasure trigger.</summary>
        public string? ErasureReason { get; set; }

        [FieldGroup("Timestamps")]
        [ReadOnly]
        public DateTimeOffset RegisteredAt { get; set; }

        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? ModifiedDate { get; set; }

        public DateTimeOffset? RemovedAt { get; set; }
    }
}

// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the kyc_screening_result table.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Schema;
using AZOA.WebAPI.Models.Kyc;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("kyc_screening_result",
        Aggregate = "KycIdentity (node-operator-only person<->user binding)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("NODE-OPERATOR-ONLY, like kyc_identity. Absent from every generic consumer read surface.")]
    [SurrealNote("score_threshold is persisted because the provider applies it SERVER-SIDE at query time and never echoes it per hit. Without it a stored result set is unexplainable later: you cannot tell whether an empty result means 'clean' or 'threshold was too high'.")]
    [SurrealNote("topics is an open array<string>, never a closed enum. The vendor does not publish an exhaustive topic list, so a strict enum would reject legitimate future values.")]
    [SurrealNote("This row is what a value gate reads: only the LATEST result for (subject_user_id, tenant_id, approved lineage_key) with decision NO_HIT opens a value operation. A missing row, a store error, MANUAL_REVIEW, and HIT all deny — an approved identity with no AML answer must not reach value.")]
    [SurrealNote("query_full_name and query_birthday_time_from are retained PII on the same D25 footing as kyc_identity's four personal fields, so retention_expires_at / erased_at exist here too. Erasing a binding without erasing these would leave a name and a date of birth behind forever.")]
    [Slice("identity")]
    [Index("kyc_screening_result_subject", Fields = new[] { "subject_user_id" })]
    [Index("kyc_screening_result_decision", Fields = new[] { "decision" })]
    [Index("kyc_screening_result_retention", Fields = new[] { "retention_expires_at" })]
    public partial class KycScreeningResult : ISurrealRecord
    {
        public const string SchemaNameConst = "kyc_screening_result";
        public string SchemaName => SchemaNameConst;

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of the screening run)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Subject + tenancy")]
        [References(typeof(User), Optional = true)]
        public string? SubjectUserId { get; set; }

        [References(typeof(User), Optional = true)]
        public string? TenantId { get; set; }

        /// <summary>Exact hosted attempt, session and verified-subject correlation.</summary>
        public string? LineageKey { get; set; }

        /// <summary>Exact <c>query.fullName</c> submitted; NONE once erased (D25).</summary>
        [FieldGroup("Exact query shape submitted to the provider (retained PII)")]
        public string? QueryFullName { get; set; }

        /// <summary>Exact <c>query.birthdayTimeFrom</c> submitted, ISO-8601, when supplied.</summary>
        public string? QueryBirthdayTimeFrom { get; set; }

        /// <summary>The threshold this result set was produced under; not echoed per hit.</summary>
        [Default("0")]
        public int ScoreThreshold { get; set; }

        [FieldGroup("AZOA-derived verdict (the provider publishes no status enum)")]
        [Inside("NO_HIT", "MANUAL_REVIEW", "HIT")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public KycScreeningDecision Decision { get; set; }

        /// <summary>Highest score across the returned hits; 0 when there were none.</summary>
        [Default("0")]
        public int TopScore { get; set; }

        /// <summary>Number of hits returned at or above the threshold.</summary>
        [Default("0")]
        public int HitCount { get; set; }

        /// <summary>Union of every topic across the hits; open string set.</summary>
        public IReadOnlyList<string>? Topics { get; set; }

        /// <summary>Provider person ids for the hits, for a later detail fetch.</summary>
        public IReadOnlyList<string>? PersonIds { get; set; }

        /// <summary>Monitoring registration that produced this result, when poll-sourced.</summary>
        public string? MonitorId { get; set; }

        /// <summary>Version of the risk policy that produced <c>decision</c>.</summary>
        public string PolicyVersion { get; set; } = string.Empty;

        /// <summary>
        /// True when the provider's result set saturated the paged fetch, so the hit
        /// list may be incomplete. Such a result may never be read as a pass.
        /// </summary>
        public bool ResultsTruncated { get; set; }

        /// <summary>When the retained query shape becomes erasable (D25).</summary>
        [FieldGroup("Retention + erasure (the query shape is name + date of birth)")]
        public DateTimeOffset? RetentionExpiresAt { get; set; }

        /// <summary>Set when the query shape was tombstoned; the verdict survives.</summary>
        public DateTimeOffset? ErasedAt { get; set; }

        /// <summary>Non-personal operator note explaining the erasure trigger.</summary>
        public string? ErasureReason { get; set; }

        [FieldGroup("Timestamps")]
        [ReadOnly]
        public DateTimeOffset ScreenedAt { get; set; }

        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? ModifiedDate { get; set; }
    }
}

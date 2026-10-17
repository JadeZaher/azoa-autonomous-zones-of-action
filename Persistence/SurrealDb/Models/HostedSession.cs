// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the hosted_session table.

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("hosted_session",
        Aggregate = "HostedSession (provider-agnostic hosted redirect flow)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Durable admission record for the begin -> hosted redirect -> untrusted callback -> authoritative re-fetch -> resume shape. Deliberately NOT KYC-specific: flow_kind discriminates KYC from a future fiat-in checkout. See Services/HostedSessions/AGENTS.md.")]
    [SurrealNote("state_version is a CAS token, not display metadata. A redelivered callback loses the conditional UPDATE instead of producing a second terminal write; the provider documents no dedup or retry semantics, so idempotency is entirely ours.")]
    [SurrealNote("provider_session_id and provider_correlation_id are secret-adjacent server-side correlation. Never project them into a tenant-facing response.")]
    [SurrealNote("B3 nullable-collision review: hosted_session_provider_session UNIQUE spans provider_session_id, which is option<string> and is NONE on every freshly claimed row. VERIFIED EMPIRICALLY against SurrealDB v3.1.4 (the pinned dev/CI image): two rows whose indexed tuple differs only by a NONE component both CREATE successfully, so many un-attached claims coexist and one subject holding an open claim cannot block another subject's ClaimAdmissionAsync. Explicit NULL is not reachable here either -- SCHEMAFULL option<string> refuses it outright ('Expected `none | string` but found `NULL`'). The index is therefore retained: it is a real dedup guarantee that two AZOA sessions never bind one vendor session, and the node-wide-brick scenario it was suspected of causing does not exist.")]
    [Slice("identity")]
    [Index("hosted_session_admission",
        Fields = new[] { "flow_kind", "provider_key", "admission_key" },
        Unique = true)]
    [Index("hosted_session_provider_session",
        Fields = new[] { "flow_kind", "provider_key", "provider_session_id" },
        Unique = true)]
    [Index("hosted_session_subject", Fields = new[] { "subject_user_id" })]
    [Index("hosted_session_poll", Fields = new[] { "state", "next_poll_at" })]
    public partial class HostedSession : ISurrealRecord
    {
        public const string SchemaNameConst = "hosted_session";
        public string SchemaName => SchemaNameConst;

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of the session)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        /// <summary>Flow family, e.g. <c>kyc</c>; a future fiat checkout is another value.</summary>
        [FieldGroup("Flow discriminator + provider")]
        [Required(NotEmpty = true)]
        public string FlowKind { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string ProviderKey { get; set; } = string.Empty;

        /// <summary>
        /// Caller-stable admission key claimed BEFORE the external create call, so a
        /// crash between claim and create cannot produce two provider sessions.
        /// </summary>
        [FieldGroup("Durable admission claimed before any external side effect")]
        [Required(NotEmpty = true)]
        public string AdmissionKey { get; set; } = string.Empty;

        public string? CreationToken { get; set; }
        public DateTimeOffset? CreationClaimedAt { get; set; }
        public DateTimeOffset? CreationStartedAt { get; set; }
        public DateTimeOffset? CreationQuarantinedAt { get; set; }
        public DateTimeOffset? CreationCompletedAt { get; set; }

        [FieldGroup("Subject + tenancy")]
        [References(typeof(User), Optional = true)]
        public string? SubjectUserId { get; set; }

        [References(typeof(User), Optional = true)]
        public string? TenantId { get; set; }

        /// <summary>Provider-side session id; server-side only, never tenant-projected.</summary>
        [FieldGroup("Server-side provider correlation (never leaves the node)")]
        public string? ProviderSessionId { get; set; }

        /// <summary>Secondary provider correlation, e.g. Identomat's additionalSessionId.</summary>
        public string? ProviderCorrelationId { get; set; }

        /// <summary>Hosted redirect handed to the subject; safe to return.</summary>
        public string? RedirectUrl { get; set; }

        /// <summary>RESUMING is the non-terminal CAS claim held while one delivery resumes.</summary>
        [FieldGroup("Lifecycle")]
        [Inside("CLAIMED", "AWAITING_SUBJECT", "AWAITING_OUTCOME", "RESUMING", "COMPLETED", "FAILED", "ABANDONED")]
        public string State { get; set; } = "CLAIMED";

        /// <summary>CAS token; a terminal transition requires the exact live value.</summary>
        [Default("0")]
        public long StateVersion { get; set; }

        /// <summary>Opaque owner of the current domain-resumption lease.</summary>
        public string? ResumeLeaseToken { get; set; }

        /// <summary>Expiry after which a crashed resumption may be reclaimed.</summary>
        public DateTimeOffset? ResumeLeaseExpiresAt { get; set; }

        /// <summary>Operator identity for the one-time admission reconciliation.</summary>
        public string? ReconciledByUserId { get; set; }

        public DateTimeOffset? ReconciledAt { get; set; }

        public string? ReconciliationReason { get; set; }

        public string? ReconciliationAuditJson { get; set; }

        /// <summary>Authoritative provider verdict, re-fetched server-side; never from a callback body.</summary>
        public string? AuthoritativeOutcome { get; set; }

        public string? ProviderDecisionId { get; set; }
        public string? ProviderEvidenceDigest { get; set; }
        public bool? ProviderEvidenceConflict { get; set; }

        /// <summary>Non-secret reason a session is non-terminal or failed.</summary>
        public string? OutcomeDetail { get; set; }

        /// <summary>Count of accepted callbacks; redeliveries increment without re-deciding.</summary>
        [Default("0")]
        public long CallbackCount { get; set; }

        /// <summary>Domain record resumed on completion, e.g. a kyc_submission id.</summary>
        public string? ResumeTargetId { get; set; }

        /// <summary>When the outcome poll may next re-fetch this attached session; NONE means due now.</summary>
        [FieldGroup("Outcome poll (lost-callback recovery)")]
        public DateTimeOffset? NextPollAt { get; set; }

        /// <summary>Outcome polls claimed so far; drives the backoff.</summary>
        public long? PollAttemptCount { get; set; }

        [FieldGroup("Timestamps")]
        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? ModifiedDate { get; set; }

        public DateTimeOffset? ExpiresAt { get; set; }

        public DateTimeOffset? LastCallbackAt { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }
    }
}

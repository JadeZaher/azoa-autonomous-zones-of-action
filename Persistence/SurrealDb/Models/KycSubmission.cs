// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the kyc_submission table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Schema;
using AZOA.WebAPI.Models.Kyc;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("kyc_submission",
        Aggregate = "KycSubmission (generic identity-verification module)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Provider-agnostic KYC ledger keyed to Avatar. The gate (IKycGateService) reads the most-recent APPROVED submission for an avatar rather than a denormalized level on the avatar table, so the shared avatar schema is untouched.")]
    [SurrealNote("status / provider persist as string enum names (JsonStringEnumConverter) to keep rows human-legible. The active-submission guard queries status IN (PENDING, IN_REVIEW).")]
    [Slice("identity")]
    [Index("kyc_submission_avatar_id", Fields = new[] { "avatar_id" })]
    [Index("kyc_submission_status", Fields = new[] { "status" })]
    public partial class KycSubmission : ISurrealRecord
    {
        public const string SchemaNameConst = "kyc_submission";
        public string SchemaName => SchemaNameConst;

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of the submission)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Owner avatar (Guid('N') hex record link); indexed for owner lookups")]
        [References(typeof(Avatar), Optional = true)]
        public string? AvatarId { get; set; }

        [FieldGroup("Verification provider that owns this submission")]
        [Inside("MANUAL", "VERIFF")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public KycProvider Provider { get; set; }

        [FieldGroup("Lifecycle status")]
        [Inside("PENDING", "IN_REVIEW", "APPROVED", "REJECTED", "EXPIRED")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public KycStatus Status { get; set; }

        [FieldGroup("Review metadata (set by the admin review path)")]
        public string? ReviewerId { get; set; }

        public string? ReviewNotes { get; set; }

        public string? RejectionReason { get; set; }

        [FieldGroup("Provider session linkage (manual provider stamps the avatar id as a pseudo-session)")]
        public string? ProviderSessionId { get; set; }

        public string? ProviderResult { get; set; }

        [FieldGroup("Timestamps")]
        [ReadOnly]
        public DateTimeOffset SubmittedAt { get; set; }

        public DateTimeOffset? ReviewedAt { get; set; }

        public DateTimeOffset? ExpiresAt { get; set; }

        public DateTimeOffset CreatedDate { get; set; }

        public DateTimeOffset? ModifiedDate { get; set; }
    }
}

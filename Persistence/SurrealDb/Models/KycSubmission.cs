// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the kyc_submission table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;
using OASIS.WebAPI.Models.Kyc;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
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

        [Id, Column(Order = 1, Type = "string")]
        [FieldGroup("Core identity (record id is the Guid('N') of the submission)")]
        [Assert("$value != NONE AND $value != \"\"")]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("Owner avatar (Guid('N') hex record link); indexed for owner lookups")]
        [References(typeof(Avatar), Optional = true)]
        [JsonPropertyName("avatar_id")]
        public string? AvatarId { get; set; }

        [Column(Order = 3, Type = "string")]
        [FieldGroup("Verification provider that owns this submission")]
        [Inside("MANUAL", "VERIFF")]
        [JsonPropertyName("provider"), JsonConverter(typeof(JsonStringEnumConverter))]
        public KycProvider Provider { get; set; }

        [Column(Order = 4, Type = "string")]
        [FieldGroup("Lifecycle status")]
        [Inside("PENDING", "IN_REVIEW", "APPROVED", "REJECTED", "EXPIRED")]
        [JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter))]
        public KycStatus Status { get; set; }

        [Column(Order = 5, Type = "option<string>")]
        [FieldGroup("Review metadata (set by the admin review path)")]
        [JsonPropertyName("reviewer_id")]
        public string? ReviewerId { get; set; }

        [Column(Order = 6, Type = "option<string>")]
        [JsonPropertyName("review_notes")]
        public string? ReviewNotes { get; set; }

        [Column(Order = 7, Type = "option<string>")]
        [JsonPropertyName("rejection_reason")]
        public string? RejectionReason { get; set; }

        [Column(Order = 8, Type = "option<string>")]
        [FieldGroup("Provider session linkage (manual provider stamps the avatar id as a pseudo-session)")]
        [JsonPropertyName("provider_session_id")]
        public string? ProviderSessionId { get; set; }

        [Column(Order = 9, Type = "option<string>")]
        [JsonPropertyName("provider_result")]
        public string? ProviderResult { get; set; }

        [Column(Order = 10, Type = "datetime")]
        [FieldGroup("Timestamps")]
        [ReadOnly]
        [JsonPropertyName("submitted_at")]
        public DateTimeOffset SubmittedAt { get; set; }

        [Column(Order = 11, Type = "option<datetime>")]
        [JsonPropertyName("reviewed_at")]
        public DateTimeOffset? ReviewedAt { get; set; }

        [Column(Order = 12, Type = "option<datetime>")]
        [JsonPropertyName("expires_at")]
        public DateTimeOffset? ExpiresAt { get; set; }

        [Column(Order = 13, Type = "datetime")]
        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }

        [Column(Order = 14, Type = "option<datetime>")]
        [JsonPropertyName("modified_date")]
        public DateTimeOffset? ModifiedDate { get; set; }
    }
}

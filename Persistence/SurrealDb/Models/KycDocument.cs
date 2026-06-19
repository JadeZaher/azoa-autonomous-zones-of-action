// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the kyc_document table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Schema;
using OASIS.WebAPI.Models.Kyc;

namespace OASIS.WebAPI.Persistence.SurrealDb.Models
{
    [SurrealTable("kyc_document",
        Aggregate = "KycDocument (generic identity-verification module)",
        Guardrail = "G6 SCHEMAFULL")]
    [SurrealNote("Document record attached to a kyc_submission. file_url references external blob storage (out of scope for this module); only the metadata + URL are persisted here. type persists as the string enum name.")]
    [Slice("identity")]
    [Index("kyc_document_submission_id", Fields = new[] { "submission_id" })]
    public partial class KycDocument : ISurrealRecord
    {
        public const string SchemaNameConst = "kyc_document";
        public string SchemaName => SchemaNameConst;

        [Id, Column(Order = 1, Type = "string")]
        [FieldGroup("Core identity (record id is the Guid('N') of the document)")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [Column(Order = 2)]
        [FieldGroup("Parent submission (Guid('N') hex record link); indexed for per-submission listing")]
        [References(typeof(KycSubmission), Optional = true)]
        [JsonPropertyName("submission_id")]
        public string? SubmissionId { get; set; }

        [Column(Order = 3, Type = "string")]
        [FieldGroup("Document classification")]
        [Inside("GOVERNMENT_ID", "PASSPORT", "DRIVERS_LICENSE", "SELFIE", "PROOF_OF_ADDRESS")]
        [JsonPropertyName("type"), JsonConverter(typeof(JsonStringEnumConverter))]
        public KycDocumentType Type { get; set; }

        [Column(Order = 4, Type = "string")]
        [FieldGroup("Blob reference + display metadata")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("file_url")]
        public string FileUrl { get; set; } = string.Empty;

        [Column(Order = 5, Type = "string")]
        [Required(NotEmpty = true)]
        [JsonPropertyName("file_name")]
        public string FileName { get; set; } = string.Empty;

        [Column(Order = 6, Type = "option<string>")]
        [JsonPropertyName("mime_type")]
        public string? MimeType { get; set; }

        [Column(Order = 7, Type = "option<int>")]
        [JsonPropertyName("file_size_bytes")]
        public long? FileSizeBytes { get; set; }

        [Column(Order = 8, Type = "option<string>")]
        [JsonPropertyName("metadata")]
        public string? Metadata { get; set; }

        [Column(Order = 9, Type = "datetime")]
        [ReadOnly]
        [JsonPropertyName("created_date")]
        public DateTimeOffset CreatedDate { get; set; }
    }
}

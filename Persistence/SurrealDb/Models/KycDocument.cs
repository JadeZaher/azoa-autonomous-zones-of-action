// SPDX-License-Identifier: UNLICENSED
// Hand-authored SurrealDB POCO for the kyc_document table.

#nullable enable

using System;
using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Schema;
using AZOA.WebAPI.Models.Kyc;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models
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

        [Id]
        [FieldGroup("Core identity (record id is the Guid('N') of the document)")]
        [Required(NotEmpty = true)]
        public string Id { get; set; } = string.Empty;

        [FieldGroup("Parent submission (Guid('N') hex record link); indexed for per-submission listing")]
        [References(typeof(KycSubmission), Optional = true)]
        public string? SubmissionId { get; set; }

        [FieldGroup("Document classification")]
        [Inside("GOVERNMENT_ID", "PASSPORT", "DRIVERS_LICENSE", "SELFIE", "PROOF_OF_ADDRESS")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public KycDocumentType Type { get; set; }

        [FieldGroup("Blob reference + display metadata")]
        [Required(NotEmpty = true)]
        public string FileUrl { get; set; } = string.Empty;

        [Required(NotEmpty = true)]
        public string FileName { get; set; } = string.Empty;

        public string? MimeType { get; set; }

        public long? FileSizeBytes { get; set; }

        public string? Metadata { get; set; }

        [ReadOnly]
        public DateTimeOffset CreatedDate { get; set; }
    }
}

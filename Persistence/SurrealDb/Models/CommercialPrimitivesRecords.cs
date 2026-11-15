// SPDX-License-Identifier: UNLICENSED

#nullable enable

using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

[SurrealTable("commercial_invoice",
    Aggregate = "CommercialPrimitivesService",
    Guardrail = "G6 SCHEMAFULL; owner-scoped commercial invoice record")]
[Slice("identity")]
[Index("commercial_invoice_owner", Fields = new[] { "owner_actor_id", "created_at" })]
[Index("commercial_invoice_number", Fields = new[] { "invoice_number" }, Unique = true)]
public sealed partial class CommercialInvoiceRecord : ISurrealRecord
{
    public const string SchemaNameConst = "commercial_invoice";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Column(Order = 2)]
    [Required(NotEmpty = true)]
    public string OwnerActorId { get; set; } = string.Empty;

    [Column(Order = 3)]
    [Required(NotEmpty = true)]
    public string InvoiceNumber { get; set; } = string.Empty;

    [Column(Order = 4)]
    [Required(NotEmpty = true)]
    public string Title { get; set; } = string.Empty;

    [Column(Order = 5)]
    [Required(NotEmpty = true)]
    public string IssuerSubject { get; set; } = string.Empty;

    [Column(Order = 6)]
    [Required(NotEmpty = true)]
    public string PayerSubject { get; set; } = string.Empty;

    [Column(Order = 7)]
    [Required(NotEmpty = true)]
    public string CurrencyCode { get; set; } = "USD";

    [Column(Order = 8)]
    [Default("0")]
    public long SubtotalMinorUnits { get; set; }

    [Column(Order = 9)]
    [Default("0")]
    public long FeeMinorUnits { get; set; }

    [Column(Order = 10)]
    [Default("0")]
    public long TotalMinorUnits { get; set; }

    [Column(Order = 11)]
    public string? TokenEquivalentUnits { get; set; }

    [Column(Order = 12)]
    [Default("\"[]\"")]
    public string LineItemsJson { get; set; } = "[]";

    [Column(Order = 13)]
    [Inside("draft", "open", "paid", "void", "expired")]
    [Default("\"open\"")]
    public string Status { get; set; } = "open";

    [Column(Order = 14)]
    public DateTimeOffset? DueAt { get; set; }

    [Column(Order = 15)]
    public string? PaymentLinkUrl { get; set; }

    [Column(Order = 16)]
    public DateTimeOffset CreatedAt { get; set; }

    [Column(Order = 17)]
    public DateTimeOffset? PaidAt { get; set; }

    [Column(Order = 18)]
    public DateTimeOffset? VoidedAt { get; set; }
}

[SurrealTable("commercial_payment_request",
    Aggregate = "CommercialPrimitivesService",
    Guardrail = "G6 SCHEMAFULL; owner-scoped commercial payment request record")]
[Slice("identity")]
[Index("commercial_payment_request_owner", Fields = new[] { "owner_actor_id", "created_at" })]
[Index("commercial_payment_request_number", Fields = new[] { "request_number" }, Unique = true)]
public sealed partial class PaymentRequestRecordModel : ISurrealRecord
{
    public const string SchemaNameConst = "commercial_payment_request";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Column(Order = 2)]
    [Required(NotEmpty = true)]
    public string OwnerActorId { get; set; } = string.Empty;

    [Column(Order = 3)]
    [Required(NotEmpty = true)]
    public string RequestNumber { get; set; } = string.Empty;

    [Column(Order = 4)]
    [Required(NotEmpty = true)]
    public string Title { get; set; } = string.Empty;

    [Column(Order = 5)]
    [Required(NotEmpty = true)]
    public string RequesterSubject { get; set; } = string.Empty;

    [Column(Order = 6)]
    [Required(NotEmpty = true)]
    public string PayerSubject { get; set; } = string.Empty;

    [Column(Order = 7)]
    [Required(NotEmpty = true)]
    public string CurrencyCode { get; set; } = "USD";

    [Column(Order = 8)]
    [Default("0")]
    public long AmountMinorUnits { get; set; }

    [Column(Order = 9)]
    public string? TokenEquivalentUnits { get; set; }

    [Column(Order = 10)]
    public string? Memo { get; set; }

    [Column(Order = 11)]
    [Inside("draft", "open", "paid", "void", "expired")]
    [Default("\"open\"")]
    public string Status { get; set; } = "open";

    [Column(Order = 12)]
    public DateTimeOffset? DueAt { get; set; }

    [Column(Order = 13)]
    public string? PaymentLinkUrl { get; set; }

    [Column(Order = 14)]
    public DateTimeOffset CreatedAt { get; set; }

    [Column(Order = 15)]
    public DateTimeOffset? PaidAt { get; set; }
}

[SurrealTable("ownership_receipt",
    Aggregate = "CommercialPrimitivesService",
    Guardrail = "G6 SCHEMAFULL; owner-scoped ownership receipt document")]
[Slice("identity")]
[Index("ownership_receipt_owner", Fields = new[] { "owner_actor_id", "issued_at" })]
[Index("ownership_receipt_number", Fields = new[] { "receipt_number" }, Unique = true)]
public sealed partial class OwnershipReceiptRecord : ISurrealRecord
{
    public const string SchemaNameConst = "ownership_receipt";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Column(Order = 2)]
    [Required(NotEmpty = true)]
    public string OwnerActorId { get; set; } = string.Empty;

    [Column(Order = 3)]
    [Required(NotEmpty = true)]
    public string ReceiptNumber { get; set; } = string.Empty;

    [Column(Order = 4)]
    [Required(NotEmpty = true)]
    public string AllocationOrSaleId { get; set; } = string.Empty;

    [Column(Order = 5)]
    [Required(NotEmpty = true)]
    public string HolderSubject { get; set; } = string.Empty;

    [Column(Order = 6)]
    [Inside("CONTRIBUTOR", "INVESTOR", "FOUNDER", "MEMBER")]
    [Default("\"MEMBER\"")]
    public string HolderClass { get; set; } = "MEMBER";

    [Column(Order = 7)]
    [Required(NotEmpty = true)]
    public string AssetSymbol { get; set; } = string.Empty;

    [Column(Order = 8)]
    public string? AssetResourceId { get; set; }

    [Column(Order = 9)]
    [Required(NotEmpty = true)]
    public string UnitsFormatted { get; set; } = string.Empty;

    [Column(Order = 10)]
    [Default("0")]
    public long FiatMinorUnits { get; set; }

    [Column(Order = 11)]
    [Required(NotEmpty = true)]
    public string CurrencyCode { get; set; } = "USD";

    [Column(Order = 12)]
    public string? ChainTxHash { get; set; }

    [Column(Order = 13)]
    [Required(NotEmpty = true)]
    public string ContentHash { get; set; } = string.Empty;

    [Column(Order = 14)]
    public DateTimeOffset IssuedAt { get; set; }

    [Column(Order = 15)]
    [Inside("verified", "recorded_pending_chain", "sandbox_simulated")]
    [Default("\"recorded_pending_chain\"")]
    public string VerificationStatus { get; set; } = "recorded_pending_chain";
}

[SurrealTable("account_document",
    Aggregate = "CommercialPrimitivesService",
    Guardrail = "G6 SCHEMAFULL; owner-scoped account document report")]
[Slice("identity")]
[Index("account_document_owner", Fields = new[] { "owner_actor_id", "generated_at" })]
public sealed partial class AccountDocumentRecord : ISurrealRecord
{
    public const string SchemaNameConst = "account_document";
    public string SchemaName => SchemaNameConst;

    [Id, Column(Order = 1)]
    [Required(NotEmpty = true)]
    public string Id { get; set; } = string.Empty;

    [Column(Order = 2)]
    [Required(NotEmpty = true)]
    public string OwnerActorId { get; set; } = string.Empty;

    [Column(Order = 3)]
    [Required(NotEmpty = true)]
    public string Subject { get; set; } = string.Empty;

    [Column(Order = 4)]
    [Inside("monthly_statement", "payout_ledger_report", "ownership_certificate", "kyc_consent_attestation", "tax_activity_summary")]
    [Default("\"monthly_statement\"")]
    public string DocumentType { get; set; } = "monthly_statement";

    [Column(Order = 5)]
    [Required(NotEmpty = true)]
    public string Title { get; set; } = string.Empty;

    [Column(Order = 6)]
    public DateTimeOffset? PeriodStart { get; set; }

    [Column(Order = 7)]
    public DateTimeOffset? PeriodEnd { get; set; }

    [Column(Order = 8)]
    [Default("\"{}\"")]
    public string SummaryMetricsJson { get; set; } = "{}";

    [Column(Order = 9)]
    [Required(NotEmpty = true)]
    public string ContentHash { get; set; } = string.Empty;

    [Column(Order = 10)]
    public string? DownloadUrl { get; set; }

    [Column(Order = 11)]
    public DateTimeOffset GeneratedAt { get; set; }
}

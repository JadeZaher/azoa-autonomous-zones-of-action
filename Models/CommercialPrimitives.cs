// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models;

/// <summary>Supported ride-along access link scopes defined by the commercial primitives contract.</summary>
public static class AccessLinkScopes
{
    public const string Kyc = "kyc";
    public const string Consent = "consent";
    public const string AddFunds = "add_funds";
    public const string CashOut = "cash_out";
    public const string InvoicePay = "invoice_pay";
    public const string ReceiptView = "receipt_view";
    public const string StatementView = "statement_view";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Kyc,
        Consent,
        AddFunds,
        CashOut,
        InvoicePay,
        ReceiptView,
        StatementView,
    };
}

/// <summary>Lifecycle states for commercial invoices and payment requests.</summary>
public static class CommercialInvoiceStatuses
{
    public const string Draft = "draft";
    public const string Open = "open";
    public const string Paid = "paid";
    public const string Void = "void";
    public const string Expired = "expired";
}

/// <summary>Deferral states for signed access links.</summary>
public static class AccessLinkDeferralStatuses
{
    public const string Active = "active";
    public const string DeferredCredentials = "deferred_credentials";
    public const string SandboxPreview = "sandbox_preview";
}

/// <summary>Provider deferral entry statuses.</summary>
public static class ProviderDeferralStatuses
{
    public const string Deferred = "deferred";
    public const string Resolved = "resolved";
}

/// <summary>Identomat session status values.</summary>
public static class IdentomatSessionStatuses
{
    public const string Pending = "Pending";
    public const string PendingVendorCredentials = "PendingVendorCredentials";
    public const string UnderReview = "UnderReview";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

/// <summary>Ownership receipt holder classes.</summary>
public static class OwnershipHolderClasses
{
    public const string Contributor = "CONTRIBUTOR";
    public const string Investor = "INVESTOR";
    public const string Founder = "FOUNDER";
    public const string Member = "MEMBER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Contributor,
        Investor,
        Founder,
        Member,
    };
}

/// <summary>Ownership receipt verification states.</summary>
public static class OwnershipVerificationStatuses
{
    public const string Verified = "verified";
    public const string RecordedPendingChain = "recorded_pending_chain";
    public const string SandboxSimulated = "sandbox_simulated";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Verified,
        RecordedPendingChain,
        SandboxSimulated,
    };
}

/// <summary>Account document report types.</summary>
public static class AccountDocumentTypes
{
    public const string MonthlyStatement = "monthly_statement";
    public const string PayoutLedgerReport = "payout_ledger_report";
    public const string OwnershipCertificate = "ownership_certificate";
    public const string KycConsentAttestation = "kyc_consent_attestation";
    public const string TaxActivitySummary = "tax_activity_summary";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        MonthlyStatement,
        PayoutLedgerReport,
        OwnershipCertificate,
        KycConsentAttestation,
        TaxActivitySummary,
    };
}

public sealed record MintAccessLinkRequest(
    string ExternalSubject,
    string Scope,
    string? TargetResourceId = null,
    string? ReturnUrl = null,
    int? TtlMinutes = null);

public sealed record VerifyAccessLinkRequest(
    string? Sig = null,
    string? Signature = null,
    string? Exp = null,
    string? Scope = null,
    string? Sub = null,
    string? Res = null,
    string? ReturnUrl = null);

public sealed record SignedAccessLink(
    string LinkId,
    string ExternalSubject,
    string Scope,
    string? TargetResourceId,
    string? ReturnUrl,
    string SignedUrl,
    string Signature,
    string RideAlongToken,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    string Provider,
    string DeferralStatus,
    string? DeferredReason);

public sealed record AccessLinkVerificationResult(
    bool Valid,
    string LinkId,
    string Scope,
    string ExternalSubject,
    DateTimeOffset ExpiresAt,
    string Provider,
    string DeferralStatus,
    string? DeferredReason,
    string? TargetResourceId = null,
    string? ReturnUrl = null,
    string? FailureReason = null);

public sealed record IdentomatSessionRequest(
    string? ExternalSubject = null,
    string? ReturnUrl = null,
    int? TtlMinutes = null);

public sealed record IdentomatSessionStatus(
    string Provider,
    string Status,
    string? SessionId,
    string? VerificationUrl,
    string? SignedAccessUrl,
    bool DeferralLogged,
    string? DeferredReason,
    DateTimeOffset? DeferralLoggedAt,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> MissingConfigurationKeys);

public sealed record InvoiceLineItemRequest(
    string Description,
    long Quantity,
    long UnitPriceMinorUnits);

public sealed record InvoiceLineItem(
    string Description,
    long Quantity,
    long UnitPriceMinorUnits,
    long AmountMinorUnits);

public sealed record CreateCommercialInvoiceRequest(
    string? Title,
    string PayerSubject,
    string CurrencyCode,
    long FeeMinorUnits,
    IReadOnlyList<InvoiceLineItemRequest> LineItems,
    string? TokenEquivalentUnits = null,
    DateTimeOffset? DueAt = null,
    bool IssueAsOpen = true);

public sealed record PayCommercialInvoiceRequest(string? PaymentReference = null);

public sealed record VoidCommercialInvoiceRequest(string? Reason = null);

public sealed record PayPaymentRequestInput(string? PaymentReference = null);

public sealed record CommercialInvoice(
    string Id,
    string InvoiceNumber,
    string Title,
    string IssuerSubject,
    string PayerSubject,
    string CurrencyCode,
    long SubtotalMinorUnits,
    long FeeMinorUnits,
    long TotalMinorUnits,
    string? TokenEquivalentUnits,
    IReadOnlyList<InvoiceLineItem> LineItems,
    string Status,
    DateTimeOffset? DueAt,
    string? PaymentLinkUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? VoidedAt);

public sealed record CreatePaymentRequestInput(
    string? Title,
    string PayerSubject,
    string CurrencyCode,
    long AmountMinorUnits,
    string? TokenEquivalentUnits = null,
    string? Memo = null,
    DateTimeOffset? DueAt = null);

public sealed record PaymentRequestRecord(
    string Id,
    string RequestNumber,
    string Title,
    string RequesterSubject,
    string PayerSubject,
    string CurrencyCode,
    long AmountMinorUnits,
    string? TokenEquivalentUnits,
    string? Memo,
    string Status,
    DateTimeOffset? DueAt,
    string? PaymentLinkUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt);

public sealed record IssueOwnershipReceiptRequest(
    string AllocationOrSaleId,
    string HolderSubject,
    string HolderClass,
    string AssetSymbol,
    string UnitsFormatted,
    long FiatMinorUnits,
    string CurrencyCode = "USD",
    string? AssetResourceId = null,
    string? ChainTxHash = null,
    string? VerificationStatus = null);

public sealed record OwnershipReceiptDocument(
    string ReceiptId,
    string ReceiptNumber,
    string AllocationOrSaleId,
    string HolderSubject,
    string HolderClass,
    string AssetSymbol,
    string? AssetResourceId,
    string UnitsFormatted,
    long FiatMinorUnits,
    string CurrencyCode,
    string? ChainTxHash,
    string ContentHash,
    DateTimeOffset IssuedAt,
    string VerificationStatus);

public sealed record GenerateAccountDocumentRequest(
    string DocumentType,
    string? Subject = null,
    string? Title = null,
    DateTimeOffset? PeriodStart = null,
    DateTimeOffset? PeriodEnd = null);

public sealed record AccountDocumentReport(
    string DocumentId,
    string Subject,
    string DocumentType,
    string Title,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd,
    IReadOnlyDictionary<string, object?> SummaryMetrics,
    string ContentHash,
    string? DownloadUrl,
    DateTimeOffset GeneratedAt);

public sealed record ProviderDeferralEntry(
    string Id,
    string Provider,
    string Capability,
    string ReasonCode,
    string Message,
    IReadOnlyList<string> MissingConfigurationKeys,
    DateTimeOffset LoggedAt,
    string Status);

// SPDX-License-Identifier: UNLICENSED

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Services.Compliance;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Options;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Commercial;

/// <summary>
/// Thread-safe singleton backing store for commercial primitives when operating in memory
/// or alongside SurrealDB write-through/read-through persistence.
/// </summary>
public sealed class CommercialPrimitivesStoreState
{
    internal sealed class InvoiceSlot
    {
        public InvoiceSlot(string ownerActorId, CommercialInvoice invoice)
        {
            OwnerActorId = ownerActorId;
            Invoice = invoice;
        }

        public string OwnerActorId { get; }
        public CommercialInvoice Invoice { get; set; }
        public object SyncRoot { get; } = new();
    }

    internal sealed record PaymentRequestSlot(string OwnerActorId, PaymentRequestRecord Request);
    internal sealed record OwnershipReceiptSlot(string OwnerActorId, OwnershipReceiptDocument Receipt);
    internal sealed record AccountDocumentSlot(string OwnerActorId, AccountDocumentReport Document);

    internal ConcurrentDictionary<string, InvoiceSlot> Invoices { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<string, PaymentRequestSlot> PaymentRequests { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<string, OwnershipReceiptSlot> OwnershipReceipts { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<string, AccountDocumentSlot> AccountDocuments { get; } = new(StringComparer.Ordinal);

    internal long InvoiceSequence;
    internal long PaymentRequestSequence;
    internal long OwnershipReceiptSequence;
}

/// <summary>
/// Implements AZOA commercial primitives: signed access link minting/verification,
/// Identomat capability status and deferral accounting, invoices, payment requests,
/// SHA-256 anchored ownership receipts, and account document reports.
/// </summary>
public sealed class CommercialPrimitivesService
{
    private const int DefaultQueryLimit = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CommercialPrimitivesStoreState _state;
    private readonly AccessLinkMintingService _accessLinks;
    private readonly IProviderDeferralLedger _deferrals;
    private readonly IOptions<KycSettings> _kycOptions;
    private readonly ILogger<CommercialPrimitivesService> _logger;
    private readonly TimeProvider _clock;
    private readonly ISurrealExecutor? _surrealExecutor;

    public CommercialPrimitivesService(
        CommercialPrimitivesStoreState state,
        AccessLinkMintingService accessLinks,
        IProviderDeferralLedger deferrals,
        IOptions<KycSettings> kycOptions,
        ILogger<CommercialPrimitivesService> logger,
        TimeProvider? clock = null,
        ISurrealExecutor? surrealExecutor = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _accessLinks = accessLinks ?? throw new ArgumentNullException(nameof(accessLinks));
        _deferrals = deferrals ?? throw new ArgumentNullException(nameof(deferrals));
        _kycOptions = kycOptions ?? throw new ArgumentNullException(nameof(kycOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
        _surrealExecutor = surrealExecutor;
    }

    public AZOAResult<SignedAccessLink> MintAccessLink(Guid actorId, MintAccessLinkRequest request)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<SignedAccessLink>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        return _accessLinks.Mint(request);
    }

    public AZOAResult<AccessLinkVerificationResult> VerifyAccessLink(
        string linkId,
        string scope,
        string externalSubject,
        long exp,
        string sig)
        => _accessLinks.VerifyAccessLink(linkId, scope, externalSubject, exp, sig);

    public AZOAResult<IdentomatSessionStatus> GetIdentomatStatus(Guid actorId)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<IdentomatSessionStatus>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        var evaluated = _deferrals.EnsureIdentomatCredentialDeferralEvaluated();
        if (_deferrals.HasLiveIdentomatCredentials)
        {
            return AZOAResult<IdentomatSessionStatus>.Success(new IdentomatSessionStatus(
                ProviderDeferralLedger.IdentomatProviderName,
                IdentomatSessionStatuses.Pending,
                SessionId: null,
                VerificationUrl: null,
                SignedAccessUrl: null,
                DeferralLogged: false,
                DeferredReason: null,
                DeferralLoggedAt: null,
                ExpiresAt: null,
                MissingConfigurationKeys: Array.Empty<string>()));
        }

        var missing = _deferrals.GetMissingIdentomatKeys();
        var latestDeferral = evaluated
            ?? _deferrals.ListDeferrals(ProviderDeferralLedger.IdentomatProviderName)
                .FirstOrDefault(e => string.Equals(
                    e.ReasonCode,
                    ProviderDeferralLedger.IdentomatCredentialsPendingReasonCode,
                    StringComparison.Ordinal));

        return AZOAResult<IdentomatSessionStatus>.Success(new IdentomatSessionStatus(
            ProviderDeferralLedger.IdentomatProviderName,
            IdentomatSessionStatuses.PendingVendorCredentials,
            SessionId: null,
            VerificationUrl: null,
            SignedAccessUrl: null,
            DeferralLogged: latestDeferral is not null || missing.Count > 0,
            DeferredReason: missing.Count > 0
                ? ProviderDeferralLedger.IdentomatCredentialsPendingReasonCode
                : latestDeferral?.ReasonCode,
            DeferralLoggedAt: latestDeferral?.LoggedAt,
            ExpiresAt: null,
            MissingConfigurationKeys: missing));
    }

    public AZOAResult<IdentomatSessionStatus> StartIdentomatSession(Guid actorId, IdentomatSessionRequest? request)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<IdentomatSessionStatus>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        var externalSubject = string.IsNullOrWhiteSpace(request?.ExternalSubject)
            ? actorId.ToString("N")
            : request.ExternalSubject.Trim();

        if (externalSubject.Length > 128 || externalSubject.Any(char.IsControl))
            return AZOAResult<IdentomatSessionStatus>.FailureWithCode(
                "Invalid externalSubject.", AzoaErrorCodes.InvalidRequest);

        var sessionId = $"idem_{Guid.NewGuid():N}";
        var linkResult = _accessLinks.Mint(new MintAccessLinkRequest(
            externalSubject,
            AccessLinkScopes.Kyc,
            TargetResourceId: sessionId,
            ReturnUrl: request?.ReturnUrl,
            TtlMinutes: request?.TtlMinutes));

        if (linkResult.IsError || linkResult.Result is null)
            return AZOAResult<IdentomatSessionStatus>.FailureWithCode(
                linkResult.Message,
                linkResult.Code ?? AzoaErrorCodes.InvalidRequest);

        var link = linkResult.Result;
        if (_deferrals.HasLiveIdentomatCredentials)
        {
            _ = _kycOptions.Value;
            return AZOAResult<IdentomatSessionStatus>.Success(new IdentomatSessionStatus(
                ProviderDeferralLedger.IdentomatProviderName,
                IdentomatSessionStatuses.Pending,
                sessionId,
                VerificationUrl: link.SignedUrl,
                SignedAccessUrl: link.SignedUrl,
                DeferralLogged: false,
                DeferredReason: null,
                DeferralLoggedAt: null,
                ExpiresAt: link.ExpiresAt,
                MissingConfigurationKeys: Array.Empty<string>()));
        }

        var missing = _deferrals.GetMissingIdentomatKeys();
        var deferral = _deferrals.EnsureIdentomatCredentialDeferralEvaluated()
            ?? _deferrals.RecordDeferral(
                ProviderDeferralLedger.IdentomatProviderName,
                ProviderDeferralLedger.IdentomatCapabilityName,
                ProviderDeferralLedger.IdentomatCredentialsPendingReasonCode,
                "Identomat KYC vendor credentials are pending configuration; returning signed ride-along preview URL.",
                missing);

        return AZOAResult<IdentomatSessionStatus>.Success(new IdentomatSessionStatus(
            ProviderDeferralLedger.IdentomatProviderName,
            IdentomatSessionStatuses.PendingVendorCredentials,
            sessionId,
            VerificationUrl: link.SignedUrl,
            SignedAccessUrl: link.SignedUrl,
            DeferralLogged: true,
            DeferredReason: deferral.ReasonCode,
            DeferralLoggedAt: deferral.LoggedAt,
            ExpiresAt: link.ExpiresAt,
            MissingConfigurationKeys: deferral.MissingConfigurationKeys));
    }

    public async Task<AZOAResult<IReadOnlyList<CommercialInvoice>>> ListInvoicesAsync(
        Guid actorId,
        CancellationToken ct = default)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<IReadOnlyList<CommercialInvoice>>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        var ownerActorId = actorId.ToString("N");
        await HydrateInvoicesFromSurrealAsync(ownerActorId, ct);

        var items = _state.Invoices.Values
            .Where(slot => string.Equals(slot.OwnerActorId, ownerActorId, StringComparison.Ordinal))
            .Select(slot =>
            {
                lock (slot.SyncRoot)
                {
                    return slot.Invoice;
                }
            })
            .OrderByDescending(inv => inv.CreatedAt)
            .ToArray();

        return AZOAResult<IReadOnlyList<CommercialInvoice>>.Success(items);
    }

    public async Task<AZOAResult<CommercialInvoice>> CreateInvoiceAsync(
        Guid actorId,
        CreateCommercialInvoiceRequest request,
        CancellationToken ct = default)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        if (request is null)
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "Invoice request is required.", AzoaErrorCodes.InvalidRequest);

        if (!TryNormalizeSubject(request.PayerSubject, out var payerSubject))
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "A valid payerSubject is required.", AzoaErrorCodes.InvalidRequest);

        if (!TryNormalizeCurrency(request.CurrencyCode, out var currencyCode))
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "A 3-letter ISO currency code is required.", AzoaErrorCodes.InvalidRequest);

        if (request.FeeMinorUnits < 0)
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "feeMinorUnits cannot be negative.", AzoaErrorCodes.InvalidRequest);

        if (request.LineItems is null || request.LineItems.Count == 0 || request.LineItems.Count > 100)
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "At least one line item (max 100) is required.", AzoaErrorCodes.InvalidRequest);

        var normalizedLines = new List<InvoiceLineItem>(request.LineItems.Count);
        long subtotal = 0;

        foreach (var item in request.LineItems)
        {
            if (item is null
                || string.IsNullOrWhiteSpace(item.Description)
                || item.Description.Trim().Length > 256
                || item.Description.Any(char.IsControl))
            {
                return AZOAResult<CommercialInvoice>.FailureWithCode(
                    "Each line item requires a valid description.", AzoaErrorCodes.InvalidRequest);
            }

            if (item.Quantity <= 0 || item.UnitPriceMinorUnits < 0)
                return AZOAResult<CommercialInvoice>.FailureWithCode(
                    "Line item quantity must be positive and unitPriceMinorUnits must be non-negative.",
                    AzoaErrorCodes.InvalidRequest);

            long lineAmount;
            try
            {
                checked
                {
                    lineAmount = item.Quantity * item.UnitPriceMinorUnits;
                    subtotal += lineAmount;
                }
            }
            catch (OverflowException)
            {
                return AZOAResult<CommercialInvoice>.FailureWithCode(
                    "Invoice amount overflow.", AzoaErrorCodes.InvalidRequest);
            }

            normalizedLines.Add(new InvoiceLineItem(
                item.Description.Trim(),
                item.Quantity,
                item.UnitPriceMinorUnits,
                lineAmount));
        }

        long total;
        try
        {
            checked
            {
                total = subtotal + request.FeeMinorUnits;
            }
        }
        catch (OverflowException)
        {
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "Invoice total overflow.", AzoaErrorCodes.InvalidRequest);
        }

        var ownerActorId = actorId.ToString("N");
        var createdAt = _clock.GetUtcNow();
        var id = $"inv_{Guid.NewGuid():N}";
        var seq = Interlocked.Increment(ref _state.InvoiceSequence);
        var invoiceNumber = $"INV-{createdAt:yyyyMMdd}-{seq:D4}";
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? $"Invoice {invoiceNumber}"
            : request.Title.Trim();

        if (title.Length > 160 || title.Any(char.IsControl))
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "Invalid invoice title.", AzoaErrorCodes.InvalidRequest);

        var linkResult = _accessLinks.Mint(new MintAccessLinkRequest(
            payerSubject,
            AccessLinkScopes.InvoicePay,
            TargetResourceId: id));

        if (linkResult.IsError || linkResult.Result is null)
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                linkResult.Message,
                linkResult.Code ?? AzoaErrorCodes.InvalidRequest);

        var status = request.IssueAsOpen
            ? CommercialInvoiceStatuses.Open
            : CommercialInvoiceStatuses.Draft;

        var invoice = new CommercialInvoice(
            id,
            invoiceNumber,
            title,
            ownerActorId,
            payerSubject,
            currencyCode,
            subtotal,
            request.FeeMinorUnits,
            total,
            NormalizeOptionalString(request.TokenEquivalentUnits),
            normalizedLines,
            status,
            request.DueAt,
            linkResult.Result.SignedUrl,
            createdAt,
            PaidAt: null,
            VoidedAt: null);

        _state.Invoices[id] = new CommercialPrimitivesStoreState.InvoiceSlot(ownerActorId, invoice);

        var record = new CommercialInvoiceRecord
        {
            Id = invoice.Id,
            OwnerActorId = ownerActorId,
            InvoiceNumber = invoice.InvoiceNumber,
            Title = invoice.Title,
            IssuerSubject = invoice.IssuerSubject,
            PayerSubject = invoice.PayerSubject,
            CurrencyCode = invoice.CurrencyCode,
            SubtotalMinorUnits = invoice.SubtotalMinorUnits,
            FeeMinorUnits = invoice.FeeMinorUnits,
            TotalMinorUnits = invoice.TotalMinorUnits,
            TokenEquivalentUnits = invoice.TokenEquivalentUnits,
            LineItemsJson = JsonSerializer.Serialize(invoice.LineItems, JsonOptions),
            Status = invoice.Status,
            DueAt = invoice.DueAt,
            PaymentLinkUrl = invoice.PaymentLinkUrl,
            CreatedAt = invoice.CreatedAt,
            PaidAt = invoice.PaidAt,
            VoidedAt = invoice.VoidedAt,
        };

        await TryExecuteSurrealCreateAsync(record, ct);
        return AZOAResult<CommercialInvoice>.Success(invoice);
    }

    public async Task<AZOAResult<CommercialInvoice>> PayInvoiceAsync(
        Guid actorId,
        string invoiceId,
        PayCommercialInvoiceRequest? request,
        CancellationToken ct = default)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        if (string.IsNullOrWhiteSpace(invoiceId))
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "invoiceId is required.", AzoaErrorCodes.InvalidRequest);

        _ = request;
        var normalizedId = invoiceId.Trim();
        var ownerActorId = actorId.ToString("N");
        var slot = await FindOwnedInvoiceAsync(ownerActorId, normalizedId, ct);
        if (slot is null)
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "Invoice not found.", AzoaErrorCodes.NotFound);

        CommercialInvoice updated;
        lock (slot.SyncRoot)
        {
            if (!string.Equals(slot.Invoice.Status, CommercialInvoiceStatuses.Open, StringComparison.Ordinal))
            {
                return AZOAResult<CommercialInvoice>.FailureWithCode(
                    $"Invoice cannot be marked paid from status '{slot.Invoice.Status}'.",
                    AzoaErrorCodes.Conflict);
            }

            updated = slot.Invoice with
            {
                Status = CommercialInvoiceStatuses.Paid,
                PaidAt = _clock.GetUtcNow(),
            };
            slot.Invoice = updated;
        }

        await TryUpdateInvoicePaidAsync(ownerActorId, updated.Id, updated.PaidAt!.Value, ct);
        return AZOAResult<CommercialInvoice>.Success(updated);
    }

    public async Task<AZOAResult<CommercialInvoice>> VoidInvoiceAsync(
        Guid actorId,
        string invoiceId,
        VoidCommercialInvoiceRequest? request,
        CancellationToken ct = default)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        if (string.IsNullOrWhiteSpace(invoiceId))
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "invoiceId is required.", AzoaErrorCodes.InvalidRequest);

        _ = request;
        var normalizedId = invoiceId.Trim();
        var ownerActorId = actorId.ToString("N");
        var slot = await FindOwnedInvoiceAsync(ownerActorId, normalizedId, ct);
        if (slot is null)
            return AZOAResult<CommercialInvoice>.FailureWithCode(
                "Invoice not found.", AzoaErrorCodes.NotFound);

        CommercialInvoice updated;
        lock (slot.SyncRoot)
        {
            if (string.Equals(slot.Invoice.Status, CommercialInvoiceStatuses.Paid, StringComparison.Ordinal)
                || string.Equals(slot.Invoice.Status, CommercialInvoiceStatuses.Void, StringComparison.Ordinal))
            {
                return AZOAResult<CommercialInvoice>.FailureWithCode(
                    $"Invoice cannot be voided from status '{slot.Invoice.Status}'.",
                    AzoaErrorCodes.Conflict);
            }

            updated = slot.Invoice with
            {
                Status = CommercialInvoiceStatuses.Void,
                VoidedAt = _clock.GetUtcNow(),
            };
            slot.Invoice = updated;
        }

        await TryUpdateInvoiceVoidedAsync(ownerActorId, updated.Id, updated.VoidedAt!.Value, ct);
        return AZOAResult<CommercialInvoice>.Success(updated);
    }

    public async Task<AZOAResult<IReadOnlyList<PaymentRequestRecord>>> ListPaymentRequestsAsync(
        Guid actorId,
        CancellationToken ct = default)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<IReadOnlyList<PaymentRequestRecord>>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        var ownerActorId = actorId.ToString("N");
        await HydratePaymentRequestsFromSurrealAsync(ownerActorId, ct);

        var items = _state.PaymentRequests.Values
            .Where(slot => string.Equals(slot.OwnerActorId, ownerActorId, StringComparison.Ordinal))
            .Select(slot => slot.Request)
            .OrderByDescending(r => r.CreatedAt)
            .ToArray();

        return AZOAResult<IReadOnlyList<PaymentRequestRecord>>.Success(items);
    }

    public async Task<AZOAResult<PaymentRequestRecord>> CreatePaymentRequestAsync(
        Guid actorId,
        CreatePaymentRequestInput request,
        CancellationToken ct = default)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<PaymentRequestRecord>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        if (request is null)
            return AZOAResult<PaymentRequestRecord>.FailureWithCode(
                "Payment request is required.", AzoaErrorCodes.InvalidRequest);

        if (!TryNormalizeSubject(request.PayerSubject, out var payerSubject))
            return AZOAResult<PaymentRequestRecord>.FailureWithCode(
                "A valid payerSubject is required.", AzoaErrorCodes.InvalidRequest);

        if (!TryNormalizeCurrency(request.CurrencyCode, out var currencyCode))
            return AZOAResult<PaymentRequestRecord>.FailureWithCode(
                "A 3-letter ISO currency code is required.", AzoaErrorCodes.InvalidRequest);

        if (request.AmountMinorUnits <= 0)
            return AZOAResult<PaymentRequestRecord>.FailureWithCode(
                "amountMinorUnits must be positive.", AzoaErrorCodes.InvalidRequest);

        var ownerActorId = actorId.ToString("N");
        var createdAt = _clock.GetUtcNow();
        var id = $"pr_{Guid.NewGuid():N}";
        var seq = Interlocked.Increment(ref _state.PaymentRequestSequence);
        var requestNumber = $"PR-{createdAt:yyyyMMdd}-{seq:D4}";
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? $"Payment Request {requestNumber}"
            : request.Title.Trim();

        if (title.Length > 160 || title.Any(char.IsControl))
            return AZOAResult<PaymentRequestRecord>.FailureWithCode(
                "Invalid payment request title.", AzoaErrorCodes.InvalidRequest);

        var linkResult = _accessLinks.Mint(new MintAccessLinkRequest(
            payerSubject,
            AccessLinkScopes.InvoicePay,
            TargetResourceId: id));

        if (linkResult.IsError || linkResult.Result is null)
            return AZOAResult<PaymentRequestRecord>.FailureWithCode(
                linkResult.Message,
                linkResult.Code ?? AzoaErrorCodes.InvalidRequest);

        var item = new PaymentRequestRecord(
            id,
            requestNumber,
            title,
            ownerActorId,
            payerSubject,
            currencyCode,
            request.AmountMinorUnits,
            NormalizeOptionalString(request.TokenEquivalentUnits),
            NormalizeOptionalString(request.Memo),
            CommercialInvoiceStatuses.Open,
            request.DueAt,
            linkResult.Result.SignedUrl,
            createdAt,
            PaidAt: null);

        _state.PaymentRequests[id] = new CommercialPrimitivesStoreState.PaymentRequestSlot(ownerActorId, item);

        var record = new PaymentRequestRecordModel
        {
            Id = item.Id,
            OwnerActorId = ownerActorId,
            RequestNumber = item.RequestNumber,
            Title = item.Title,
            RequesterSubject = item.RequesterSubject,
            PayerSubject = item.PayerSubject,
            CurrencyCode = item.CurrencyCode,
            AmountMinorUnits = item.AmountMinorUnits,
            TokenEquivalentUnits = item.TokenEquivalentUnits,
            Memo = item.Memo,
            Status = item.Status,
            DueAt = item.DueAt,
            PaymentLinkUrl = item.PaymentLinkUrl,
            CreatedAt = item.CreatedAt,
            PaidAt = item.PaidAt,
        };

        await TryExecuteSurrealCreateAsync(record, ct);
        return AZOAResult<PaymentRequestRecord>.Success(item);
    }

    public async Task<AZOAResult<IReadOnlyList<OwnershipReceiptDocument>>> ListOwnershipReceiptsAsync(
        Guid actorId,
        CancellationToken ct = default)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<IReadOnlyList<OwnershipReceiptDocument>>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        var ownerActorId = actorId.ToString("N");
        await HydrateOwnershipReceiptsFromSurrealAsync(ownerActorId, ct);

        var items = _state.OwnershipReceipts.Values
            .Where(slot => string.Equals(slot.OwnerActorId, ownerActorId, StringComparison.Ordinal))
            .Select(slot => slot.Receipt)
            .OrderByDescending(r => r.IssuedAt)
            .ToArray();

        return AZOAResult<IReadOnlyList<OwnershipReceiptDocument>>.Success(items);
    }

    public async Task<AZOAResult<OwnershipReceiptDocument>> IssueOwnershipReceiptAsync(
        Guid actorId,
        IssueOwnershipReceiptRequest request,
        CancellationToken ct = default)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<OwnershipReceiptDocument>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        if (request is null)
            return AZOAResult<OwnershipReceiptDocument>.FailureWithCode(
                "Ownership receipt request is required.", AzoaErrorCodes.InvalidRequest);

        if (string.IsNullOrWhiteSpace(request.AllocationOrSaleId) || request.AllocationOrSaleId.Any(char.IsControl))
            return AZOAResult<OwnershipReceiptDocument>.FailureWithCode(
                "allocationOrSaleId is required.", AzoaErrorCodes.InvalidRequest);

        if (!TryNormalizeSubject(request.HolderSubject, out var holderSubject))
            return AZOAResult<OwnershipReceiptDocument>.FailureWithCode(
                "A valid holderSubject is required.", AzoaErrorCodes.InvalidRequest);

        var holderClass = request.HolderClass?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!OwnershipHolderClasses.All.Contains(holderClass))
            return AZOAResult<OwnershipReceiptDocument>.FailureWithCode(
                $"Unsupported holderClass '{request.HolderClass}'.", AzoaErrorCodes.InvalidRequest);

        if (string.IsNullOrWhiteSpace(request.AssetSymbol)
            || request.AssetSymbol.Trim().Length > 32
            || request.AssetSymbol.Any(char.IsControl))
            return AZOAResult<OwnershipReceiptDocument>.FailureWithCode(
                "A valid assetSymbol is required.", AzoaErrorCodes.InvalidRequest);

        if (string.IsNullOrWhiteSpace(request.UnitsFormatted) || request.UnitsFormatted.Any(char.IsControl))
            return AZOAResult<OwnershipReceiptDocument>.FailureWithCode(
                "unitsFormatted is required.", AzoaErrorCodes.InvalidRequest);

        if (request.FiatMinorUnits < 0)
            return AZOAResult<OwnershipReceiptDocument>.FailureWithCode(
                "fiatMinorUnits cannot be negative.", AzoaErrorCodes.InvalidRequest);

        if (!TryNormalizeCurrency(request.CurrencyCode, out var currencyCode))
            return AZOAResult<OwnershipReceiptDocument>.FailureWithCode(
                "A 3-letter ISO currency code is required.", AzoaErrorCodes.InvalidRequest);

        var chainTxHash = NormalizeOptionalString(request.ChainTxHash);
        var verificationStatus = string.IsNullOrWhiteSpace(request.VerificationStatus)
            ? (chainTxHash is not null
                ? OwnershipVerificationStatuses.Verified
                : OwnershipVerificationStatuses.RecordedPendingChain)
            : request.VerificationStatus.Trim();

        if (!OwnershipVerificationStatuses.All.Contains(verificationStatus))
            return AZOAResult<OwnershipReceiptDocument>.FailureWithCode(
                $"Unsupported verificationStatus '{request.VerificationStatus}'.", AzoaErrorCodes.InvalidRequest);

        var ownerActorId = actorId.ToString("N");
        var issuedAt = _clock.GetUtcNow();
        var receiptId = $"rcp_{Guid.NewGuid():N}";
        var seq = Interlocked.Increment(ref _state.OwnershipReceiptSequence);
        var receiptNumber = $"RCP-{issuedAt:yyyyMMdd}-{seq:D4}";
        var allocationOrSaleId = request.AllocationOrSaleId.Trim();
        var assetSymbol = request.AssetSymbol.Trim();
        var assetResourceId = NormalizeOptionalString(request.AssetResourceId);
        var unitsFormatted = request.UnitsFormatted.Trim();

        var draft = new OwnershipReceiptDocument(
            receiptId,
            receiptNumber,
            allocationOrSaleId,
            holderSubject,
            holderClass,
            assetSymbol,
            assetResourceId,
            unitsFormatted,
            request.FiatMinorUnits,
            currencyCode,
            chainTxHash,
            ContentHash: string.Empty,
            issuedAt,
            verificationStatus);

        var contentHash = ComputeOwnershipReceiptContentHash(draft);
        var document = draft with { ContentHash = contentHash };

        _state.OwnershipReceipts[receiptId] = new CommercialPrimitivesStoreState.OwnershipReceiptSlot(ownerActorId, document);

        var record = new OwnershipReceiptRecord
        {
            Id = document.ReceiptId,
            OwnerActorId = ownerActorId,
            ReceiptNumber = document.ReceiptNumber,
            AllocationOrSaleId = document.AllocationOrSaleId,
            HolderSubject = document.HolderSubject,
            HolderClass = document.HolderClass,
            AssetSymbol = document.AssetSymbol,
            AssetResourceId = document.AssetResourceId,
            UnitsFormatted = document.UnitsFormatted,
            FiatMinorUnits = document.FiatMinorUnits,
            CurrencyCode = document.CurrencyCode,
            ChainTxHash = document.ChainTxHash,
            ContentHash = document.ContentHash,
            IssuedAt = document.IssuedAt,
            VerificationStatus = document.VerificationStatus,
        };

        await TryExecuteSurrealCreateAsync(record, ct);
        return AZOAResult<OwnershipReceiptDocument>.Success(document);
    }

    public async Task<AZOAResult<IReadOnlyList<AccountDocumentReport>>> ListAccountDocumentsAsync(
        Guid actorId,
        CancellationToken ct = default)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<IReadOnlyList<AccountDocumentReport>>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        var ownerActorId = actorId.ToString("N");
        await HydrateAccountDocumentsFromSurrealAsync(ownerActorId, ct);

        var items = _state.AccountDocuments.Values
            .Where(slot => string.Equals(slot.OwnerActorId, ownerActorId, StringComparison.Ordinal))
            .Select(slot => slot.Document)
            .OrderByDescending(d => d.GeneratedAt)
            .ToArray();

        return AZOAResult<IReadOnlyList<AccountDocumentReport>>.Success(items);
    }

    public async Task<AZOAResult<AccountDocumentReport>> GenerateAccountDocumentAsync(
        Guid actorId,
        GenerateAccountDocumentRequest request,
        CancellationToken ct = default)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<AccountDocumentReport>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        if (request is null)
            return AZOAResult<AccountDocumentReport>.FailureWithCode(
                "Document generation request is required.", AzoaErrorCodes.InvalidRequest);

        var documentType = request.DocumentType?.Trim() ?? string.Empty;
        if (!AccountDocumentTypes.All.Contains(documentType))
            return AZOAResult<AccountDocumentReport>.FailureWithCode(
                $"Unsupported documentType '{request.DocumentType}'.", AzoaErrorCodes.InvalidRequest);

        if (request.PeriodStart.HasValue && request.PeriodEnd.HasValue
            && request.PeriodStart.Value > request.PeriodEnd.Value)
        {
            return AZOAResult<AccountDocumentReport>.FailureWithCode(
                "periodStart cannot be after periodEnd.", AzoaErrorCodes.InvalidRequest);
        }

        var ownerActorId = actorId.ToString("N");
        var subject = string.IsNullOrWhiteSpace(request.Subject)
            ? ownerActorId
            : request.Subject.Trim();

        if (subject.Length > 128 || subject.Any(char.IsControl))
            return AZOAResult<AccountDocumentReport>.FailureWithCode(
                "Invalid subject.", AzoaErrorCodes.InvalidRequest);

        await HydrateInvoicesFromSurrealAsync(ownerActorId, ct);
        await HydratePaymentRequestsFromSurrealAsync(ownerActorId, ct);
        await HydrateOwnershipReceiptsFromSurrealAsync(ownerActorId, ct);

        var invoices = _state.Invoices.Values
            .Where(slot => string.Equals(slot.OwnerActorId, ownerActorId, StringComparison.Ordinal))
            .Select(slot =>
            {
                lock (slot.SyncRoot)
                {
                    return slot.Invoice;
                }
            })
            .Where(inv => InPeriod(inv.CreatedAt, request.PeriodStart, request.PeriodEnd))
            .ToArray();

        var paymentRequests = _state.PaymentRequests.Values
            .Where(slot => string.Equals(slot.OwnerActorId, ownerActorId, StringComparison.Ordinal))
            .Select(slot => slot.Request)
            .Where(pr => InPeriod(pr.CreatedAt, request.PeriodStart, request.PeriodEnd))
            .ToArray();

        var receipts = _state.OwnershipReceipts.Values
            .Where(slot => string.Equals(slot.OwnerActorId, ownerActorId, StringComparison.Ordinal))
            .Select(slot => slot.Receipt)
            .Where(r => InPeriod(r.IssuedAt, request.PeriodStart, request.PeriodEnd))
            .ToArray();

        var openInvoices = invoices
            .Where(i => string.Equals(i.Status, CommercialInvoiceStatuses.Open, StringComparison.Ordinal))
            .ToArray();
        var paidInvoices = invoices
            .Where(i => string.Equals(i.Status, CommercialInvoiceStatuses.Paid, StringComparison.Ordinal))
            .ToArray();
        var voidInvoices = invoices
            .Where(i => string.Equals(i.Status, CommercialInvoiceStatuses.Void, StringComparison.Ordinal))
            .ToArray();

        var metrics = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["invoiceCount"] = invoices.Length,
            ["openInvoiceCount"] = openInvoices.Length,
            ["paidInvoiceCount"] = paidInvoices.Length,
            ["voidInvoiceCount"] = voidInvoices.Length,
            ["openInvoiceTotalMinorUnits"] = openInvoices.Sum(i => i.TotalMinorUnits),
            ["paidInvoiceTotalMinorUnits"] = paidInvoices.Sum(i => i.TotalMinorUnits),
            ["paymentRequestCount"] = paymentRequests.Length,
            ["paymentRequestTotalMinorUnits"] = paymentRequests.Sum(r => r.AmountMinorUnits),
            ["ownershipReceiptCount"] = receipts.Length,
            ["ownershipTotalFiatMinorUnits"] = receipts.Sum(r => r.FiatMinorUnits),
            ["activeDeferralCount"] = _deferrals.ListDeferrals()
                .Count(d => string.Equals(d.Status, ProviderDeferralStatuses.Deferred, StringComparison.Ordinal)),
        };

        var documentId = $"doc_{Guid.NewGuid():N}";
        var generatedAt = _clock.GetUtcNow();
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? DefaultDocumentTitle(documentType, generatedAt)
            : request.Title.Trim();

        if (title.Length > 160 || title.Any(char.IsControl))
            return AZOAResult<AccountDocumentReport>.FailureWithCode(
                "Invalid document title.", AzoaErrorCodes.InvalidRequest);

        var scope = string.Equals(documentType, AccountDocumentTypes.OwnershipCertificate, StringComparison.Ordinal)
            ? AccessLinkScopes.ReceiptView
            : AccessLinkScopes.StatementView;

        var downloadLinkResult = _accessLinks.Mint(new MintAccessLinkRequest(
            subject,
            scope,
            TargetResourceId: documentId));

        if (downloadLinkResult.IsError || downloadLinkResult.Result is null)
            return AZOAResult<AccountDocumentReport>.FailureWithCode(
                downloadLinkResult.Message,
                downloadLinkResult.Code ?? AzoaErrorCodes.InvalidRequest);

        var contentHash = ComputeAccountDocumentContentHash(
            documentId,
            subject,
            documentType,
            title,
            request.PeriodStart,
            request.PeriodEnd,
            metrics,
            generatedAt);

        var report = new AccountDocumentReport(
            documentId,
            subject,
            documentType,
            title,
            request.PeriodStart,
            request.PeriodEnd,
            metrics,
            contentHash,
            downloadLinkResult.Result.SignedUrl,
            generatedAt);

        _state.AccountDocuments[documentId] = new CommercialPrimitivesStoreState.AccountDocumentSlot(ownerActorId, report);

        var record = new AccountDocumentRecord
        {
            Id = report.DocumentId,
            OwnerActorId = ownerActorId,
            Subject = report.Subject,
            DocumentType = report.DocumentType,
            Title = report.Title,
            PeriodStart = report.PeriodStart,
            PeriodEnd = report.PeriodEnd,
            SummaryMetricsJson = JsonSerializer.Serialize(metrics, JsonOptions),
            ContentHash = report.ContentHash,
            DownloadUrl = report.DownloadUrl,
            GeneratedAt = report.GeneratedAt,
        };

        await TryExecuteSurrealCreateAsync(record, ct);
        return AZOAResult<AccountDocumentReport>.Success(report);
    }

    public AZOAResult<IReadOnlyList<ProviderDeferralEntry>> ListDeferrals(
        Guid actorId,
        string? provider = null)
    {
        if (actorId == Guid.Empty)
            return AZOAResult<IReadOnlyList<ProviderDeferralEntry>>.FailureWithCode(
                "Authenticated subject is required.", AzoaErrorCodes.Forbidden);

        _deferrals.EnsureIdentomatCredentialDeferralEvaluated();
        return AZOAResult<IReadOnlyList<ProviderDeferralEntry>>.Success(_deferrals.ListDeferrals(provider));
    }

    public static string ComputeOwnershipReceiptContentHash(OwnershipReceiptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var canonical = string.Join(
            '|',
            document.ReceiptId,
            document.ReceiptNumber,
            document.AllocationOrSaleId,
            document.HolderSubject,
            document.HolderClass,
            document.AssetSymbol,
            document.AssetResourceId ?? string.Empty,
            document.UnitsFormatted,
            document.FiatMinorUnits.ToString(CultureInfo.InvariantCulture),
            document.CurrencyCode,
            document.ChainTxHash ?? string.Empty,
            document.IssuedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            document.VerificationStatus);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static string ComputeAccountDocumentContentHash(
        string documentId,
        string subject,
        string documentType,
        string title,
        DateTimeOffset? periodStart,
        DateTimeOffset? periodEnd,
        IReadOnlyDictionary<string, object?> metrics,
        DateTimeOffset generatedAt)
    {
        var sortedMetrics = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in metrics)
            sortedMetrics[entry.Key] = entry.Value;
        var canonical = string.Join(
            '|',
            documentId,
            subject,
            documentType,
            title,
            periodStart?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            periodEnd?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            JsonSerializer.Serialize(sortedMetrics, JsonOptions),
            generatedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private async Task<CommercialPrimitivesStoreState.InvoiceSlot?> FindOwnedInvoiceAsync(
        string ownerActorId,
        string invoiceId,
        CancellationToken ct)
    {
        if (_state.Invoices.TryGetValue(invoiceId, out var existing))
        {
            return string.Equals(existing.OwnerActorId, ownerActorId, StringComparison.Ordinal)
                ? existing
                : null;
        }

        if (_surrealExecutor is null)
            return null;

        try
        {
            var row = await _surrealExecutor.QuerySingleAsync<CommercialInvoiceRecord>(
                SurrealQuery<CommercialInvoiceRecord>.Key(invoiceId),
                ct);

            if (row is null || !string.Equals(row.OwnerActorId, ownerActorId, StringComparison.Ordinal))
                return null;

            var hydrated = ToCommercialInvoice(row);
            var slot = new CommercialPrimitivesStoreState.InvoiceSlot(row.OwnerActorId, hydrated);
            return _state.Invoices.GetOrAdd(hydrated.Id, slot);
        }
        catch (SurrealStatementException ex)
        {
            LogSurrealFallback("LookupInvoice", ex);
        }
        catch (InvalidOperationException ex)
        {
            LogSurrealFallback("LookupInvoice", ex);
        }
        catch (HttpRequestException ex)
        {
            LogSurrealFallback("LookupInvoice", ex);
        }
        catch (SocketException ex)
        {
            LogSurrealFallback("LookupInvoice", ex);
        }
        catch (TimeoutException ex)
        {
            LogSurrealFallback("LookupInvoice", ex);
        }
        catch (JsonException ex)
        {
            LogSurrealFallback("LookupInvoice", ex);
        }

        return null;
    }

    private async Task HydrateInvoicesFromSurrealAsync(string ownerActorId, CancellationToken ct)
    {
        if (_surrealExecutor is null)
            return;

        try
        {
            var rows = await _surrealExecutor.QueryAsync<CommercialInvoiceRecord>(
                SurrealQuery<CommercialInvoiceRecord>.From()
                    .Where(row => row.OwnerActorId == ownerActorId)
                    .OrderByDescending(row => row.CreatedAt)
                    .Limit(DefaultQueryLimit),
                ct);

            foreach (var row in rows)
            {
                var invoice = ToCommercialInvoice(row);
                _state.Invoices.TryAdd(
                    invoice.Id,
                    new CommercialPrimitivesStoreState.InvoiceSlot(row.OwnerActorId, invoice));
            }
        }
        catch (SurrealStatementException ex)
        {
            LogSurrealFallback("ListInvoices", ex);
        }
        catch (InvalidOperationException ex)
        {
            LogSurrealFallback("ListInvoices", ex);
        }
        catch (HttpRequestException ex)
        {
            LogSurrealFallback("ListInvoices", ex);
        }
        catch (SocketException ex)
        {
            LogSurrealFallback("ListInvoices", ex);
        }
        catch (TimeoutException ex)
        {
            LogSurrealFallback("ListInvoices", ex);
        }
        catch (JsonException ex)
        {
            LogSurrealFallback("ListInvoices", ex);
        }
    }

    private async Task HydratePaymentRequestsFromSurrealAsync(string ownerActorId, CancellationToken ct)
    {
        if (_surrealExecutor is null)
            return;

        try
        {
            var rows = await _surrealExecutor.QueryAsync<PaymentRequestRecordModel>(
                SurrealQuery<PaymentRequestRecordModel>.From()
                    .Where(row => row.OwnerActorId == ownerActorId)
                    .OrderByDescending(row => row.CreatedAt)
                    .Limit(DefaultQueryLimit),
                ct);

            foreach (var row in rows)
            {
                var dto = new PaymentRequestRecord(
                    row.Id,
                    row.RequestNumber,
                    row.Title,
                    row.RequesterSubject,
                    row.PayerSubject,
                    row.CurrencyCode,
                    row.AmountMinorUnits,
                    row.TokenEquivalentUnits,
                    row.Memo,
                    row.Status,
                    row.DueAt,
                    row.PaymentLinkUrl,
                    row.CreatedAt,
                    row.PaidAt);

                _state.PaymentRequests.TryAdd(
                    dto.Id,
                    new CommercialPrimitivesStoreState.PaymentRequestSlot(row.OwnerActorId, dto));
            }
        }
        catch (SurrealStatementException ex)
        {
            LogSurrealFallback("ListPaymentRequests", ex);
        }
        catch (InvalidOperationException ex)
        {
            LogSurrealFallback("ListPaymentRequests", ex);
        }
        catch (HttpRequestException ex)
        {
            LogSurrealFallback("ListPaymentRequests", ex);
        }
        catch (SocketException ex)
        {
            LogSurrealFallback("ListPaymentRequests", ex);
        }
        catch (TimeoutException ex)
        {
            LogSurrealFallback("ListPaymentRequests", ex);
        }
    }

    private async Task HydrateOwnershipReceiptsFromSurrealAsync(string ownerActorId, CancellationToken ct)
    {
        if (_surrealExecutor is null)
            return;

        try
        {
            var rows = await _surrealExecutor.QueryAsync<OwnershipReceiptRecord>(
                SurrealQuery<OwnershipReceiptRecord>.From()
                    .Where(row => row.OwnerActorId == ownerActorId)
                    .OrderByDescending(row => row.IssuedAt)
                    .Limit(DefaultQueryLimit),
                ct);

            foreach (var row in rows)
            {
                var dto = new OwnershipReceiptDocument(
                    row.Id,
                    row.ReceiptNumber,
                    row.AllocationOrSaleId,
                    row.HolderSubject,
                    row.HolderClass,
                    row.AssetSymbol,
                    row.AssetResourceId,
                    row.UnitsFormatted,
                    row.FiatMinorUnits,
                    row.CurrencyCode,
                    row.ChainTxHash,
                    row.ContentHash,
                    row.IssuedAt,
                    row.VerificationStatus);

                _state.OwnershipReceipts.TryAdd(
                    dto.ReceiptId,
                    new CommercialPrimitivesStoreState.OwnershipReceiptSlot(row.OwnerActorId, dto));
            }
        }
        catch (SurrealStatementException ex)
        {
            LogSurrealFallback("ListOwnershipReceipts", ex);
        }
        catch (InvalidOperationException ex)
        {
            LogSurrealFallback("ListOwnershipReceipts", ex);
        }
        catch (HttpRequestException ex)
        {
            LogSurrealFallback("ListOwnershipReceipts", ex);
        }
        catch (SocketException ex)
        {
            LogSurrealFallback("ListOwnershipReceipts", ex);
        }
        catch (TimeoutException ex)
        {
            LogSurrealFallback("ListOwnershipReceipts", ex);
        }
    }

    private async Task HydrateAccountDocumentsFromSurrealAsync(string ownerActorId, CancellationToken ct)
    {
        if (_surrealExecutor is null)
            return;

        try
        {
            var rows = await _surrealExecutor.QueryAsync<AccountDocumentRecord>(
                SurrealQuery<AccountDocumentRecord>.From()
                    .Where(row => row.OwnerActorId == ownerActorId)
                    .OrderByDescending(row => row.GeneratedAt)
                    .Limit(DefaultQueryLimit),
                ct);

            foreach (var row in rows)
            {
                var metrics = DeserializeMetrics(row.SummaryMetricsJson);
                var dto = new AccountDocumentReport(
                    row.Id,
                    row.Subject,
                    row.DocumentType,
                    row.Title,
                    row.PeriodStart,
                    row.PeriodEnd,
                    metrics,
                    row.ContentHash,
                    row.DownloadUrl,
                    row.GeneratedAt);

                _state.AccountDocuments.TryAdd(
                    dto.DocumentId,
                    new CommercialPrimitivesStoreState.AccountDocumentSlot(row.OwnerActorId, dto));
            }
        }
        catch (SurrealStatementException ex)
        {
            LogSurrealFallback("ListAccountDocuments", ex);
        }
        catch (InvalidOperationException ex)
        {
            LogSurrealFallback("ListAccountDocuments", ex);
        }
        catch (HttpRequestException ex)
        {
            LogSurrealFallback("ListAccountDocuments", ex);
        }
        catch (SocketException ex)
        {
            LogSurrealFallback("ListAccountDocuments", ex);
        }
        catch (TimeoutException ex)
        {
            LogSurrealFallback("ListAccountDocuments", ex);
        }
        catch (JsonException ex)
        {
            LogSurrealFallback("ListAccountDocuments", ex);
        }
    }

    private async Task TryExecuteSurrealCreateAsync<TRecord>(TRecord record, CancellationToken ct)
        where TRecord : class, ISurrealRecord, new()
    {
        if (_surrealExecutor is null)
            return;

        try
        {
            var response = await _surrealExecutor.ExecuteAsync(SurrealWriter.Create(record), ct);
            response.EnsureAllOk();
        }
        catch (SurrealStatementException ex)
        {
            LogSurrealFallback(typeof(TRecord).Name, ex);
        }
        catch (InvalidOperationException ex)
        {
            LogSurrealFallback(typeof(TRecord).Name, ex);
        }
        catch (HttpRequestException ex)
        {
            LogSurrealFallback(typeof(TRecord).Name, ex);
        }
        catch (SocketException ex)
        {
            LogSurrealFallback(typeof(TRecord).Name, ex);
        }
        catch (TimeoutException ex)
        {
            LogSurrealFallback(typeof(TRecord).Name, ex);
        }
    }

    private async Task TryUpdateInvoicePaidAsync(
        string ownerActorId,
        string invoiceId,
        DateTimeOffset paidAt,
        CancellationToken ct)
    {
        if (_surrealExecutor is null)
            return;

        try
        {
            var query = SurrealWriter.UpdateOnly<CommercialInvoiceRecord>(invoiceId)
                .Where(row => row.OwnerActorId == ownerActorId)
                .Set(row => row.Status, CommercialInvoiceStatuses.Paid)
                .Set(row => row.PaidAt, paidAt)
                .Build();

            var response = await _surrealExecutor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
        }
        catch (SurrealStatementException ex)
        {
            LogSurrealFallback("PayInvoice", ex);
        }
        catch (InvalidOperationException ex)
        {
            LogSurrealFallback("PayInvoice", ex);
        }
        catch (HttpRequestException ex)
        {
            LogSurrealFallback("PayInvoice", ex);
        }
        catch (SocketException ex)
        {
            LogSurrealFallback("PayInvoice", ex);
        }
        catch (TimeoutException ex)
        {
            LogSurrealFallback("PayInvoice", ex);
        }
    }

    private async Task TryUpdateInvoiceVoidedAsync(
        string ownerActorId,
        string invoiceId,
        DateTimeOffset voidedAt,
        CancellationToken ct)
    {
        if (_surrealExecutor is null)
            return;

        try
        {
            var query = SurrealWriter.UpdateOnly<CommercialInvoiceRecord>(invoiceId)
                .Where(row => row.OwnerActorId == ownerActorId)
                .Set(row => row.Status, CommercialInvoiceStatuses.Void)
                .Set(row => row.VoidedAt, voidedAt)
                .Build();

            var response = await _surrealExecutor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
        }
        catch (SurrealStatementException ex)
        {
            LogSurrealFallback("VoidInvoice", ex);
        }
        catch (InvalidOperationException ex)
        {
            LogSurrealFallback("VoidInvoice", ex);
        }
        catch (HttpRequestException ex)
        {
            LogSurrealFallback("VoidInvoice", ex);
        }
        catch (SocketException ex)
        {
            LogSurrealFallback("VoidInvoice", ex);
        }
        catch (TimeoutException ex)
        {
            LogSurrealFallback("VoidInvoice", ex);
        }
    }

    private void LogSurrealFallback(string operation, Exception ex)
    {
        _logger.LogWarning(
            ex,
            "Commercial primitives SurrealDB write-through/read-through fallback on {Operation}; continuing with in-memory state.",
            operation);
    }

    private static CommercialInvoice ToCommercialInvoice(CommercialInvoiceRecord row)
    {
        var lines = string.IsNullOrWhiteSpace(row.LineItemsJson)
            ? Array.Empty<InvoiceLineItem>()
            : JsonSerializer.Deserialize<InvoiceLineItem[]>(row.LineItemsJson, JsonOptions)
              ?? Array.Empty<InvoiceLineItem>();

        return new CommercialInvoice(
            row.Id,
            row.InvoiceNumber,
            row.Title,
            row.IssuerSubject,
            row.PayerSubject,
            row.CurrencyCode,
            row.SubtotalMinorUnits,
            row.FeeMinorUnits,
            row.TotalMinorUnits,
            row.TokenEquivalentUnits,
            lines,
            row.Status,
            row.DueAt,
            row.PaymentLinkUrl,
            row.CreatedAt,
            row.PaidAt,
            row.VoidedAt);
    }

    private static IReadOnlyDictionary<string, object?> DeserializeMetrics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions)
               ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static bool TryNormalizeSubject(string? raw, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Any(char.IsControl))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = raw.Trim();
        return normalized.Length is >= 1 and <= 128;
    }

    private static bool TryNormalizeCurrency(string? raw, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            normalized = string.Empty;
            return false;
        }

        var trimmed = raw.Trim().ToUpperInvariant();
        if (trimmed.Length != 3 || !trimmed.All(c => c is >= 'A' and <= 'Z'))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = trimmed;
        return true;
    }

    private static string? NormalizeOptionalString(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static bool InPeriod(DateTimeOffset timestamp, DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start.HasValue && timestamp < start.Value)
            return false;
        if (end.HasValue && timestamp > end.Value)
            return false;
        return true;
    }

    private static string DefaultDocumentTitle(string documentType, DateTimeOffset generatedAt)
        => documentType switch
        {
            AccountDocumentTypes.MonthlyStatement => $"Monthly Statement ({generatedAt:yyyy-MM})",
            AccountDocumentTypes.PayoutLedgerReport => $"Payout Ledger Report ({generatedAt:yyyy-MM-dd})",
            AccountDocumentTypes.OwnershipCertificate => $"Ownership Certificate ({generatedAt:yyyy-MM-dd})",
            AccountDocumentTypes.KycConsentAttestation => $"KYC & Consent Attestation ({generatedAt:yyyy-MM-dd})",
            AccountDocumentTypes.TaxActivitySummary => $"Tax Activity Summary ({generatedAt:yyyy})",
            _ => $"Account Document ({generatedAt:yyyy-MM-dd})",
        };
}

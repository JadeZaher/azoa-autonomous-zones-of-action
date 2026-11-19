// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Web;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Services.Commercial;
using AZOA.WebAPI.Services.Compliance;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Commercial;

public sealed class CommercialPrimitivesTests
{
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset initialUtcNow)
        {
            _utcNow = initialUtcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public sealed record Entry(LogLevel Level, string Message);

        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new Entry(logLevel, formatter(state, exception)));
        }
    }

    private static IConfiguration BuildConfiguration(
        string hostedBaseUrl = "https://node.azoa.example",
        bool liveIdentomatCredentials = false,
        string? jwtKey = "unit-test-hmac-signing-key-material-0123456789")
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Jwt:Key"] = jwtKey,
            ["Tenant:ClaimInvites:Enabled"] = "true",
            ["Tenant:ClaimInvites:StubDeliveryAcknowledged"] = "true",
            ["Tenant:ClaimInvites:HostedBaseUrl"] = hostedBaseUrl,
            ["Tenant:ClaimInvites:AllowedReturnOrigins:0"] = hostedBaseUrl,
            ["Kyc:Provider"] = "identomat",
            ["Kyc:Identomat:CompanyKey"] = liveIdentomatCredentials ? "live-company-key" : "",
            ["Kyc:Identomat:ConfigId"] = liveIdentomatCredentials ? "live-config-id" : "",
            ["Kyc:Identomat:CallbackApiKey"] = liveIdentomatCredentials ? "live-callback-key-0123456789abcdef0123456789" : "",
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static KycSettings BuildKycSettings(bool liveCredentials)
        => new()
        {
            Provider = "identomat",
            Identomat = new IdentomatKycSettings
            {
                CompanyKey = liveCredentials ? "live-company-key" : "",
                ConfigId = liveCredentials ? "live-config-id" : "",
                CallbackApiKey = liveCredentials ? "live-callback-key-0123456789abcdef0123456789" : "",
            },
        };

    private static CommercialPrimitivesService CreateService(
        out MutableTimeProvider clock,
        out CapturingLogger<ProviderDeferralLedger> deferralLogger,
        out ProviderDeferralLedger ledger,
        bool liveIdentomatCredentials = false,
        string hostedBaseUrl = "https://node.azoa.example",
        string? jwtKey = "unit-test-hmac-signing-key-material-0123456789",
        IKycManager? kycManager = null)
    {
        clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        deferralLogger = new CapturingLogger<ProviderDeferralLedger>();
        var kycSettings = Options.Create(BuildKycSettings(liveIdentomatCredentials));
        ledger = new ProviderDeferralLedger(kycSettings, deferralLogger, clock);
        var config = BuildConfiguration(hostedBaseUrl, liveIdentomatCredentials, jwtKey);
        var handoffOptions = MemberHandoffOptions.FromConfiguration(config);
        var accessLinks = new AccessLinkMintingService(config, ledger, handoffOptions, clock);

        return new CommercialPrimitivesService(
            new CommercialPrimitivesStoreState(),
            accessLinks,
            ledger,
            kycSettings,
            NullLogger<CommercialPrimitivesService>.Instance,
            clock,
            surrealExecutor: null,
            kycManager: kycManager);
    }

    private static CommercialPrimitivesController CreateController(
        CommercialPrimitivesService service,
        Guid actorId,
        string? jsonBody = null)
    {
        var httpContext = new DefaultHttpContext();
        if (actorId != Guid.Empty)
        {
            httpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "TestAuth"));
        }

        if (jsonBody is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(jsonBody);
            httpContext.Request.Body = new MemoryStream(bytes);
            httpContext.Request.ContentLength = bytes.Length;
            httpContext.Request.ContentType = "application/json";
        }
        else
        {
            httpContext.Request.Body = new MemoryStream();
            httpContext.Request.ContentLength = 0;
        }

        return new CommercialPrimitivesController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Theory]
    [InlineData(AccessLinkScopes.Kyc)]
    [InlineData(AccessLinkScopes.Consent)]
    [InlineData(AccessLinkScopes.AddFunds)]
    [InlineData(AccessLinkScopes.CashOut)]
    [InlineData(AccessLinkScopes.InvoicePay)]
    [InlineData(AccessLinkScopes.ReceiptView)]
    [InlineData(AccessLinkScopes.StatementView)]
    public void AccessLink_MintAndVerify_SucceedsForAllSupportedScopes(string scope)
    {
        var service = CreateService(out _, out _, out _);
        var actorId = Guid.NewGuid();

        var mintResult = service.MintAccessLink(
            actorId,
            new MintAccessLinkRequest(
                ExternalSubject: "member-ext-42",
                Scope: scope,
                TargetResourceId: "res_123",
                ReturnUrl: "https://app.azoa.example/callback",
                TtlMinutes: 30));

        Assert.False(mintResult.IsError);
        var minted = Assert.IsType<SignedAccessLink>(mintResult.Result);
        Assert.StartsWith("https://node.azoa.example/ride/", minted.SignedUrl, StringComparison.Ordinal);
        Assert.Equal(scope, minted.Scope);
        Assert.Equal("member-ext-42", minted.ExternalSubject);
        Assert.Equal(AccessLinkDeferralStatuses.DeferredCredentials, minted.DeferralStatus);
        Assert.Equal(ProviderDeferralLedger.IdentomatCredentialsPendingReasonCode, minted.DeferredReason);

        var uri = new Uri(minted.SignedUrl);
        var query = HttpUtility.ParseQueryString(uri.Query);
        var exp = long.Parse(query["exp"]!);
        var sig = query["sig"]!;

        var verifiedResult = service.VerifyAccessLink(minted.LinkId, scope, "member-ext-42", exp, sig);
        Assert.False(verifiedResult.IsError);
        var verified = Assert.IsType<AccessLinkVerificationResult>(verifiedResult.Result);
        Assert.True(verified.Valid);
        Assert.Equal(minted.LinkId, verified.LinkId);
        Assert.Equal(scope, verified.Scope);
        Assert.Equal("member-ext-42", verified.ExternalSubject);
        Assert.Equal("res_123", verified.TargetResourceId);
        Assert.Equal("https://app.azoa.example/callback", verified.ReturnUrl);
    }

    [Fact]
    public void AccessLink_Verify_RejectsTamperingAndExpiration_AndUsesEphemeralFallbackWithoutJwtKey()
    {
        var service = CreateService(out var clock, out _, out _, jwtKey: null);
        var actorId = Guid.NewGuid();

        var minted = service.MintAccessLink(
            actorId,
            new MintAccessLinkRequest("sub-tamper", AccessLinkScopes.ReceiptView, TtlMinutes: 15)).Result!;

        var query = HttpUtility.ParseQueryString(new Uri(minted.SignedUrl).Query);
        var exp = long.Parse(query["exp"]!);
        var sig = query["sig"]!;

        var tamperedSig = sig[..^2] + (sig.EndsWith("AA", StringComparison.Ordinal) ? "BB" : "AA");
        var badSig = service.VerifyAccessLink(minted.LinkId, AccessLinkScopes.ReceiptView, "sub-tamper", exp, tamperedSig);
        Assert.True(badSig.IsError);
        Assert.Equal("INVALID_SIGNATURE", badSig.Code);
        Assert.False(badSig.Result!.Valid);

        var tamperedScope = service.VerifyAccessLink(minted.LinkId, AccessLinkScopes.CashOut, "sub-tamper", exp, sig);
        Assert.True(tamperedScope.IsError);
        Assert.Equal("INVALID_SIGNATURE", tamperedScope.Code);

        var tamperedSubject = service.VerifyAccessLink(minted.LinkId, AccessLinkScopes.ReceiptView, "other-user", exp, sig);
        Assert.True(tamperedSubject.IsError);
        Assert.Equal("INVALID_SIGNATURE", tamperedSubject.Code);

        var tamperedExp = service.VerifyAccessLink(minted.LinkId, AccessLinkScopes.ReceiptView, "sub-tamper", exp + 60, sig);
        Assert.True(tamperedExp.IsError);
        Assert.Equal("INVALID_SIGNATURE", tamperedExp.Code);

        clock.Advance(TimeSpan.FromMinutes(16));
        var expired = service.VerifyAccessLink(minted.LinkId, AccessLinkScopes.ReceiptView, "sub-tamper", exp, sig);
        Assert.True(expired.IsError);
        Assert.Equal("LINK_EXPIRED", expired.Code);
        Assert.False(expired.Result!.Valid);
    }

    [Fact]
    public void AccessLink_Mint_RejectsUnsupportedScopeAndUnsafeReturnUrl()
    {
        var service = CreateService(out _, out _, out _);
        var actorId = Guid.NewGuid();

        var badScope = service.MintAccessLink(actorId, new MintAccessLinkRequest("sub-1", "admin_override"));
        Assert.True(badScope.IsError);
        Assert.Equal(AzoaErrorCodes.InvalidRequest, badScope.Code);

        var badScheme = service.MintAccessLink(
            actorId,
            new MintAccessLinkRequest("sub-1", AccessLinkScopes.Kyc, ReturnUrl: "javascript:alert(1)"));
        Assert.True(badScheme.IsError);
        Assert.Equal(AzoaErrorCodes.InvalidRequest, badScheme.Code);
    }

    [Fact]
    public void IdentomatDeferralLedger_LogsWarningAtStartupAndDeferralOnSessionStartWhenKeysMissing()
    {
        var service = CreateService(out _, out var logger, out _, liveIdentomatCredentials: false);
        var actorId = Guid.NewGuid();

        var startupWarning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(ProviderDeferralLedger.IdentomatCredentialsPendingReasonCode, startupWarning.Message, StringComparison.Ordinal);

        var statusResult = service.GetIdentomatStatus(actorId);
        Assert.False(statusResult.IsError);
        Assert.Equal(IdentomatSessionStatuses.PendingVendorCredentials, statusResult.Result!.Status);
        Assert.True(statusResult.Result.DeferralLogged);
        Assert.Equal(
            [
                IdentomatKycSettings.CompanyKeyName,
                IdentomatKycSettings.ConfigIdName,
                IdentomatKycSettings.CallbackApiKeyName,
            ],
            statusResult.Result.MissingConfigurationKeys);

        var sessionResult = service.StartIdentomatSession(actorId, null);
        Assert.False(sessionResult.IsError);
        var session = sessionResult.Result!;
        Assert.Equal("Identomat", session.Provider);
        Assert.Equal(IdentomatSessionStatuses.PendingVendorCredentials, session.Status);
        Assert.True(session.DeferralLogged);
        Assert.Equal(ProviderDeferralLedger.IdentomatCredentialsPendingReasonCode, session.DeferredReason);
        Assert.NotNull(session.SignedAccessUrl);
        Assert.NotNull(session.VerificationUrl);

        var uri = new Uri(session.SignedAccessUrl!);
        var linkId = uri.Segments[^1];
        var query = HttpUtility.ParseQueryString(uri.Query);
        var verified = service.VerifyAccessLink(
            linkId,
            query["scope"]!,
            query["sub"]!,
            long.Parse(query["exp"]!),
            query["sig"]!);
        Assert.False(verified.IsError);
        Assert.True(verified.Result!.Valid);

        var deferrals = service.ListDeferrals(actorId, "Identomat");
        Assert.False(deferrals.IsError);
        var entry = Assert.Single(deferrals.Result!);
        Assert.Equal(ProviderDeferralStatuses.Deferred, entry.Status);
        Assert.Equal(ProviderDeferralLedger.IdentomatCredentialsPendingReasonCode, entry.ReasonCode);
    }

    [Fact]
    public void IdentomatDeferralLedger_ReturnsActivePendingSessionWhenCredentialsConfigured()
    {
        var service = CreateService(out _, out var logger, out var ledger, liveIdentomatCredentials: true);
        var actorId = Guid.NewGuid();

        Assert.True(ledger.HasLiveIdentomatCredentials);
        Assert.Empty(logger.Entries);
        Assert.Empty(ledger.ListDeferrals());

        var sessionResult = service.StartIdentomatSession(
            actorId,
            new IdentomatSessionRequest("ext-member-live", "https://app.azoa.example/kyc-done", 20));
        Assert.False(sessionResult.IsError);
        var session = sessionResult.Result!;
        Assert.Equal(IdentomatSessionStatuses.Pending, session.Status);
        Assert.False(session.DeferralLogged);
        Assert.Null(session.DeferredReason);
        Assert.Empty(session.MissingConfigurationKeys);
        Assert.NotNull(session.SignedAccessUrl);
    }

    [Fact]
    public async Task CommercialInvoices_LifecycleAndIdorIsolationWorkAsSpecified()
    {
        var service = CreateService(out _, out _, out _);
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();

        var createdResult = await service.CreateInvoiceAsync(
            ownerA,
            new CreateCommercialInvoiceRequest(
                Title: "Node Onboarding Invoice",
                PayerSubject: "payer-001",
                CurrencyCode: "usd",
                FeeMinorUnits: 250,
                LineItems:
                [
                    new InvoiceLineItemRequest("Compliance verification", 2, 1500),
                    new InvoiceLineItemRequest("Settlement rail setup", 1, 5000),
                ],
                TokenEquivalentUnits: "82.50"),
            CancellationToken.None);

        Assert.False(createdResult.IsError);
        var created = createdResult.Result!;
        Assert.Equal("USD", created.CurrencyCode);
        Assert.Equal(8000, created.SubtotalMinorUnits);
        Assert.Equal(250, created.FeeMinorUnits);
        Assert.Equal(8250, created.TotalMinorUnits);
        Assert.Equal(CommercialInvoiceStatuses.Open, created.Status);
        Assert.NotNull(created.PaymentLinkUrl);

        var listB = await service.ListInvoicesAsync(ownerB);
        Assert.False(listB.IsError);
        Assert.Empty(listB.Result!);

        var payCrossOwner = await service.PayInvoiceAsync(ownerB, created.Id, null);
        Assert.True(payCrossOwner.IsError);
        Assert.Equal(AzoaErrorCodes.NotFound, payCrossOwner.Code);

        var voidCrossOwner = await service.VoidInvoiceAsync(ownerB, created.Id, new VoidCommercialInvoiceRequest("idor"));
        Assert.True(voidCrossOwner.IsError);
        Assert.Equal(AzoaErrorCodes.NotFound, voidCrossOwner.Code);

        var paid = await service.PayInvoiceAsync(ownerA, created.Id, new PayCommercialInvoiceRequest("wire-ref-99"));
        Assert.False(paid.IsError);
        Assert.Equal(CommercialInvoiceStatuses.Paid, paid.Result!.Status);
        Assert.NotNull(paid.Result.PaidAt);

        var voidPaid = await service.VoidInvoiceAsync(ownerA, created.Id, null);
        Assert.True(voidPaid.IsError);
        Assert.Equal(AzoaErrorCodes.Conflict, voidPaid.Code);

        var secondInvoice = (await service.CreateInvoiceAsync(
            ownerA,
            new CreateCommercialInvoiceRequest(
                Title: "Voidable Invoice",
                PayerSubject: "payer-002",
                CurrencyCode: "EUR",
                FeeMinorUnits: 0,
                LineItems: [new InvoiceLineItemRequest("Seat fee", 1, 1000)]))).Result!;

        var voided = await service.VoidInvoiceAsync(ownerA, secondInvoice.Id, new VoidCommercialInvoiceRequest("cancelled"));
        Assert.False(voided.IsError);
        Assert.Equal(CommercialInvoiceStatuses.Void, voided.Result!.Status);
        Assert.NotNull(voided.Result.VoidedAt);

        var payVoided = await service.PayInvoiceAsync(ownerA, secondInvoice.Id, null);
        Assert.True(payVoided.IsError);
        Assert.Equal(AzoaErrorCodes.Conflict, payVoided.Code);
    }

    [Fact]
    public async Task PaymentRequests_CreateAndListEnforcesValidationAndOwnerScope()
    {
        var service = CreateService(out _, out _, out _);
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();

        var invalidAmount = await service.CreatePaymentRequestAsync(
            ownerA,
            new CreatePaymentRequestInput("Invalid", "payer-1", "USD", 0));
        Assert.True(invalidAmount.IsError);
        Assert.Equal(AzoaErrorCodes.InvalidRequest, invalidAmount.Code);

        var created = await service.CreatePaymentRequestAsync(
            ownerA,
            new CreatePaymentRequestInput(
                Title: "Capital Contribution Call",
                PayerSubject: "member-payer-88",
                CurrencyCode: "usd",
                AmountMinorUnits: 250000,
                TokenEquivalentUnits: "2500.00",
                Memo: "Tranche 1"));

        Assert.False(created.IsError);
        var pr = created.Result!;
        Assert.StartsWith("PR-", pr.RequestNumber, StringComparison.Ordinal);
        Assert.Equal("USD", pr.CurrencyCode);
        Assert.Equal(250000, pr.AmountMinorUnits);
        Assert.Equal(CommercialInvoiceStatuses.Open, pr.Status);
        Assert.NotNull(pr.PaymentLinkUrl);

        var listA = await service.ListPaymentRequestsAsync(ownerA);
        Assert.Single(listA.Result!);

        var listB = await service.ListPaymentRequestsAsync(ownerB);
        Assert.Empty(listB.Result!);
    }

    [Fact]
    public async Task OwnershipReceipts_ComputesDeterministicSha256ContentHashAndDetectsTampering()
    {
        var service = CreateService(out _, out _, out _);
        var actorId = Guid.NewGuid();

        var issuedResult = await service.IssueOwnershipReceiptAsync(
            actorId,
            new IssueOwnershipReceiptRequest(
                AllocationOrSaleId: "alloc-2026-001",
                HolderSubject: "holder-subject-7",
                HolderClass: "contributor",
                AssetSymbol: "AZOA-UNIT",
                UnitsFormatted: "1250.0000",
                FiatMinorUnits: 500000,
                CurrencyCode: "USD",
                AssetResourceId: "asset-pool-1"));

        Assert.False(issuedResult.IsError);
        var doc = issuedResult.Result!;
        Assert.Equal(OwnershipHolderClasses.Contributor, doc.HolderClass);
        Assert.Equal(OwnershipVerificationStatuses.RecordedPendingChain, doc.VerificationStatus);
        Assert.Matches("^[0-9a-f]{64}$", doc.ContentHash);

        var recomputed = CommercialPrimitivesService.ComputeOwnershipReceiptContentHash(doc);
        Assert.Equal(doc.ContentHash, recomputed);

        var tamperedHash = CommercialPrimitivesService.ComputeOwnershipReceiptContentHash(
            doc with { UnitsFormatted = "9999.0000" });
        Assert.NotEqual(doc.ContentHash, tamperedHash);

        var anchoredResult = await service.IssueOwnershipReceiptAsync(
            actorId,
            new IssueOwnershipReceiptRequest(
                AllocationOrSaleId: "alloc-2026-002",
                HolderSubject: "holder-subject-8",
                HolderClass: "INVESTOR",
                AssetSymbol: "AZOA-UNIT",
                UnitsFormatted: "500.0000",
                FiatMinorUnits: 200000,
                ChainTxHash: "0xabc123def456"));
        Assert.False(anchoredResult.IsError);
        Assert.Equal(OwnershipVerificationStatuses.Verified, anchoredResult.Result!.VerificationStatus);
    }

    [Fact]
    public async Task AccountDocuments_GenerateAggregatesMetricsAndMintsDownloadLink()
    {
        var service = CreateService(out _, out _, out _);
        var actorId = Guid.NewGuid();

        var inv = (await service.CreateInvoiceAsync(
            actorId,
            new CreateCommercialInvoiceRequest(
                "Inv 1",
                "payer-1",
                "USD",
                100,
                [new InvoiceLineItemRequest("Service", 1, 900)]))).Result!;
        await service.PayInvoiceAsync(actorId, inv.Id, null);

        await service.CreatePaymentRequestAsync(
            actorId,
            new CreatePaymentRequestInput("Req 1", "payer-2", "USD", 4500));

        await service.IssueOwnershipReceiptAsync(
            actorId,
            new IssueOwnershipReceiptRequest(
                "alloc-1",
                "holder-1",
                OwnershipHolderClasses.Member,
                "AZOA-UNIT",
                "100.00",
                10000));

        var docResult = await service.GenerateAccountDocumentAsync(
            actorId,
            new GenerateAccountDocumentRequest(AccountDocumentTypes.MonthlyStatement));

        Assert.False(docResult.IsError);
        var doc = docResult.Result!;
        Assert.Equal(AccountDocumentTypes.MonthlyStatement, doc.DocumentType);
        Assert.Matches("^[0-9a-f]{64}$", doc.ContentHash);
        Assert.NotNull(doc.DownloadUrl);
        Assert.Equal(1, doc.SummaryMetrics["invoiceCount"]);
        Assert.Equal(1, doc.SummaryMetrics["paidInvoiceCount"]);
        Assert.Equal(1000L, doc.SummaryMetrics["paidInvoiceTotalMinorUnits"]);
        Assert.Equal(1, doc.SummaryMetrics["paymentRequestCount"]);
        Assert.Equal(4500L, doc.SummaryMetrics["paymentRequestTotalMinorUnits"]);
        Assert.Equal(1, doc.SummaryMetrics["ownershipReceiptCount"]);
        Assert.Equal(10000L, doc.SummaryMetrics["ownershipTotalFiatMinorUnits"]);

        var docs = await service.ListAccountDocumentsAsync(actorId);
        Assert.Single(docs.Result!);
    }

    [Fact]
    public async Task CommercialPrimitivesController_HandlesAnonymousVerifyOptionalBodiesAndCrossOwner404()
    {
        var service = CreateService(out _, out _, out _);
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        var ownerController = CreateController(service, owner);
        var mintAction = ownerController.MintAccessLink(new MintAccessLinkRequest("sub-ctrl", AccessLinkScopes.Kyc));
        var mintOk = Assert.IsType<OkObjectResult>(mintAction.Result);
        var minted = Assert.IsType<AZOAResult<SignedAccessLink>>(mintOk.Value).Result!;

        var query = HttpUtility.ParseQueryString(new Uri(minted.SignedUrl).Query);
        var anonController = CreateController(service, Guid.Empty);
        var verifyOk = Assert.IsType<OkObjectResult>(anonController.VerifyAccessLink(
            minted.LinkId,
            query["scope"],
            query["sub"],
            long.Parse(query["exp"]!),
            query["sig"]).Result);
        Assert.True(Assert.IsType<AZOAResult<AccessLinkVerificationResult>>(verifyOk.Value).Result!.Valid);

        var verifyTampered = Assert.IsType<ObjectResult>(anonController.VerifyAccessLink(
            minted.LinkId,
            query["scope"],
            query["sub"],
            long.Parse(query["exp"]!),
            "tampered").Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, verifyTampered.StatusCode);

        var emptySessionAction = await ownerController.StartIdentomatSession(CancellationToken.None);
        var sessionOk = Assert.IsType<OkObjectResult>(emptySessionAction.Result);
        Assert.Equal(
            IdentomatSessionStatuses.PendingVendorCredentials,
            Assert.IsType<AZOAResult<IdentomatSessionStatus>>(sessionOk.Value).Result!.Status);

        var createdInvOk = Assert.IsType<OkObjectResult>((await ownerController.CreateInvoice(
            new CreateCommercialInvoiceRequest("Ctrl Invoice", "payer-c", "USD", 0, [new InvoiceLineItemRequest("Line", 1, 500)]),
            CancellationToken.None)).Result);
        var createdInv = Assert.IsType<AZOAResult<CommercialInvoice>>(createdInvOk.Value).Result!;

        var strangerController = CreateController(service, stranger);
        Assert.IsType<NotFoundObjectResult>((await strangerController.PayInvoice(createdInv.Id, CancellationToken.None)).Result);

        var paidOk = Assert.IsType<OkObjectResult>((await ownerController.PayInvoice(createdInv.Id, CancellationToken.None)).Result);
        Assert.Equal(
            CommercialInvoiceStatuses.Paid,
            Assert.IsType<AZOAResult<CommercialInvoice>>(paidOk.Value).Result!.Status);
    }

    [Fact]
    public void ConfigurationGates_EnabledInAppSettings_AndOffRampCSharpDefaultPreserved()
    {
        Assert.False(new OffRampOptions().RequestVoidEnabled);

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var prodJsonPath = Path.Combine(repoRoot, "appsettings.json");
        var devJsonPath = Path.Combine(repoRoot, "appsettings.Development.json");

        using var prodDoc = JsonDocument.Parse(File.ReadAllText(prodJsonPath));
        var prodInvites = prodDoc.RootElement.GetProperty("Tenant").GetProperty("ClaimInvites");
        Assert.True(prodInvites.GetProperty("Enabled").GetBoolean());
        Assert.True(prodInvites.GetProperty("StubDeliveryAcknowledged").GetBoolean());
        Assert.StartsWith("https://", prodInvites.GetProperty("HostedBaseUrl").GetString(), StringComparison.Ordinal);
        Assert.True(prodDoc.RootElement.GetProperty("Fiat").GetProperty("OffRamp").GetProperty("RequestVoidEnabled").GetBoolean());

        using var devDoc = JsonDocument.Parse(File.ReadAllText(devJsonPath));
        var devInvites = devDoc.RootElement.GetProperty("Tenant").GetProperty("ClaimInvites");
        Assert.True(devInvites.GetProperty("Enabled").GetBoolean());
        Assert.True(devInvites.GetProperty("StubDeliveryAcknowledged").GetBoolean());
        Assert.Equal("http://localhost:3100", devInvites.GetProperty("HostedBaseUrl").GetString());
        Assert.True(devDoc.RootElement.GetProperty("Fiat").GetProperty("OffRamp").GetProperty("RequestVoidEnabled").GetBoolean());
    }

    [Fact]
    public async Task IdentomatLiveDelegation_UsesRealKycManagerSessionAndMapsStatus()
    {
        var actorId = Guid.NewGuid();
        var expiresAt = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var kycManager = new Mock<IKycManager>();
        kycManager
            .Setup(m => m.BeginAsync(actorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSessionStartModel>.Success(new KycSessionStartModel
            {
                Provider = KycProvider.IDENTOMAT,
                ProviderKey = IdentomatKycProviderService.ProviderKeyValue,
                HostedVerification = true,
                ProviderSessionId = "real-azoa-session-01",
                VerificationUrl = "https://widget.identomat.com/?session_token=live-token-01",
                ExpiresAt = expiresAt,
            }));
        kycManager
            .Setup(m => m.GetStatusAsync(actorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmissionModel>.Success(new KycSubmissionModel
            {
                Id = Guid.NewGuid(),
                UserId = actorId,
                Provider = KycProvider.IDENTOMAT,
                ProviderKey = IdentomatKycProviderService.ProviderKeyValue,
                Status = KycStatus.IN_REVIEW,
                ProviderSessionId = "real-azoa-session-01",
                VerificationUrl = "https://widget.identomat.com/?session_token=live-token-01",
                ExpiresAt = expiresAt,
            }));

        var service = CreateService(out _, out _, out _, liveIdentomatCredentials: true, kycManager: kycManager.Object);

        var started = await service.StartIdentomatSessionAsync(
            actorId,
            new IdentomatSessionRequest("ext-live-1", "https://app.azoa.example/done", 25),
            CancellationToken.None);

        Assert.False(started.IsError);
        Assert.Equal(IdentomatSessionStatuses.Pending, started.Result!.Status);
        Assert.Equal("real-azoa-session-01", started.Result.SessionId);
        Assert.Equal("https://widget.identomat.com/?session_token=live-token-01", started.Result.VerificationUrl);
        Assert.StartsWith("https://node.azoa.example/ride/", started.Result.SignedAccessUrl, StringComparison.Ordinal);

        var status = await service.GetIdentomatStatusAsync(actorId, CancellationToken.None);
        Assert.False(status.IsError);
        Assert.Equal(IdentomatSessionStatuses.UnderReview, status.Result!.Status);
        Assert.Equal("real-azoa-session-01", status.Result.SessionId);
    }

    [Fact]
    public async Task IdentomatWireEnvelopeAndDefaultConfigId_UnwrapNestedResultAndOmitDefaultConfig()
    {
        var nestedJson = """{"result":{"id":"sess_live_99","url":"https://widget.identomat.com/?session_token=tok_live_99"}}""";
        var deserialized = JsonSerializer.Deserialize<IdentomatBeginResponse>(nestedJson)!;

        IdentomatBeginRequestWithSessionOverrides? sentBody = null;
        var transport = new Mock<IIdentomatHttpTransport>();
        transport
            .Setup(t => t.PostAsync<IdentomatBeginRequestWithSessionOverrides, IdentomatBeginResponse>(
                "https://external-api.identomat.com",
                "/begin",
                It.IsAny<IdentomatBeginRequestWithSessionOverrides>(),
                "begin",
                It.IsAny<CancellationToken>()))
            .Callback((string _, string __, IdentomatBeginRequestWithSessionOverrides body, string ___, CancellationToken ____) => sentBody = body)
            .ReturnsAsync(AZOAResult<IdentomatBeginResponse>.Success(deserialized));

        var settings = IdentomatTestFactory.CompleteSettings();
        settings.ConfigId = "default";
        var provider = new IdentomatKycProviderService(
            IdentomatTestFactory.Options(settings),
            transport.Object,
            IdentomatTestFactory.AlwaysAllowQuota(),
            Mock.Of<IHostedSessionFlow>());

        var created = await provider.CreateHostedSessionAsync(new HostedSessionRequest
        {
            FlowKind = HostedSessionFlowKinds.Kyc,
            SubjectUserId = Guid.NewGuid(),
            AdmissionKey = "kyc:identomat:default-test",
        });

        Assert.False(created.IsError, created.Message);
        Assert.Null(sentBody!.ConfigId);
        Assert.Equal("sess_live_99", created.Result!.ProviderSessionId);
        Assert.Equal("https://widget.identomat.com/?session_token=tok_live_99", created.Result.RedirectUrl);

        var inProgress = IdentomatKycProviderService.MapOutcome(new IdentomatResultResponse { Result = "in_progress" });
        Assert.True(inProgress.SessionExists);
        Assert.False(inProgress.IsTerminal);
    }

    [Fact]
    public async Task CommercialValidation_RejectsMalformedReferencesAndFragments()
    {
        var service = CreateService(out _, out _, out _);
        var actorId = Guid.NewGuid();

        var fragmentLink = service.MintAccessLink(
            actorId,
            new MintAccessLinkRequest("sub-frag", AccessLinkScopes.Kyc, ReturnUrl: "https://app.azoa.example/cb#secret"));
        Assert.True(fragmentLink.IsError);
        Assert.Equal(AzoaErrorCodes.InvalidRequest, fragmentLink.Code);

        var inv = (await service.CreateInvoiceAsync(
            actorId,
            new CreateCommercialInvoiceRequest("Inv Valid", "payer-v", "USD", 0, [new InvoiceLineItemRequest("Fee", 1, 100)]))).Result!;

        var badPay = await service.PayInvoiceAsync(actorId, inv.Id, new PayCommercialInvoiceRequest("   "));
        Assert.True(badPay.IsError);
        Assert.Equal(AzoaErrorCodes.InvalidRequest, badPay.Code);

        var badVoid = await service.VoidInvoiceAsync(actorId, inv.Id, new VoidCommercialInvoiceRequest("bad\nreason"));
        Assert.True(badVoid.IsError);
        Assert.Equal(AzoaErrorCodes.InvalidRequest, badVoid.Code);

        var badReceipt = await service.IssueOwnershipReceiptAsync(
            actorId,
            new IssueOwnershipReceiptRequest(
                "alloc-v",
                "holder-v",
                OwnershipHolderClasses.Member,
                "AZOA-UNIT",
                "10.00",
                1000,
                ChainTxHash: new string('a', 129)));
        Assert.True(badReceipt.IsError);
        Assert.Equal(AzoaErrorCodes.InvalidRequest, badReceipt.Code);
    }

    [Fact]
    public async Task PayPaymentRequest_ListAccessLinks_PostVerifyFallbackAndSimulatedReceiptHashWorkEndToEnd()
    {
        var service = CreateService(out _, out _, out _);
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        var createdReq = await service.CreatePaymentRequestAsync(
            owner,
            new CreatePaymentRequestInput("Req Pay", "payer-pr", "USD", 2500));
        Assert.False(createdReq.IsError);
        var pr = createdReq.Result!;
        Assert.Equal(CommercialInvoiceStatuses.Open, pr.Status);

        var badPayRef = await service.PayPaymentRequestAsync(owner, pr.Id, new PayPaymentRequestInput("   "));
        Assert.True(badPayRef.IsError);
        Assert.Equal(AzoaErrorCodes.InvalidRequest, badPayRef.Code);

        var strangerPay = await service.PayPaymentRequestAsync(stranger, pr.Id, null);
        Assert.True(strangerPay.IsError);
        Assert.Equal(AzoaErrorCodes.NotFound, strangerPay.Code);

        var paid = await service.PayPaymentRequestAsync(owner, pr.Id, new PayPaymentRequestInput("stripe_pi_123"));
        Assert.False(paid.IsError);
        Assert.Equal(CommercialInvoiceStatuses.Paid, paid.Result!.Status);
        Assert.NotNull(paid.Result.PaidAt);

        var doublePay = await service.PayPaymentRequestAsync(owner, pr.Id, null);
        Assert.True(doublePay.IsError);
        Assert.Equal(AzoaErrorCodes.Conflict, doublePay.Code);

        var simReceipt = await service.IssueOwnershipReceiptAsync(
            owner,
            new IssueOwnershipReceiptRequest(
                "alloc-sim",
                "holder-sim",
                OwnershipHolderClasses.Member,
                "AZOA-UNIT",
                "12.00",
                1200,
                ChainTxHash: "sim:simulated:tx-001"));
        Assert.False(simReceipt.IsError);
        Assert.Equal(OwnershipVerificationStatuses.SandboxSimulated, simReceipt.Result!.VerificationStatus);

        var minted = service.MintAccessLink(
            owner,
            new MintAccessLinkRequest("sub-list", AccessLinkScopes.InvoicePay, TargetResourceId: pr.Id, ReturnUrl: "https://app.azoa.example/done"));
        Assert.False(minted.IsError);
        var link = minted.Result!;

        var listed = service.ListAccessLinks(owner);
        Assert.False(listed.IsError);
        Assert.Contains(listed.Result!, x => x.LinkId == link.LinkId);

        // Fresh service instance without prior in-memory _metadata verifies via signed params + res/returnUrl fallback.
        var restartedService = CreateService(out _, out _, out _);
        var query = HttpUtility.ParseQueryString(new Uri(link.SignedUrl).Query);
        var postBody = JsonSerializer.Serialize(new VerifyAccessLinkRequest(
            Sig: query["sig"],
            Exp: query["exp"],
            Scope: query["scope"],
            Sub: query["sub"],
            Res: query["res"],
            ReturnUrl: query["returnUrl"]));
        var anonPostController = CreateController(restartedService, Guid.Empty, postBody);
        var postOk = Assert.IsType<OkObjectResult>(
            (await anonPostController.VerifyAccessLinkPost(link.LinkId, ct: CancellationToken.None)).Result);
        var verified = Assert.IsType<AZOAResult<AccessLinkVerificationResult>>(postOk.Value).Result!;
        Assert.True(verified.Valid);
        Assert.Equal(pr.Id, verified.TargetResourceId);
        Assert.Equal("https://app.azoa.example/done", verified.ReturnUrl);
    }
}

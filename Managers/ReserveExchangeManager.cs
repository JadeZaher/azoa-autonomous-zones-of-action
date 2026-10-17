// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Runs every refusable gate, then journals the exchange as a durable source before any commitment or
/// signing and settles it through the exact saved group. See <see cref="IReserveExchangeManager"/>.
/// </summary>
/// <remarks>See Services/Reserve/AGENTS.md §durable-exchange for ordering, idempotency and recovery.</remarks>
public sealed class ReserveExchangeManager : IReserveExchangeManager
{
    /// <summary>Operation type name of retired exchange rows; still the consent probe's capability name.</summary>
    public const string ChainOperationType = "Exchange";

    /// <summary>The scope an exchange signs under, paired with <see cref="ChainOperationType"/> by <see cref="AzoaScopes"/>.</summary>
    public const string ExchangeSigningScope = AzoaScopes.SwapSign;

    /// <summary>Parameter name on retired exchange rows; read only by generic reconciliation of those rows.</summary>
    public const string ProjectCreditAppliedParameter = "ExchangeProjectCreditApplied";

    private const string ExchangeFailedCode = "EXCHANGE_FAILED";
    private const string UnavailableMessage = "The exchange service is temporarily unavailable. Try again later.";
    private const string PendingMessage = "This exchange is still settling and has not been confirmed. Retry once it settles; do not raise a second one.";
    private const int MaxProjectReferenceLength = 128;
    private const int MaxPartitionLength = 64;
    private const int MaxPurposeLength = 256;
    private const int MaxHoldReferenceLength = 96;

    private readonly IValueAccessService _valueAccess;
    private readonly INodeGovernanceGuard _nodeGovernance;
    private readonly IPlatformReserve _reserve;
    private readonly IPlatformReserveLedgerStore _reserveLedgers;
    private readonly IProjectTreasury _treasury;
    private readonly IPlatformTokenGenesis _platformToken;
    private readonly IWalletStore _wallets;
    private readonly IExchangeRatePolicy _ratePolicy;
    private readonly IExchangeDeclaredRateWriter _declaredRates;
    private readonly IExchangeHoldSourceStore _sources;
    private readonly ExchangeHoldSourceService _settlement;
    private readonly ExchangeAuthorityFactory _authorities;
    private readonly SigningAdmissionAuthorityFactory _signing;
    private readonly IBlockchainProviderFactory _providers;
    private readonly ExchangeOptions _options;
    private readonly IValueRateSource _valueRateSource;
    private readonly ILogger<ReserveExchangeManager>? _logger;

    public ReserveExchangeManager(
        IValueAccessService valueAccess,
        IPlatformReserve reserve,
        IPlatformReserveLedgerStore reserveLedgers,
        IProjectTreasury treasury,
        IPlatformTokenGenesis platformToken,
        IWalletStore wallets,
        IExchangeRatePolicy ratePolicy,
        IExchangeDeclaredRateWriter declaredRates,
        IExchangeHoldSourceStore sources,
        ExchangeHoldSourceService settlement,
        ExchangeAuthorityFactory authorities,
        SigningAdmissionAuthorityFactory signing,
        IBlockchainProviderFactory providers,
        ExchangeOptions options,
        INodeGovernanceGuard? nodeGovernance = null,
        IValueRateSource? valueRateSource = null,
        ILogger<ReserveExchangeManager>? logger = null)
    {
        _valueAccess = valueAccess ?? throw new ArgumentNullException(nameof(valueAccess));
        _reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        _reserveLedgers = reserveLedgers ?? throw new ArgumentNullException(nameof(reserveLedgers));
        _treasury = treasury ?? throw new ArgumentNullException(nameof(treasury));
        _platformToken = platformToken ?? throw new ArgumentNullException(nameof(platformToken));
        _wallets = wallets ?? throw new ArgumentNullException(nameof(wallets));
        _ratePolicy = ratePolicy ?? throw new ArgumentNullException(nameof(ratePolicy));
        _declaredRates = declaredRates ?? throw new ArgumentNullException(nameof(declaredRates));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _settlement = settlement ?? throw new ArgumentNullException(nameof(settlement));
        _authorities = authorities ?? throw new ArgumentNullException(nameof(authorities));
        _signing = signing ?? throw new ArgumentNullException(nameof(signing));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nodeGovernance = nodeGovernance ?? NodeGovernanceGuard.Unrestricted;
        _valueRateSource = valueRateSource ?? UnpricedValueRateSource.Instance;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ExchangeReceipt>> ExchangeAsync(
        ExchangeRequest request, ExchangeCallerContext caller, CancellationToken ct = default)
    {
        if (request is null || caller is null)
            return Invalid("An exchange request and its caller context are required.");

        // ── Step 1: shape ─────────────────────────────────────────────────────
        var shapeError = DescribeShapeRefusal(request, caller);
        if (shapeError is not null)
            return Invalid(shapeError);
        if (!TryParseAmount(request.PlatformAmount, out var platformAmount, out var amountError)
            || !TryParseAmount(request.ProjectUnitAmount, out var projectUnitAmount, out amountError))
            return Invalid(amountError);

        var chainType = request.ChainType.Trim();
        var projectAssetId = request.ProjectAssetId.Trim();
        var partition = ReservePartitionCodec.Normalize(request.ReservePartition);
        var projectReference = request.ProjectReference.Trim();
        var requestKey = caller.ClientIdempotencyKey.Trim();
        var now = DateTimeOffset.UtcNow;

        // ── Step 2: an original source for this key is resumed, never re-decided ──
        var sourceId = ExchangeHoldSourceIdentity.ForRequest(caller.ActingTenantId, caller.ApiKeyId, requestKey);
        if (!request.PreviewOnly && await _sources.GetExchangeSourceAsync(sourceId, ct) is { } existing)
            return await ResumeAsync(existing, request, caller, ct);

        // ── Step 3: node governance ───────────────────────────────────────────
        var governance = await _nodeGovernance.EnsureAllowedAsync(chainType, projectAssetId, $"exchange:{request.Direction}", ct);
        if (governance.IsRefused)
            return NotAllowed(governance.ConsumerMessage);

        // ── Step 4: the platform unit this node actually issues ───────────────
        var platformState = await _platformToken.GetStateAsync(ct);
        if (platformState.IsError || platformState.Result is null)
        {
            _logger?.LogWarning("Exchange refused: the platform unit is not available ({Detail}).", platformState.Message);
            return Unavailable();
        }
        var platformAssetId = platformState.Result.Definition.AssetId;
        if (!string.Equals(platformState.Result.Definition.ChainType, chainType, StringComparison.OrdinalIgnoreCase))
            return NotAllowed("This node does not run its platform unit on the rail this exchange names.");

        // ── Step 5: the declared rate, and the policy that admits it ──────────
        if (!DeclaredRateBinding.TryBind(request.DeclaredRate, chainType, projectAssetId, out var declaredRate, out var rateError))
            return Invalid(rateError);
        var identity = ExchangeIdempotency.CreateFromClientKey(caller.ApiKeyId, requestKey);
        var evaluation = new ExchangeRateEvaluation(
            chainType, request.Direction, projectAssetId, platformAssetId,
            platformAmount, projectUnitAmount, declaredRate, now,
            TenantId: caller.ActingTenantId,
            ProjectReference: projectReference,
            SellerUserId: caller.TargetUserId,
            RequestKey: identity.Correlation,
            IsPreview: request.PreviewOnly);
        var decision = await _ratePolicy.EvaluateAsync(evaluation, ct);
        if (decision.IsError || decision.Result is null)
            return AZOAResult<ExchangeReceipt>.FailureWithCode(decision.Message, decision.Code ?? AzoaErrorCodes.OperationNotAllowed);

        // ── Step 6: the hold window and any automatic-release request ─────────
        var windowError = DescribeHoldWindowRefusal(request.ReleaseNotBefore, now) ?? DescribeReleaseRefusal(request);
        if (windowError is not null)
            return Invalid(windowError);

        // ── Step 6b: D-7 — settle only where the provider declares the exchange module for this network ──
        if (!ExchangeDeclared(chainType))
            return AZOAResult<ExchangeReceipt>.FailureWithCode(UnavailableMessage, ConsumerSafeErrorCodes.RailUnavailable);

        // ── Step 7: quote-only ────────────────────────────────────────────────
        if (request.PreviewOnly)
            return await PreviewAsync(request, caller, chainType, projectAssetId, platformAssetId, partition, projectReference,
                platformAmount, projectUnitAmount, decision.Result, sourceId, now, ct);

        // ── Step 8: actual authority and identity readiness, fail closed ──────
        if (caller.Credential is not { Kind: "ApiKey" } credential || credential.ActorUserId != caller.ActingTenantId
            || credential.ApiKeyId != caller.ApiKeyId || credential.TenantId is not null)
            return AZOAResult<ExchangeReceipt>.FailureWithCode("The authenticated API-key credential is required.", AzoaErrorCodes.Forbidden);
        var gate = await _valueAccess.RequireValueAccessAsync(caller.TargetUserId, caller.ActingTenantId, ct);
        if (gate.IsError)
            return AZOAResult<ExchangeReceipt>.FailureWithCode(gate.Message, AzoaErrorCodes.Forbidden);

        // ── Step 9: the accounts and the exact reserve identity ───────────────
        var accounts = await ResolveAccountsAsync(caller, chainType, projectAssetId, projectReference, ct);
        if (accounts.IsError || accounts.Result is not { } resolved)
            return AZOAResult<ExchangeReceipt>.FailureWithCode(accounts.Message, accounts.Code ?? AzoaErrorCodes.OperationNotAllowed);
        var refusal = await DescribeCommitmentRefusalAsync(request.Direction, resolved, partition, platformAssetId,
            platformAmount, projectUnitAmount, ct);
        if (refusal is not null)
            return RailRefusal<ExchangeReceipt>(refusal);

        // ── Step 10: record the rate this exchange is priced at ───────────────
        if (decision.Result.DeclaredRate is { } admittedRate && (await _declaredRates.RecordAsync(admittedRate, ct)).IsError)
            return AZOAResult<ExchangeReceipt>.FailureWithCode(
                "The rate for this exchange could not be recorded, so it was not run.", AzoaErrorCodes.DependencyUnavailable);

        // ── Step 11: what the recipient needs to hold what they receive (folded into the exact group) ──
        var takesDeliveryOf = request.Direction == ExchangeDirection.Buy
            ? (request.ReleaseNotBefore is null ? projectAssetId : null)
            : platformAssetId;
        string? recipientFunding = null;
        if (takesDeliveryOf is not null)
        {
            var readiness = await RecipientFundingAsync(resolved, takesDeliveryOf, ct);
            if (readiness.IsError)
                return AZOAResult<ExchangeReceipt>.FailureWithCode(readiness.Message, readiness.Code ?? ConsumerSafeErrorCodes.RecipientNotReady);
            recipientFunding = readiness.Result;
        }

        // ── Step 12: the immutable source, its two-party authority and release consent ──
        var terms = await TermsAsync(request, chainType, platformAssetId, projectAssetId, partition, projectReference,
            platformAmount, projectUnitAmount, decision.Result, now);
        var draft = Draft(request, caller, requestKey, resolved, partition, projectReference, platformAssetId,
            platformAmount, projectUnitAmount, terms, recipientFunding);
        if (draft is null)
            return AZOAResult<ExchangeReceipt>.FailureWithCode(
                "This node has no accepted network-fee cap for exchange settlement.", AzoaErrorCodes.DependencyUnavailable);
        var authority = await _authorities.BindAsync(credential, caller.ActingTenantId, caller.TargetUserId,
            resolved.Wallet.Id, resolved.Asset.ChainType, resolved.Wallet.Address, ct);
        if (authority.IsError || authority.Result is null)
            return AZOAResult<ExchangeReceipt>.FailureWithCode(authority.Message, AzoaErrorCodes.Forbidden);
        if (request.ReleaseAcceptance is { } acceptance)
        {
            var consent = await ReleaseConsentAsync(draft, request.ReleaseMandate!, acceptance, credential, ct);
            if (consent.IsError || consent.Result is null)
                return AZOAResult<ExchangeReceipt>.FailureWithCode(consent.Message, consent.Code ?? AzoaErrorCodes.InvalidRequest);
            draft = draft with { ReleaseConsent = consent.Result };
        }

        // ── Step 13: admit before any commitment, then settle the exact saved group ──
        var admitted = await _sources.AdmitExchangeSourceAsync(draft, authority.Result, ct);
        if (admitted is null)
            return AZOAResult<ExchangeReceipt>.FailureWithCode(
                "This Idempotency-Key already names a different exchange, or the tenant or participant authority changed.",
                AzoaErrorCodes.OperationNotAllowed);
        return await OutcomeAsync(await _settlement.ContinueAsync(admitted.Id, credential, ct), admitted.Id, ct);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ExchangeReceipt>> GetReceiptAsync(ExchangeReceiptQuery query, CancellationToken ct = default)
    {
        if (query is null || query.ApiKeyId == Guid.Empty || query.CallerUserId == Guid.Empty
            || string.IsNullOrWhiteSpace(query.ClientIdempotencyKey))
            return Invalid("A caller API key, caller identity and idempotency key are required.");
        var source = await _sources.GetExchangeSourceAsync(
            ExchangeHoldSourceIdentity.ForRequest(query.CallerUserId, query.ApiKeyId, query.ClientIdempotencyKey.Trim()), ct);
        if (source is null || source.TenantUserId != query.CallerUserId.ToString("N") || source.ApiKeyId != query.ApiKeyId.ToString("N"))
            return NotFound();
        var receipt = Receipt(source);
        return receipt is null ? Unavailable() : AZOAResult<ExchangeReceipt>.Success(receipt, "Exchange receipt found.");
    }

    // ── Resumption of an original source ─────────────────────────────────────

    private async Task<AZOAResult<ExchangeReceipt>> ResumeAsync(ExchangeHoldSource source, ExchangeRequest request,
        ExchangeCallerContext caller, CancellationToken ct)
    {
        var intent = JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson);
        var terms = intent is null ? null : JsonSerializer.Deserialize<ExchangeSourceTerms>(intent.OriginalRequestJson);
        if (intent is null || terms is null || source.ParticipantUserId != caller.TargetUserId.ToString("N"))
            return NotAllowed("This Idempotency-Key already names a different exchange.");
        if (terms.RequestJson != RequestJson(request))
            return NotAllowed("This Idempotency-Key already names a different exchange.");
        if (caller.Credential is not { Kind: "ApiKey" } credential)
            return Pending(source);
        if (source.State == "Admitted" && request.ReleaseAcceptance is { Disclosure: { } offered } acceptance
            && intent.ReleaseConsent is { } current && current.Quote != offered.Quote && request.ReleaseMandate is { } mandate)
        {
            var revised = await ReleaseConsentAsync(intent, mandate, acceptance, credential, ct);
            if (revised.IsError || revised.Result is null)
                return AZOAResult<ExchangeReceipt>.FailureWithCode(revised.Message, revised.Code ?? AzoaErrorCodes.InvalidRequest);
            if (await _sources.ReviseExchangeReleaseAsync(source.Id, new(acceptance.RequestId, source.Version, revised.Result), ct) is null)
                return NotAllowed("The release disclosure can be revised only before this exchange commits.");
        }
        return await OutcomeAsync(await _settlement.ContinueAsync(source.Id, credential, ct), source.Id, ct);
    }

    private async Task<AZOAResult<ExchangeReceipt>> OutcomeAsync(AZOAResult<ExchangeHoldSource> advanced, string sourceId,
        CancellationToken ct)
    {
        var source = advanced.Result ?? await _sources.GetExchangeSourceAsync(sourceId, ct);
        var receipt = source is null ? null : Receipt(source);
        if (source is null || receipt is null)
            return Unavailable();
        return source.State switch
        {
            "Completed" => AZOAResult<ExchangeReceipt>.Success(receipt, "Exchange completed."),
            "Compensated" or "Abandoned" => AZOAResult<ExchangeReceipt>.FailureWithCode(
                "This exchange did not settle and its commitment was returned. Nothing was moved.",
                ConsumerSafeErrorCodes.RailUnavailable, receipt),
            "Admitted" when advanced.IsError => AZOAResult<ExchangeReceipt>.FailureWithCode(
                UnavailableMessage, AzoaErrorCodes.DependencyUnavailable, receipt),
            "Admitted" => AZOAResult<ExchangeReceipt>.FailureWithCode(
                "This exchange could not take its reserve or treasury commitment. Nothing was moved.",
                ConsumerSafeErrorCodes.Declined, receipt),
            _ => AZOAResult<ExchangeReceipt>.FailureWithCode(PendingMessage, ConsumerSafeErrorCodes.SettlementPending, receipt),
        };
    }

    private AZOAResult<ExchangeReceipt> Pending(ExchangeHoldSource source)
    {
        var receipt = Receipt(source);
        if (receipt is null) return Unavailable();
        receipt.Replayed = true;
        return receipt.State == ExchangeReceiptState.AwaitingReconciliation
            ? AZOAResult<ExchangeReceipt>.FailureWithCode(PendingMessage, ConsumerSafeErrorCodes.SettlementPending, receipt)
            : AZOAResult<ExchangeReceipt>.Success(receipt, "Duplicate request: returning the original exchange.");
    }

    // ── Preview and automatic-release disclosure ─────────────────────────────

    private async Task<AZOAResult<ExchangeReceipt>> PreviewAsync(ExchangeRequest request, ExchangeCallerContext caller,
        string chainType, string projectAssetId, string platformAssetId, string partition, string projectReference,
        BigInteger platformAmount, BigInteger projectUnitAmount, ExchangeRateDecision decision, string sourceId,
        DateTimeOffset now, CancellationToken ct)
    {
        var previewGate = await _valueAccess.RequireValueAccessAsync(caller.TargetUserId, caller.ActingTenantId, ct);
        if (previewGate.IsError)
            return AZOAResult<ExchangeReceipt>.Failure(previewGate.Message);
        var buying = request.Direction == ExchangeDirection.Buy;
        var receipt = new ExchangeReceipt
        {
            ReceiptReference = string.Empty,
            SourceId = null,
            State = ExchangeReceiptState.AwaitingReconciliation,
            RequiresReconciliation = true,
            Direction = request.Direction,
            UserId = caller.TargetUserId,
            ProjectReference = projectReference,
            AmountSpent = Format(buying ? platformAmount : projectUnitAmount),
            AmountSpentAssetId = buying ? platformAssetId : projectAssetId,
            AmountReceived = Format(buying ? projectUnitAmount : platformAmount),
            AmountReceivedAssetId = buying ? projectAssetId : platformAssetId,
            HoldReleasesAt = request.ReleaseNotBefore,
            Rate = decision.DeclaredRate?.MinorUnitsPerBaseUnit.ToString(CultureInfo.InvariantCulture),
            RateCurrencyCode = decision.DeclaredRate?.CurrencyCode,
            RateSource = decision.DeclaredRate?.Source,
            RateVersion = decision.DeclaredRate?.Version,
            RateAsOf = decision.DeclaredRate?.DeclaredAt,
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
        };
        if (request.ReleaseMandate is { } mandate)
        {
            var accounts = await ResolveAccountsAsync(caller, chainType, projectAssetId, projectReference, ct);
            if (accounts.IsError || accounts.Result is not { } resolved)
                return AZOAResult<ExchangeReceipt>.FailureWithCode(accounts.Message, accounts.Code ?? AzoaErrorCodes.OperationNotAllowed);
            var terms = await TermsAsync(request, chainType, platformAssetId, projectAssetId, partition, projectReference,
                platformAmount, projectUnitAmount, decision, now);
            var draft = Draft(request, caller, caller.ClientIdempotencyKey.Trim(), resolved, partition, projectReference,
                platformAssetId, platformAmount, projectUnitAmount, terms);
            if (draft is null)
                return AZOAResult<ExchangeReceipt>.FailureWithCode(
                    "This node has no accepted network-fee cap for exchange settlement.", AzoaErrorCodes.DependencyUnavailable);
            var disclosure = await DiscloseReleaseAsync(draft, mandate, ct);
            if (disclosure.IsError || disclosure.Result is null)
                return AZOAResult<ExchangeReceipt>.FailureWithCode(disclosure.Message, disclosure.Code ?? AzoaErrorCodes.OperationNotAllowed);
            receipt.ReleaseDisclosure = disclosure.Result;
            receipt.HoldReference = draft.Opening?.HoldId;
            receipt.SourceId = sourceId;
        }
        return AZOAResult<ExchangeReceipt>.Success(receipt, "Quote only: nothing was moved and no exchange was admitted.");
    }

    private async Task<AZOAResult<ExchangeReleaseDisclosure>> DiscloseReleaseAsync(ExchangeHoldSourceIntent draft,
        ExchangeReleaseMandateRequest mandate, CancellationToken ct)
    {
        if (draft.Opening?.Definition is not { ReleaseNotBefore: { } notBefore } definition)
            return AZOAResult<ExchangeReleaseDisclosure>.FailureWithCode("Automatic release applies only to a held purchase.", AzoaErrorCodes.InvalidRequest);
        var asset = draft.Opening!.Asset;
        var provider = _providers.GetProvider(asset.ChainType, asset.Network);
        if (!provider.TryGetModule<IValueCustodyModule>(out var custody) || custody is null)
            return AZOAResult<ExchangeReleaseDisclosure>.FailureWithCode(UnavailableMessage, AzoaErrorCodes.DependencyUnavailable);
        var quote = await custody.QuoteValueCustodyAsync(new(asset.ChainType, asset.Network, asset.AssetId, asset.HoldingAddress,
            draft.ParticipantWallet.Address, definition.QuantityBaseUnits, asset.HoldingAddress), ct);
        if (quote.IsError || quote.Result is null)
            return RailRefusal<ExchangeReleaseDisclosure>(quote.Message);
        var terms = new HoldReleaseMandateTerms(1, HoldCommandIdentity.Hold(draft.Opening!), CanonicalJson.Hash(definition), asset,
            definition.QuantityBaseUnits, draft.ParticipantWallet, notBefore, mandate.ExpiresAt, quote.Result.NativeCurrency,
            quote.Result.NativeDecimals, new(asset.HoldingAddress, asset.HoldingAddress, mandate.MaximumNetworkFeeBaseUnits,
                mandate.MaximumLockedBalanceBaseUnits, mandate.MaximumNonrefundableCostBaseUnits));
        if (!HoldReleaseMandateIdentity.CostsWithin(quote.Result, terms.MaximumCosts))
            return AZOAResult<ExchangeReleaseDisclosure>.FailureWithCode(
                "The current release costs exceed the requested caps.", AzoaErrorCodes.OperationNotAllowed);
        return AZOAResult<ExchangeReleaseDisclosure>.Success(new(terms, quote.Result,
            CanonicalJson.Hash(new { Terms = terms, Quote = quote.Result })));
    }

    /// <summary>Verifies an echoed disclosure against fresh server mechanics and captures transfer:sign from the same credential.</summary>
    private async Task<AZOAResult<ExchangeReleaseConsent>> ReleaseConsentAsync(ExchangeHoldSourceIntent draft,
        ExchangeReleaseMandateRequest mandate, ExchangeReleaseAcceptance acceptance, SigningCredentialWitness credential,
        CancellationToken ct)
    {
        if (acceptance.RequestId == Guid.Empty || acceptance.Disclosure is not { } offered)
            return AZOAResult<ExchangeReleaseConsent>.FailureWithCode("The previewed release disclosure is required.", AzoaErrorCodes.InvalidRequest);
        var fresh = await DiscloseReleaseAsync(draft, mandate, ct);
        if (fresh.IsError || fresh.Result is null)
            return AZOAResult<ExchangeReleaseConsent>.FailureWithCode(fresh.Message, fresh.Code ?? AzoaErrorCodes.OperationNotAllowed);
        var current = fresh.Result;
        if (offered.Terms != current.Terms || offered.DisclosureHash != CanonicalJson.Hash(new { offered.Terms, offered.Quote })
            || !ValueCustodyIdentity.ValidQuote(offered.Quote) || offered.Quote.ExpiresAt <= DateTimeOffset.UtcNow
            || offered.Quote.Request != current.Quote.Request || offered.Quote.ProtocolIdentity != current.Quote.ProtocolIdentity
            || offered.Quote.AssetAuthorityHash != current.Quote.AssetAuthorityHash
            || !HoldReleaseMandateIdentity.CostsWithin(offered.Quote, offered.Terms.MaximumCosts))
            return AZOAResult<ExchangeReleaseConsent>.FailureWithCode(
                "The accepted release disclosure is stale or differs from these terms; preview again.", AzoaErrorCodes.OperationNotAllowed);
        var witness = await _signing.BindConsentAsync(credential, [AzoaScopes.TransferSign], ct);
        if (witness.IsError || witness.Result is null)
            return AZOAResult<ExchangeReleaseConsent>.FailureWithCode(
                "Automatic release needs the API key's transfer-signing scope.", AzoaErrorCodes.Forbidden);
        return AZOAResult<ExchangeReleaseConsent>.Success(new(acceptance.RequestId, offered.Terms, offered.Quote, witness.Result,
            offered.DisclosureHash));
    }

    /// <summary>
    /// Reads, without signing, whether the participant can hold <paramref name="assetId"/>: null when ready, else the
    /// reserve-paid funding the exact group carries ahead of its acceptance (Services/Reserve/AGENTS.md §recipient-readiness).
    /// </summary>
    private async Task<AZOAResult<string?>> RecipientFundingAsync(ResolvedAccounts accounts, string assetId, CancellationToken ct)
    {
        var provider = _providers.GetProvider(accounts.Asset.ChainType, accounts.Asset.Network);
        if (!provider.TryGetModule<IRecipientPreparationModule>(out var module) || module is not { SupportsRecipientPreparation: true })
            return AZOAResult<string?>.FailureWithCode(UnavailableMessage, ConsumerSafeErrorCodes.RailUnavailable);
        var readiness = await module.GetRecipientAssetReadinessAsync(assetId, accounts.Wallet.Address, ct);
        if (readiness.IsError || readiness.Result is not { } state)
            return RailRefusal<string?>(readiness.Message);
        if (state.IsBlocked)
            return RailRefusal<string?>("frozen");
        if (state.CanReceive)
            return AZOAResult<string?>.Success(null);
        if (!UnsignedIntegerText.TryParse(state.ShortfallBaseUnits, out var shortfall)
            || !UnsignedIntegerText.TryParse(_options.MaximumRecipientFundingBaseUnits?.Trim(), out var cap) || shortfall > cap)
            return RailRefusal<string?>("minimum balance");
        return AZOAResult<string?>.Success(shortfall.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// D-7: the provider's own <see cref="IExchangeTransactionModule"/> advertisement for the settlement network is the single
    /// activation pin; absent, an exchange (and its quote) refuses before any journal row (Services/Reserve/AGENTS.md §d7-activation).
    /// </summary>
    private bool ExchangeDeclared(string chainType)
    {
        try
        {
            return _providers.GetProvider(chainType, _providers.DefaultNetwork).TryGetModule<IExchangeTransactionModule>(out var module)
                && module is not null;
        }
        catch (ValueRailProviderNotFoundException) { return false; }
    }

    // ── Source construction ──────────────────────────────────────────────────

    private ExchangeHoldSourceIntent? Draft(ExchangeRequest request, ExchangeCallerContext caller, string requestKey,
        ResolvedAccounts accounts, string partition, string projectReference, string platformAssetId,
        BigInteger platformAmount, BigInteger projectUnitAmount, ExchangeSourceTerms terms, string? recipientFunding = null)
    {
        if (!UnsignedIntegerText.TryParse(_options.MaximumGroupNetworkFeeBaseUnits?.Trim(), out var cap) || cap <= 0)
            return null;
        var tenant = caller.ActingTenantId;
        var participant = caller.TargetUserId;
        var sourceId = ExchangeHoldSourceIdentity.ForRequest(tenant, caller.ApiKeyId, requestKey);
        var wallet = new SigningWalletWitness(accounts.Wallet.Id, participant, accounts.Wallet.Address, accounts.Wallet.ChainType);
        HoldCommandIntent? opening = null;
        ExchangeUnitsCommitment? units = null;
        if (request.Direction == ExchangeDirection.Buy && request.ReleaseNotBefore is { } releaseNotBefore)
        {
            var holdId = string.IsNullOrWhiteSpace(request.HoldReference)
                ? $"exchange:{ExchangeIdempotency.CreateFromClientKey(caller.ApiKeyId, requestKey).Correlation}"
                : request.HoldReference!.Trim();
            var definition = new HoldDefinition(accounts.Asset, Format(projectUnitAmount), "Reserve", participant, null, partition,
                releaseNotBefore, request.ReleaseMandate is not null, request.Purpose?.Trim());
            opening = new HoldCommandIntent(1, "exchange:" + sourceId, "Open", holdId, tenant, tenant, null, tenant,
                accounts.Asset, definition);
        }
        else
            units = new(request.Direction == ExchangeDirection.Buy ? "Issue" : "Return", accounts.Asset, Format(projectUnitAmount), partition,
                recipientFunding);
        return new ExchangeHoldSourceIntent(1, requestKey, caller.ApiKeyId, tenant, participant,
            CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(terms)), opening, platformAssetId, Format(platformAmount),
            projectReference, accounts.TreasuryAddress, wallet, cap.ToString(CultureInfo.InvariantCulture), Units: units);
    }

    private async Task<ExchangeSourceTerms> TermsAsync(ExchangeRequest request, string chainType, string platformAssetId,
        string projectAssetId, string partition, string projectReference, BigInteger platformAmount, BigInteger projectUnitAmount,
        ExchangeRateDecision decision, DateTimeOffset now)
    {
        var buying = request.Direction == ExchangeDirection.Buy;
        var value = await StampValueAsync(chainType, buying ? platformAssetId : projectAssetId,
            buying ? platformAmount : projectUnitAmount, now);
        var rate = decision.DeclaredRate;
        return new ExchangeSourceTerms(1, RequestJson(request), decision.PolicyVersion,
            rate?.MinorUnitsPerBaseUnit.ToString(CultureInfo.InvariantCulture), rate?.CurrencyCode, rate?.Source, rate?.Version,
            rate?.DeclaredAt, value);
    }

    /// <summary>Canonical caller terms of one exchange; a replay under the same key must match exactly.</summary>
    private static string RequestJson(ExchangeRequest request)
        => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            ChainType = request.ChainType?.Trim(), request.Direction, ProjectReference = request.ProjectReference?.Trim(),
            ProjectAssetId = request.ProjectAssetId?.Trim(), PlatformAmount = request.PlatformAmount?.Trim(),
            ProjectUnitAmount = request.ProjectUnitAmount?.Trim(), ReservePartition = ReservePartitionCodec.Normalize(request.ReservePartition),
            request.DeclaredRate, request.ReleaseNotBefore, HoldReference = request.HoldReference?.Trim(), Purpose = request.Purpose?.Trim(),
            request.ReleaseMandate,
        }));

    private async Task<string?> StampValueAsync(string chainType, string assetId, BigInteger baseUnits, DateTimeOffset at)
    {
        var dimension = ValueDimension.ChainAsset(assetId);
        try
        {
            var quote = await _valueRateSource.TryQuoteAsync(dimension, chainType, at, CancellationToken.None);
            return quote is null ? null : ValueStamp.From(RailAmount.In(dimension, baseUnits), quote).MinorUnits;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "The value rate source failed for an exchange on {Chain}; recording it unpriced.", chainType);
            return null;
        }
    }

    // ── Accounts and commitment pre-checks ───────────────────────────────────

    private async Task<AZOAResult<ResolvedAccounts>> ResolveAccountsAsync(ExchangeCallerContext caller, string chainType,
        string projectAssetId, string projectReference, CancellationToken ct)
    {
        var wallets = await _wallets.GetByUserAsync(caller.TargetUserId, ct);
        if (wallets.IsError)
            return AZOAResult<ResolvedAccounts>.FailureWithCode(UnavailableMessage, AzoaErrorCodes.DependencyUnavailable);
        var wallet = wallets.Result?.FirstOrDefault(candidate =>
            string.Equals(candidate.ChainType, chainType, StringComparison.OrdinalIgnoreCase));
        if (wallet is null)
            return AZOAResult<ResolvedAccounts>.FailureWithCode(
                "This person has no account on this rail yet, so there is nothing to exchange.", AzoaErrorCodes.InvalidRequest);
        var reserve = await _reserve.GetAccountAsync(chainType, ct);
        if (reserve.IsError || reserve.Result is null)
        {
            _logger?.LogWarning("Exchange refused: the reserve account for {Chain} did not resolve ({Detail}).", chainType, reserve.Message);
            return RailRefusal<ResolvedAccounts>(reserve.Message);
        }
        var network = _providers.DefaultNetwork;
        var ledger = await _reserveLedgers.GetAsync(PlatformReserveLedger.RecordIdFor(chainType, network.ToString(), projectAssetId), ct);
        if (ledger.IsError)
            return AZOAResult<ResolvedAccounts>.FailureWithCode(UnavailableMessage, AzoaErrorCodes.DependencyUnavailable);
        if (ledger.Result is not { } row || string.IsNullOrWhiteSpace(row.ProtocolIdentity) || row.AssetId != projectAssetId
            || !string.Equals(row.Chain, chainType, StringComparison.OrdinalIgnoreCase) || row.Network != network.ToString()
            || !string.Equals(row.ReserveAddress, reserve.Result.Address, StringComparison.Ordinal))
            return AZOAResult<ResolvedAccounts>.FailureWithCode(
                "The project's units are not registered with this node's reserve.", AzoaErrorCodes.OperationNotAllowed);
        var treasury = await _treasury.ResolveAsync(new ProjectTreasuryReference(row.Chain, caller.ActingTenantId, projectReference), ct);
        if (treasury.IsError || treasury.Result is null)
        {
            _logger?.LogWarning("Exchange refused: the treasury for project {Project} did not resolve ({Detail}).", projectReference, treasury.Message);
            return RailRefusal<ResolvedAccounts>(treasury.Message);
        }
        if (treasury.Result.Status != ProjectTreasuryStatus.Open)
            return AZOAResult<ResolvedAccounts>.FailureWithCode("This project is no longer accepting funds.", AzoaErrorCodes.OperationNotAllowed);
        return AZOAResult<ResolvedAccounts>.Success(new(wallet, new(row.Chain, network, row.ProtocolIdentity, row.AssetId, row.ReserveAddress),
            treasury.Result.Address, row, new ProjectTreasuryReference(row.Chain, caller.ActingTenantId, projectReference)));
    }

    /// <summary>Explains an oversubscribed partition or short treasury before admission; the source transaction rechecks atomically.</summary>
    private async Task<string?> DescribeCommitmentRefusalAsync(ExchangeDirection direction, ResolvedAccounts accounts, string partition,
        string platformAssetId, BigInteger platformAmount, BigInteger projectUnitAmount, CancellationToken ct)
    {
        if (direction == ExchangeDirection.Buy)
        {
            var planned = HoldReserveMutationPlan.Create(accounts.Reserve, partition, ReserveDrawKind.Hold,
                Format(projectUnitAmount), accounts.Asset.ProtocolIdentity);
            return planned.IsError ? planned.Message : null;
        }
        var returned = HoldReserveMutationPlan.Create(accounts.Reserve, partition, ReserveDrawKind.Return,
            Format(projectUnitAmount), accounts.Asset.ProtocolIdentity);
        if (returned.IsError) return returned.Message;
        var ledger = await _treasury.GetLedgerAsync(accounts.Treasury, ct);
        if (ledger.IsError || ledger.Result is null) return ledger.Message;
        var assets = ProjectTreasuryAssetCodec.Decode(ledger.Result.Assets, out var error);
        if (assets is null) return error;
        var counters = assets.GetValueOrDefault(platformAssetId) ?? ProjectTreasuryAssetCounters.Empty;
        return counters.TryApply(ProjectTreasuryMovementKind.Hold, platformAmount, platformAssetId, out _, out var shortfall)
            ? null : shortfall;
    }

    // ── Receipts ──────────────────────────────────────────────────────────────

    private static ExchangeReceipt? Receipt(ExchangeHoldSource source)
    {
        var intent = JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson);
        var terms = intent is null ? null : JsonSerializer.Deserialize<ExchangeSourceTerms>(intent.OriginalRequestJson);
        var unitsAsset = intent?.Opening?.Asset ?? intent?.Units?.Asset;
        var unitsQuantity = intent?.Opening?.Definition?.QuantityBaseUnits ?? intent?.Units?.QuantityBaseUnits;
        if (intent is null || terms is null || unitsAsset is null || unitsQuantity is null) return null;
        var buying = intent.Units?.Kind != "Return";
        var prepared = source.PreparedWireJson is null ? null : JsonSerializer.Deserialize<PreparedExchangeWire>(source.PreparedWireJson);
        var state = source.State switch
        {
            "Completed" => ExchangeReceiptState.Completed,
            "Compensated" or "Abandoned" => ExchangeReceiptState.Failed,
            _ => ExchangeReceiptState.AwaitingReconciliation,
        };
        return new ExchangeReceipt
        {
            ReceiptReference = ExchangeIdempotency.CreateFromClientKey(intent.ApiKeyId, intent.RequestKey).Correlation,
            SourceId = source.Id,
            State = state,
            IsTerminal = state != ExchangeReceiptState.AwaitingReconciliation,
            RequiresReconciliation = state == ExchangeReceiptState.AwaitingReconciliation,
            Direction = buying ? ExchangeDirection.Buy : ExchangeDirection.Sell,
            UserId = intent.ParticipantUserId,
            ProjectReference = intent.ProjectReference,
            AmountSpent = buying ? intent.PlatformQuantityBaseUnits : unitsQuantity,
            AmountSpentAssetId = buying ? intent.PlatformAssetId : unitsAsset.AssetId,
            AmountReceived = buying ? unitsQuantity : intent.PlatformQuantityBaseUnits,
            AmountReceivedAssetId = buying ? unitsAsset.AssetId : intent.PlatformAssetId,
            ProjectCreditAmount = buying && state == ExchangeReceiptState.Completed ? intent.PlatformQuantityBaseUnits : null,
            SettlementReference = prepared?.GroupId,
            SettlementLegReferences = prepared?.TransactionIds ?? Array.Empty<string>(),
            HoldReference = intent.Opening?.HoldId,
            HoldReleasesAt = intent.Opening?.Definition?.ReleaseNotBefore,
            ReleaseMandateId = intent.ReleaseConsent is not null && source.HoldCommandId is not null
                ? CanonicalJson.Hash(new { Domain = "exchange-release-mandate-v1", source.Id }) : null,
            Rate = terms.Rate,
            RateCurrencyCode = terms.RateCurrencyCode,
            RateSource = terms.RateSource,
            RateVersion = terms.RateVersion,
            RateAsOf = terms.RateAsOf,
            ValueMinorUnits = terms.ValueMinorUnits,
            CreatedAt = source.CreatedAt.UtcDateTime,
            UpdatedAt = source.UpdatedAt.UtcDateTime,
            FailureCode = state == ExchangeReceiptState.Failed ? ExchangeFailedCode : null,
        };
    }

    // ── Shape ─────────────────────────────────────────────────────────────────

    private static string? DescribeShapeRefusal(ExchangeRequest request, ExchangeCallerContext caller)
    {
        if (caller.ApiKeyId == Guid.Empty || caller.CallerUserId == Guid.Empty)
            return "The exchange caller context is incomplete.";
        if (caller.TargetUserId == Guid.Empty)
            return "An exchange must name the person it is for.";
        if (caller.ActingTenantId == Guid.Empty)
            return "An exchange must be driven by a resolvable tenant.";
        if (string.IsNullOrWhiteSpace(caller.ClientIdempotencyKey))
            return "An Idempotency-Key is required. Two identical sponsorships are a normal thing to "
                + "want, so the node will not guess whether a repeat is a retry or a second one.";
        if (string.IsNullOrWhiteSpace(request.ChainType))
            return "An exchange must name the rail it settles on.";
        if (string.IsNullOrWhiteSpace(request.ProjectReference) || request.ProjectReference.Trim().Length > MaxProjectReferenceLength)
            return $"A project reference is required and must be {MaxProjectReferenceLength} characters or fewer.";
        if (string.IsNullOrWhiteSpace(request.ProjectAssetId))
            return "An exchange must name the project's units.";
        if (!IsValidPartition(request.ReservePartition))
            return "A reserve partition is required and must be 1-" + MaxPartitionLength + " characters of "
                + "letters, digits, '-' or '_'. Without it one purpose could quietly draw down another's earmark.";
        if (request.Purpose is { Length: > MaxPurposeLength })
            return $"Purpose must be {MaxPurposeLength} characters or fewer.";
        if (request.HoldReference is { } holdReference
            && (holdReference.Trim().Length == 0 || holdReference.Trim().Length > MaxHoldReferenceLength))
            return $"A supplied hold reference must be 1-{MaxHoldReferenceLength} characters.";
        return request.Direction is ExchangeDirection.Buy or ExchangeDirection.Sell
            ? null
            : $"Unsupported exchange direction: {request.Direction}.";
    }

    private string? DescribeHoldWindowRefusal(DateTimeOffset? releaseNotBefore, DateTimeOffset now)
    {
        if (releaseNotBefore is not { } window)
            return null;
        if (window <= now)
            return "A hold window must end in the future; use no window to take delivery now.";
        return window - now > _options.MaxHoldWindow
            ? "The requested hold window is longer than this node allows."
            : null;
    }

    private static string? DescribeReleaseRefusal(ExchangeRequest request)
    {
        if (request.ReleaseAcceptance is not null && request.ReleaseMandate is null)
            return "A release acceptance must accompany the automatic-release request it accepts.";
        if (request.ReleaseMandate is not { } mandate)
            return null;
        if (request.Direction != ExchangeDirection.Buy || request.ReleaseNotBefore is not { } window)
            return "Automatic release applies only to a purchase held for a window.";
        if (mandate.ExpiresAt <= window)
            return "The automatic-release permission must outlast the hold window.";
        if (!UnsignedIntegerText.TryParse(mandate.MaximumNetworkFeeBaseUnits, out _)
            || !UnsignedIntegerText.TryParse(mandate.MaximumLockedBalanceBaseUnits, out _)
            || !UnsignedIntegerText.TryParse(mandate.MaximumNonrefundableCostBaseUnits, out _))
            return "Automatic-release cost caps must be whole base-unit counts.";
        return !request.PreviewOnly && request.ReleaseAcceptance is null
            ? "Automatic release needs explicit acceptance of the previewed release disclosure."
            : null;
    }

    private static bool IsValidPartition(string? partition)
    {
        if (string.IsNullOrWhiteSpace(partition))
            return false;
        var trimmed = partition.Trim();
        return trimmed.Length <= MaxPartitionLength
            && trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool TryParseAmount(string? raw, out BigInteger amount, out string error)
    {
        amount = BigInteger.Zero;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Both sides of an exchange must carry an amount.";
            return false;
        }
        if (!BigInteger.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out amount))
        {
            error = "An exchange amount must be a whole, non-negative number of base units.";
            return false;
        }
        if (amount <= BigInteger.Zero)
        {
            error = "An exchange amount must be positive.";
            return false;
        }
        return true;
    }

    private static string Format(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);

    private static AZOAResult<T> RailRefusal<T>(string? railMessage)
    {
        var mapped = ConsumerSafeErrors.Map(railMessage);
        return AZOAResult<T>.FailureWithCode(mapped.Message, mapped.Code);
    }

    private static AZOAResult<ExchangeReceipt> Invalid(string message)
        => AZOAResult<ExchangeReceipt>.FailureWithCode(message, AzoaErrorCodes.InvalidRequest);

    private static AZOAResult<ExchangeReceipt> NotAllowed(string message)
        => AZOAResult<ExchangeReceipt>.FailureWithCode(message, AzoaErrorCodes.OperationNotAllowed);

    private static AZOAResult<ExchangeReceipt> NotFound()
        => AZOAResult<ExchangeReceipt>.FailureWithCode("Exchange receipt not found.", AzoaErrorCodes.NotFound);

    private static AZOAResult<ExchangeReceipt> Unavailable()
        => AZOAResult<ExchangeReceipt>.FailureWithCode(UnavailableMessage, AzoaErrorCodes.DependencyUnavailable);

    private sealed record ResolvedAccounts(IWallet Wallet, HoldAssetIdentity Asset, string TreasuryAddress,
        PlatformReserveLedger Reserve, ProjectTreasuryReference Treasury);

    /// <summary>Public-safe receipt correlation and the buy-back queue anchor, domain-separated from allocation keys.</summary>
    internal static class ExchangeIdempotency
    {
        private const string ReceiptCorrelationDomain = "azoa:exchange-receipt:v1:";

        public static ExchangeIdempotencyValue CreateFromClientKey(Guid apiKeyId, string clientIdempotencyKey)
        {
            if (apiKeyId == Guid.Empty)
                throw new ArgumentException("API key id is required.", nameof(apiKeyId));
            if (string.IsNullOrWhiteSpace(clientIdempotencyKey))
                throw new ArgumentException("Idempotency key is required.", nameof(clientIdempotencyKey));
            var ledgerKey = $"exchange:{apiKeyId}:{clientIdempotencyKey.Trim()}";
            var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ReceiptCorrelationDomain + ledgerKey));
            return new ExchangeIdempotencyValue(ledgerKey, Convert.ToHexString(digest).ToLowerInvariant());
        }
    }

    /// <summary>Internal key plus the public-safe correlation.</summary>
    internal sealed record ExchangeIdempotencyValue(string LedgerKey, string Correlation);
}

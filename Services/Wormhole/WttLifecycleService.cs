using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Authenticated WTT offers, admission and participant commands; see AGENTS.md §Operation journal.</summary>
public sealed partial class WttLifecycleService(IWttOperationStore store, IWalletStore wallets,
    SigningAdmissionAuthorityFactory authorities, WttModuleResolver modules, IWttPreparationStore preparations,
    WttRecoveryService recovery, IKeyCustodyService custody, TimeProvider clock)
{
    private static readonly TimeSpan MaximumOfferLifetime = TimeSpan.FromMinutes(5);

    public async Task<AZOAResult<WttOfferView>> QuoteAsync(SigningCredentialWitness credential, WttQuoteRequest request,
        CancellationToken ct = default)
    {
        if (request is null || credential.ActorUserId == Guid.Empty || credential.TenantId == Guid.Empty
            || !WttOperationIdentity.ValidRequestKey(request.RequestKey) || request.Source is null || request.Target is null
            || request.Source.WalletId == Guid.Empty || request.Target.WalletId == Guid.Empty
            || !WttModuleResolver.TryParseNetwork(request.Source.Network, out var sourceNetwork)
            || !WttModuleResolver.TryParseNetwork(request.Target.Network, out var targetNetwork)
            || string.Equals(request.Source.ChainType, request.Target.ChainType, StringComparison.OrdinalIgnoreCase)
            || !ValidBudget(request.SourceBudget) || !ValidBudget(request.TargetBudget)
            || !UnsignedIntegerText.TryParse(request.Amount, out var amount) || amount <= 0
            || request.RecipientAddress is not { Length: > 0 and <= 128 } || request.TokenAddress is not { Length: <= 128 }
            || request.ReturnOfOperationId is { } returnOf && !WttOperationIdentity.IsCanonicalId(returnOf))
            return Invalid<WttOfferView>("A complete WTT request with a stable request key, two distinct chains and explicit budgets is required.");
        if (request.RelayerFee is not (null or "0"))
            return Invalid<WttOfferView>("Relayer fees are refused: the platform redeems its own transfers, so a fee would only pay a front-runner.");
        var source = modules.Resolve<IWttSourceModule>(request.Source.ChainType, sourceNetwork);
        var target = modules.Resolve<IWttTargetModule>(request.Target.ChainType, targetNetwork);
        if (source is null || target is null)
            return Unavailable<WttOfferView>("No enabled reviewed WTT module serves this exact source and target network.");
        var requestKeyHash = WttOperationIdentity.RequestKeyHash(request.RequestKey);
        var operationId = WttOperationIdentity.Operation(credential.ActorUserId, credential.TenantId, requestKeyHash);
        if (await store.GetAsync(operationId, ct) is not null)
            return Conflict<WttOfferView>("This request key already belongs to an admitted or cancelled WTT operation.");
        var sourceWallet = await OwnedWalletAsync(credential, request.Source.WalletId, source.ChainType, ct);
        var targetWallet = await OwnedWalletAsync(credential, request.Target.WalletId, target.ChainType, ct);
        if (sourceWallet is null || targetWallet is null)
            return Forbidden<WttOfferView>("Both WTT wallets must be owned by the authenticated participant on their exact chains.");
        var custodialRecipient = await OwnCustodialWalletAsync(credential.ActorUserId, target.ChainType, request.RecipientAddress, ct);
        if (credential.TenantId is not null && !custodialRecipient)
            return Forbidden<WttOfferView>("A tenant may deliver only to one of the user's own custodial wallets on the destination chain.");
        var sourceAuthority = await authorities.BindDirectAsync(credential, sourceWallet.Value.Id, source.ChainType, sourceWallet.Value.Address,
            [AzoaScopes.WttSourceSign], ct);
        var targetAuthority = await authorities.BindDirectAsync(credential, targetWallet.Value.Id, target.ChainType, targetWallet.Value.Address,
            [AzoaScopes.WttTargetSign], ct);
        if (sourceAuthority.IsError || targetAuthority.IsError)
            return Forbidden<WttOfferView>(sourceAuthority.IsError ? sourceAuthority.Message : targetAuthority.Message);
        var inspected = await source.InspectWttSourceAsync(request.TokenAddress ?? "", request.NativeAsset, ct);
        if (inspected.IsError) return Invalid<WttOfferView>(inspected.Message);
        var (sourceDescriptor, asset) = inspected.Result;
        if (sourceDescriptor is null || asset is null || sourceDescriptor.ChainType != source.ChainType || sourceDescriptor.Network != sourceNetwork)
            return Invalid<WttOfferView>("The source provider returned another deployment.");
        WttOperation? original = null;
        if (request.ReturnOfOperationId is { } returnOperationId)
        {
            original = await store.GetAsync(returnOperationId, ct);
            if (original is null || !InScope(original, credential, false) || original.State != "Settled")
                return Invalid<WttOfferView>("A return requires the participant's own settled WTT operation.");
            var originalIntent = JsonSerializer.Deserialize<WttOperationIntent>(original.IntentJson)!;
            if (originalIntent.Target.ProtocolIdentity != sourceDescriptor.ProtocolIdentity
                || originalIntent.Asset.OriginChain != asset.OriginChain || originalIntent.Asset.OriginAddress != asset.OriginAddress
                || originalIntent.Source.ChainType != target.ChainType)
                return Invalid<WttOfferView>("A return must move the settled representation of the same origin asset back toward its source chain.");
        }
        var inspection = await target.InspectWttTargetAsync(asset, request.RecipientAddress, request.UnwrapNative, ct);
        if (inspection.IsError || inspection.Result is null) return Invalid<WttOfferView>(inspection.Message);
        var targetView = inspection.Result;
        if (targetView.Network.ChainType != target.ChainType || targetView.Network.Network != targetNetwork)
            return Invalid<WttOfferView>("The target provider returned another deployment.");
        // Custody signs only stage-payer wire; a participant step (opt-in, native output) needs the recipient's own client signature.
        if (custodialRecipient && (request.UnwrapNative || !targetView.RecipientReady
                && !ChainAddressIdentity.Equivalent(target.ChainType, targetWallet.Value.Address, request.RecipientAddress)))
            return Invalid<WttOfferView>("A custodial recipient cannot sign the participant step this route needs; deliver to the target payer "
                + "without native output, or to a wallet that signs for itself.");
        var requiresAttestation = !targetView.Registered;
        if (requiresAttestation && asset.OriginChain != sourceDescriptor.ProtocolChain)
            return Invalid<WttOfferView>("The target lacks a registered representation; attest from the asset's origin chain first.");
        var rules = await source.ReadWttPublicationRulesAsync(ct);
        if (rules.IsError || rules.Result is null) return Unavailable<WttOfferView>(rules.Message);
        WttNormalizedAmount normalized;
        try { normalized = WttPayloadCodec.Normalize(request.Amount, "0", asset.Decimals); }
        catch (FormatException exception) { return Invalid<WttOfferView>(exception.Message); }
        var intent = new WttOperationIntent(1, operationId, credential.ActorUserId, credential.TenantId, sourceDescriptor, targetView.Network,
            asset, new(sourceWallet.Value.Address, Context(credential, sourceWallet.Value.Id, AzoaScopes.WttSourceSign, source.ChainType)),
            new(targetWallet.Value.Address, Context(credential, targetWallet.Value.Id, AzoaScopes.WttTargetSign, target.ChainType)),
            request.RecipientAddress, targetView.RecipientProtocolAddress, targetView.AssetAddress, targetView.Decimals, normalized,
            rules.Result.RequiresZeroNonce ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4)),
            rules.Result.ConsistencyLevel, request.UnwrapNative, requiresAttestation, request.SourceBudget, request.TargetBudget);
        var quoted = await source.QuoteWttSourceAsync(intent, ct);
        if (quoted.IsError || quoted.Result is null) return Invalid<WttOfferView>(quoted.Message);
        var quote = quoted.Result;
        var now = clock.GetUtcNow();
        var intentHash = CanonicalJson.Hash(intent);
        if (quote.IntentHash != intentHash || quote.Phase != "Source" || quote.ProtocolIdentity != sourceDescriptor.ProtocolIdentity
            || quote.ExpiresAt <= now || quote.ExpiresAt > now.Add(MaximumOfferLifetime) || quote.Stages is not { Count: > 0 })
            return Invalid<WttOfferView>("The source provider quote changed the frozen intent or its validity window.");
        var quoteJson = Canonical(quote);
        var inspectionJson = Canonical(targetView);
        var offer = new WttOffer
        {
            Id = Guid.NewGuid().ToString("N"), ActorUserId = credential.ActorUserId.ToString("N"), TenantId = Tenant(credential.TenantId),
            OperationId = operationId, RequestKeyHash = requestKeyHash, IntentJson = Canonical(intent), IntentHash = intentHash,
            SourceQuoteJson = quoteJson, TargetInspectionJson = inspectionJson, ReturnOfOperationId = request.ReturnOfOperationId,
            OfferHash = WttOperationIdentity.Offer(intentHash, CanonicalJson.HashCanonical(quoteJson), CanonicalJson.HashCanonical(inspectionJson),
                request.ReturnOfOperationId, quote.ExpiresAt),
            ExpiresAt = quote.ExpiresAt, CreatedAt = now,
        };
        var saved = await store.CreateOfferAsync(offer, ct);
        return saved is null ? Conflict<WttOfferView>("Offer persistence conflicted; request a new quote.")
            : AZOAResult<WttOfferView>.Success(OfferView(offer, intent, quote, targetView));
    }

    public async Task<AZOAResult<WttOperationView>> StartAsync(SigningCredentialWitness credential, WttStartRequest request,
        string? idempotencyKey, CancellationToken ct = default)
    {
        if (request is null || !request.AcceptEconomicEffects || !WttOperationIdentity.ValidRequestKey(idempotencyKey)
            || !Guid.TryParseExact(request.OfferId, "N", out _) || !WttOperationIdentity.IsCanonicalId(request.OfferHash))
            return Invalid<WttOperationView>("Explicit acceptance of the exact offer and its original Idempotency-Key are required.");
        var offer = await store.GetOfferAsync(request.OfferId, ct);
        if (offer is null || offer.ActorUserId != credential.ActorUserId.ToString("N") || offer.TenantId != Tenant(credential.TenantId)
            || offer.OfferHash != request.OfferHash || offer.RequestKeyHash != WttOperationIdentity.RequestKeyHash(idempotencyKey!))
            return Conflict<WttOperationView>("The offer does not belong to this participant and request key.");
        var candidate = Candidate(offer);
        var existing = await store.GetAsync(candidate.Id, ct);
        if (existing is not null)
            return existing.OfferId == offer.Id && existing.OfferHash == offer.OfferHash && InScope(existing, credential, false) && CanReadWtt(credential)
                ? AZOAResult<WttOperationView>.Success(await ProjectAsync(existing, ct))
                : Conflict<WttOperationView>("The request key already belongs to a different immutable WTT operation.");
        var intent = JsonSerializer.Deserialize<WttOperationIntent>(offer.IntentJson)!;
        var authority = await authorities.BindDirectAsync(credential, intent.SourceAuthority.Context.WalletId, intent.Source.ChainType,
            intent.SourceAuthority.Address, [AzoaScopes.WttSourceSign], ct);
        if (authority.IsError || authority.Result is null) return Forbidden<WttOperationView>(authority.Message);
        var saved = await store.AdmitAsync(candidate, new(AzoaScopes.WttSourceSign, authority.Result, null), ct);
        return saved is null ? Conflict<WttOperationView>("The offer expired, was consumed, or current source authority changed before admission.")
            : AZOAResult<WttOperationView>.Success(await ProjectAsync(saved, ct));
    }

    public async Task<AZOAResult<WttOperationView>> GetAsync(string operationId, SigningCredentialWitness credential,
        CancellationToken ct = default)
    {
        if (!CanReadWtt(credential)) return Forbidden<WttOperationView>(MissingReadScope);
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        return operation is null ? NotFound<WttOperationView>() : AZOAResult<WttOperationView>.Success(await ProjectAsync(operation, ct));
    }

    /// <summary>Operator projection without participant scoping; the transport enforces the operator policy.</summary>
    public async Task<WttOperationView?> InspectAsync(string operationId, CancellationToken ct = default)
    {
        if (!WttOperationIdentity.IsCanonicalId(operationId)) return null;
        var operation = await store.GetAsync(operationId, ct);
        return operation is null ? null : await ProjectAsync(operation, ct);
    }

    public async Task<AZOAResult<WttOperationView>> LookupAsync(string? requestKey, SigningCredentialWitness credential,
        CancellationToken ct = default)
    {
        if (!WttOperationIdentity.ValidRequestKey(requestKey)) return Invalid<WttOperationView>("A request key is required.");
        var id = WttOperationIdentity.Operation(credential.ActorUserId, credential.TenantId, WttOperationIdentity.RequestKeyHash(requestKey!));
        return await GetAsync(id, credential, ct);
    }

    public async Task<AZOAResult<WttOperationPage>> ListAsync(SigningCredentialWitness credential, string? cursor, int limit,
        CancellationToken ct = default)
    {
        if (!CanReadWtt(credential)) return Forbidden<WttOperationPage>(MissingReadScope);
        DateTimeOffset? before = null;
        if (cursor is not null)
        {
            if (!long.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
                || ticks <= 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
                return Invalid<WttOperationPage>("Invalid cursor.");
            before = new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        var size = Math.Clamp(limit, 1, 50);
        var rows = await store.ListAsync(credential.ActorUserId.ToString("N"), Tenant(credential.TenantId), before, size, ct);
        var items = new List<WttOperationView>(rows.Count);
        foreach (var row in rows) items.Add(await ProjectAsync(row, ct));
        var next = rows.Count == size ? rows[^1].CreatedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return AZOAResult<WttOperationPage>.Success(new(items, next));
    }

    public async Task<AZOAResult<WttOperationView>> CancelAsync(SigningCredentialWitness credential, WttCancelRequest request,
        string? idempotencyKey, CancellationToken ct = default)
    {
        if (request is null || !WttOperationIdentity.ValidRequestKey(idempotencyKey) || !Guid.TryParseExact(request.OfferId, "N", out _))
            return Invalid<WttOperationView>("Cancellation requires the original request key and offer.");
        var offer = await store.GetOfferAsync(request.OfferId, ct);
        if (offer is null || offer.ActorUserId != credential.ActorUserId.ToString("N") || offer.OfferHash != request.OfferHash
            || offer.TenantId != Tenant(credential.TenantId) && credential.TenantId is not null
            || offer.RequestKeyHash != WttOperationIdentity.RequestKeyHash(idempotencyKey!))
            return Conflict<WttOperationView>("The offer does not belong to this participant and request key.");
        var candidate = Candidate(offer);
        var intent = JsonSerializer.Deserialize<WttOperationIntent>(offer.IntentJson)!;
        var existing = await store.GetAsync(candidate.Id, ct);
        long? probedVersion = null;
        if (existing is not null && existing.State != "Cancelled"
            && modules.Resolve<IWttNonceReservationModule>(intent.Source.ChainType, intent.Source.Network) is { } nonces)
        {
            if (!await NoncesFreeAsync(intent, await store.ReadStagesAsync(candidate.Id, ct), nonces, ct))
                return Conflict<WttOperationView>("A source stage owns a reserved or signed account nonce; continue or let it resolve before cancelling.");
            probedVersion = existing.Version;
        }
        var consent = await authorities.BindConsentAsync(credential, [AzoaScopes.WttSourceSign], ct);
        if (consent.IsError || consent.Result is null) return Forbidden<WttOperationView>(consent.Message);
        var saved = await store.CancelAsync(candidate, new(AzoaScopes.WttSourceSign, null, consent.Result), probedVersion, ct);
        if (saved is null)
            return Conflict<WttOperationView>("The operation has a sent, exposed, published or in-flight effect and cannot be cancelled.");
        // A replayed cancellation re-seals, so a crash between the journal transition and the seal is repaired idempotently.
        await SealUnsentAsync(intent, await store.ReadStagesAsync(candidate.Id, ct), ct);
        return AZOAResult<WttOperationView>.Success(await ProjectAsync(saved, ct));
    }

    private WttOperation Candidate(WttOffer offer)
    {
        var now = clock.GetUtcNow();
        return new WttOperation
        {
            Id = offer.OperationId, ActorUserId = offer.ActorUserId, TenantId = offer.TenantId, RequestKeyHash = offer.RequestKeyHash,
            OfferId = offer.Id, OfferHash = offer.OfferHash, IntentJson = offer.IntentJson, IntentHash = offer.IntentHash,
            SourceQuoteJson = offer.SourceQuoteJson, ReturnOfOperationId = offer.ReturnOfOperationId,
            CreatedAt = now, UpdatedAt = now, NextAttemptAt = now,
        };
    }

    private async Task<WttOperation?> ReadOwnedAsync(string operationId, SigningCredentialWitness credential, bool directOwner,
        CancellationToken ct)
    {
        if (!WttOperationIdentity.IsCanonicalId(operationId)) return null;
        var operation = await store.GetAsync(operationId, ct);
        return operation is not null && InScope(operation, credential, directOwner) ? operation : null;
    }

    private async Task<(Guid Id, string Address)?> OwnedWalletAsync(SigningCredentialWitness credential, Guid walletId, string chain,
        CancellationToken ct)
    {
        var wallet = await wallets.GetByIdAsync(walletId, ct);
        return wallet.IsError || wallet.Result is null || wallet.Result.UserId != credential.ActorUserId
            || !string.Equals(wallet.Result.ChainType, chain, StringComparison.OrdinalIgnoreCase)
            ? null : (walletId, wallet.Result.Address);
    }

    /// <summary>Whether the address is one of the user's wallets whose key custody actually holds; the wallet type alone proves nothing.</summary>
    private async Task<bool> OwnCustodialWalletAsync(Guid actor, string chain, string address, CancellationToken ct)
    {
        var owned = await wallets.GetByUserAsync(actor, ct);
        if (owned.IsError || owned.Result is null) return false;
        foreach (var wallet in owned.Result.Where(candidate => candidate.UserId == actor && candidate.WalletType == WalletType.Platform
            && string.Equals(candidate.ChainType, chain, StringComparison.OrdinalIgnoreCase)
            && ChainAddressIdentity.Equivalent(chain, candidate.Address, address)))
            if (await custody.CanSignAsync(wallet.Id, actor) is { IsError: false, Result: true }) return true;
        return false;
    }

    /// <summary>True when no source stage that could hold a durable nonce does; Preparing and Prepared always may.</summary>
    private static async Task<bool> NoncesFreeAsync(WttOperationIntent intent, IReadOnlyList<WttOperationStage> stages,
        IWttNonceReservationModule nonces, CancellationToken ct)
    {
        foreach (var stage in stages.Where(candidate => candidate.Phase == "Source"
            && candidate.State is "Planned" or "Preparing" or "Prepared" or "Retired"))
            if (stage.State is "Preparing" or "Prepared"
                || await nonces.ReadWttNonceHoldAsync(intent, "Source", stage.Stage, stage.Generation, ct) != WttNonceHold.None)
                return false;
        return true;
    }

    private static SigningContext Context(SigningCredentialWitness credential, Guid wallet, string scope, string chain)
        => new(credential.ActorUserId, wallet, false, credential.TenantId ?? Guid.Empty,
            credential.TenantId is null ? Guid.Empty : credential.ActorUserId, scope, chain);

    private static bool InScope(WttOperation row, SigningCredentialWitness credential, bool directOwner)
        => credential.ActorUserId != Guid.Empty && credential.TenantId != Guid.Empty && row.ActorUserId == credential.ActorUserId.ToString("N")
            && (row.TenantId == Tenant(credential.TenantId) || directOwner && credential.TenantId is null);

    private const string MissingReadScope = "Reading WTT operations requires a WTT scope on the credential.";
    private static readonly string[] WttScopes = [AzoaScopes.WttSourceSign, AzoaScopes.WttTargetSign, AzoaScopes.WttParticipantSign,
        AzoaScopes.WttCleanupSign];

    private static bool CanReadWtt(SigningCredentialWitness credential)
        => credential.OwnerAuthority || credential.Scopes.Any(scope => WttScopes.Contains(scope, StringComparer.Ordinal));

    private static bool ValidBudget(WttChainBudget? budget)
        => budget is not null && UnsignedIntegerText.TryParse(budget.MaximumNetworkFee, out _)
            && UnsignedIntegerText.TryParse(budget.MaximumLockedDeposit, out _) && UnsignedIntegerText.TryParse(budget.MaximumValue, out _);

    private static string Tenant(Guid? tenant) => tenant?.ToString("N") ?? "";
    private static string Canonical(object value) => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(value));

    private static AZOAResult<T> Invalid<T>(string message) => AZOAResult<T>.FailureWithCode(message, AzoaErrorCodes.InvalidRequest);
    private static AZOAResult<T> Conflict<T>(string message) => AZOAResult<T>.FailureWithCode(message, AzoaErrorCodes.Conflict);
    private static AZOAResult<T> Forbidden<T>(string message) => AZOAResult<T>.FailureWithCode(message, AzoaErrorCodes.Forbidden);
    private static AZOAResult<T> Unavailable<T>(string message) => AZOAResult<T>.FailureWithCode(message, AzoaErrorCodes.DependencyUnavailable);
    private static AZOAResult<T> NotFound<T>() => AZOAResult<T>.FailureWithCode("WTT operation not found in the authenticated participant scope.",
        AzoaErrorCodes.NotFound);
    private static AZOAResult<T> Changed<T>() => Conflict<T>("The operation, stage, version or participant scope changed; reload its current state.");
}

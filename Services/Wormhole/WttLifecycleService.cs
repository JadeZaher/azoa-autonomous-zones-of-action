using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Authenticated WTT offers, admission and participant commands; see AGENTS.md §Operation journal.</summary>
public sealed partial class WttLifecycleService(IWttOperationStore store, IWalletStore wallets,
    SigningAdmissionAuthorityFactory authorities, WttModuleResolver modules, IWttPreparationStore preparations,
    WttRecoveryService recovery, IKeyCustodyService custody, TimeProvider clock,
    WttStageKeyReadinessState? stageKeys = null, IWttRecipientDirectory? recipients = null, IWalletOwnershipProof? ownership = null)
{
    private static readonly TimeSpan MaximumOfferLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Request keys in this namespace belong to workflow nodes; direct callers cannot claim them.</summary>
    internal const string ActionRequestKeyPrefix = "action:";

    /// <summary>The signing principal: a direct credential, or a frozen workflow node witness over that credential.</summary>
    private sealed record WttPrincipal(SigningCredentialWitness Credential, WorkflowSigningWitness? Workflow)
    {
        public WttActionBinding? Action => Workflow is null ? null
            : new(Workflow.Action.RunId.ToString("N"), Workflow.Action.NodeId.ToString("N"), Workflow.Action.IntentHash);
    }

    private enum RecipientKind { None, Custodial, ProvenExternal }

    public Task<AZOAResult<WttOfferView>> QuoteAsync(SigningCredentialWitness credential, WttQuoteRequest request,
        CancellationToken ct = default)
        => QuoteCoreAsync(new(credential, null), request, ct);

    /// <summary>Workflow quote under the frozen node witness; the node identity fixes the request key.</summary>
    internal Task<AZOAResult<WttOfferView>> QuoteForWorkflowAsync(WorkflowSigningWitness witness, WttQuoteRequest request,
        CancellationToken ct = default)
        => QuoteCoreAsync(new(witness.Credential, witness), request, ct);

    private async Task<AZOAResult<WttOfferView>> QuoteCoreAsync(WttPrincipal principal, WttQuoteRequest request, CancellationToken ct)
    {
        var credential = principal.Credential;
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
        if (!RequestKeyFits(principal, request.RequestKey))
            return Invalid<WttOfferView>("Workflow request keys are reserved for their own action node.");
        if (request.RelayerFee is not (null or "0"))
            return Invalid<WttOfferView>("Relayer fees are refused: the platform redeems its own transfers, so a fee would only pay a front-runner.");
        if (principal.Workflow is { } witness && !WorkflowScopesSelected(witness))
            return Forbidden<WttOfferView>("A workflow transfer requires a node witness that selected both WTT payer scopes.");
        var source = modules.Resolve<IWttSourceModule>(request.Source.ChainType, sourceNetwork);
        var target = modules.Resolve<IWttTargetModule>(request.Target.ChainType, targetNetwork);
        if (source is null || target is null)
            return Unavailable<WttOfferView>("No enabled reviewed WTT module serves this exact source and target network.");
        if (StageKeysDegraded(source.ChainType, sourceNetwork, target.ChainType, targetNetwork) is { } degraded)
            return Unavailable<WttOfferView>(degraded);
        var requestKeyHash = WttOperationIdentity.RequestKeyHash(request.RequestKey);
        var operationId = WttOperationIdentity.Operation(credential.ActorUserId, credential.TenantId, requestKeyHash);
        if (await store.GetAsync(operationId, ct) is not null)
            return Conflict<WttOfferView>("This request key already belongs to an admitted or cancelled WTT operation.");
        var sourceWallet = await OwnedWalletAsync(credential, request.Source.WalletId, source.ChainType, ct);
        var targetWallet = await OwnedWalletAsync(credential, request.Target.WalletId, target.ChainType, ct);
        if (sourceWallet is null || targetWallet is null)
            return Forbidden<WttOfferView>("Both WTT wallets must be owned by the authenticated participant on their exact chains.");
        var recipient = await OwnRecipientAsync(credential.ActorUserId, target.ChainType, request.RecipientAddress, ct);
        if (credential.TenantId is not null && recipient == RecipientKind.None)
            return Forbidden<WttOfferView>("A tenant may deliver only to one of the user's own custodial or signature-proven wallets on the destination chain.");
        var sourceAuthority = await BindSigningAsync(principal, sourceWallet.Value.Id, source.ChainType, sourceWallet.Value.Address,
            AzoaScopes.WttSourceSign, ct);
        var targetAuthority = await BindSigningAsync(principal, targetWallet.Value.Id, target.ChainType, targetWallet.Value.Address,
            AzoaScopes.WttTargetSign, ct);
        if (sourceAuthority.IsError || targetAuthority.IsError)
            return Forbidden<WttOfferView>(sourceAuthority.IsError ? sourceAuthority.Message : targetAuthority.Message);
        var inspected = await source.InspectWttSourceAsync(request.TokenAddress ?? "", request.NativeAsset, ct);
        if (inspected.IsError) return Invalid<WttOfferView>(inspected.Message);
        var (sourceDescriptor, asset) = inspected.Result;
        if (sourceDescriptor is null || asset is null || sourceDescriptor.ChainType != source.ChainType || sourceDescriptor.Network != sourceNetwork)
            return Invalid<WttOfferView>("The source provider returned another deployment.");
        if (request.ReturnOfOperationId is { } returnOperationId)
        {
            var original = await store.GetAsync(returnOperationId, ct);
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
        // A participant step (opt-in, native output) is always the recipient's own client signature; see AGENTS.md §Recipient rule.
        var participantStep = request.UnwrapNative || !targetView.RecipientReady
            && !ChainAddressIdentity.Equivalent(target.ChainType, targetWallet.Value.Address, request.RecipientAddress);
        if (participantStep && recipient == RecipientKind.Custodial)
            return Invalid<WttOfferView>("A custodial recipient cannot sign the participant step this route needs; deliver to the target payer "
                + "without native output, or to a wallet that signs for itself.");
        if (participantStep && (credential.TenantId is not null || principal.Workflow is not null))
            return Invalid<WttOfferView>("Tenant and workflow transfers cannot carry a recipient participant step; deliver to a recipient "
                + "that is already ready, or start the transfer with the owner's direct credential.");
        if (participantStep && recipients is not null
            && await recipients.IsAnotherUsersCustodialWalletAsync(credential.ActorUserId, target.ChainType, request.RecipientAddress, ct))
            return Invalid<WttOfferView>("The recipient is another user's custodial wallet, which can never sign this route's participant step.");
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

    public Task<AZOAResult<WttOperationView>> StartAsync(SigningCredentialWitness credential, WttStartRequest request,
        string? idempotencyKey, CancellationToken ct = default)
        => StartCoreAsync(new(credential, null), request, idempotencyKey, ct);

    /// <summary>Workflow admission bound to the frozen node; every later first send re-proves that node is still admitted.</summary>
    internal Task<AZOAResult<WttOperationView>> StartForWorkflowAsync(WorkflowSigningWitness witness, WttStartRequest request,
        string requestKey, CancellationToken ct = default)
        => StartCoreAsync(new(witness.Credential, witness), request, requestKey, ct);

    private async Task<AZOAResult<WttOperationView>> StartCoreAsync(WttPrincipal principal, WttStartRequest request,
        string? idempotencyKey, CancellationToken ct)
    {
        var credential = principal.Credential;
        if (request is null || !request.AcceptEconomicEffects || !WttOperationIdentity.ValidRequestKey(idempotencyKey)
            || !Guid.TryParseExact(request.OfferId, "N", out _) || !WttOperationIdentity.IsCanonicalId(request.OfferHash))
            return Invalid<WttOperationView>("Explicit acceptance of the exact offer and its original Idempotency-Key are required.");
        if (!RequestKeyFits(principal, idempotencyKey!))
            return Invalid<WttOperationView>("Workflow request keys are reserved for their own action node.");
        var offer = await store.GetOfferAsync(request.OfferId, ct);
        if (offer is null || offer.ActorUserId != credential.ActorUserId.ToString("N") || offer.TenantId != Tenant(credential.TenantId)
            || offer.OfferHash != request.OfferHash || offer.RequestKeyHash != WttOperationIdentity.RequestKeyHash(idempotencyKey!))
            return Conflict<WttOperationView>("The offer does not belong to this participant and request key.");
        var candidate = Candidate(offer, principal.Action);
        var existing = await store.GetAsync(candidate.Id, ct);
        if (existing is not null)
            return existing.OfferId == offer.Id && existing.OfferHash == offer.OfferHash && InScope(existing, credential, false)
                && ActionOf(existing) == principal.Action && (principal.Workflow is not null || CanReadWtt(credential))
                ? AZOAResult<WttOperationView>.Success(await ProjectAsync(existing, ct))
                : Conflict<WttOperationView>("The request key already belongs to a different immutable WTT operation.");
        var intent = JsonSerializer.Deserialize<WttOperationIntent>(offer.IntentJson)!;
        if (StageKeysDegraded(intent.Source.ChainType, intent.Source.Network, intent.Target.ChainType, intent.Target.Network) is { } degraded)
            return Unavailable<WttOperationView>(degraded);
        var authority = await BindSigningAsync(principal, intent.SourceAuthority.Context.WalletId, intent.Source.ChainType,
            intent.SourceAuthority.Address, AzoaScopes.WttSourceSign, ct);
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

    /// <summary>Workflow projection of the node's own operation; the caller proved the frozen node identity.</summary>
    internal async Task<WttOperationView?> ProjectOwnedAsync(string operationId, CancellationToken ct = default)
        => await store.GetAsync(operationId, ct) is { } operation ? await ProjectAsync(operation, ct) : null;

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
        if (idempotencyKey!.StartsWith(ActionRequestKeyPrefix, StringComparison.Ordinal))
            return Invalid<WttOperationView>("A workflow transfer resolves through its action node, not direct cancellation.");
        var offer = await store.GetOfferAsync(request.OfferId, ct);
        if (offer is null || offer.ActorUserId != credential.ActorUserId.ToString("N") || offer.OfferHash != request.OfferHash
            || offer.TenantId != Tenant(credential.TenantId) && credential.TenantId is not null
            || offer.RequestKeyHash != WttOperationIdentity.RequestKeyHash(idempotencyKey))
            return Conflict<WttOperationView>("The offer does not belong to this participant and request key.");
        var candidate = Candidate(offer, null);
        var intent = JsonSerializer.Deserialize<WttOperationIntent>(offer.IntentJson)!;
        var existing = await store.GetAsync(candidate.Id, ct);
        long? probedVersion = null;
        var nonces = modules.Resolve<IWttNonceReservationModule>(intent.Source.ChainType, intent.Source.Network);
        if (existing is not null && existing.State != "Cancelled" && nonces is not null)
        {
            if (!await NoncesFreeAsync(intent, await store.ReadStagesAsync(candidate.Id, ct), nonces, ct))
                return Conflict<WttOperationView>("A source stage owns a reserved or signed account nonce; continue or let it resolve before cancelling.");
            probedVersion = existing.Version;
        }
        var consent = await authorities.BindConsentAsync(credential, [AzoaScopes.WttSourceSign], ct);
        if (consent.IsError || consent.Result is null) return Forbidden<WttOperationView>(consent.Message);
        var saved = await store.CancelAsync(candidate, new(AzoaScopes.WttSourceSign, null, consent.Result), probedVersion, nonces is not null, ct);
        if (saved is null)
            return Conflict<WttOperationView>("The operation has a sent, exposed, published or in-flight effect and cannot be cancelled.");
        // A replayed cancellation re-seals, so a crash between the journal transition and the seal is repaired idempotently.
        await SealUnsentAsync(saved, await store.ReadStagesAsync(candidate.Id, ct), ct);
        return AZOAResult<WttOperationView>.Success(await ProjectAsync(saved, ct));
    }

    /// <summary>Direct owner continues a tenant-started target phase after the tenant's authority ends; see AGENTS.md §Operation exits.</summary>
    public async Task<AZOAResult<WttOperationView>> TakeOverAsync(string operationId, WttOwnerTakeoverCommand command,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (command is null || command.ExpectedVersion < 0) return Changed<WttOperationView>();
        if (credential.TenantId is not null) return Forbidden<WttOperationView>("Owner takeover requires the owner's direct credential.");
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        if (operation is null) return NotFound<WttOperationView>();
        var owner = WttOwnerIntent.From(JsonSerializer.Deserialize<WttOperationIntent>(operation.IntentJson)!);
        // A replayed takeover returns the recorded owner journal instead of a conflict.
        if (operation.OwnerIntentJson is { } recorded)
            return recorded == Canonical(owner) ? AZOAResult<WttOperationView>.Success(await ProjectAsync(operation, ct))
                : Conflict<WttOperationView>("The recorded owner intent differs from this operation's admitted intent.");
        if (operation.TenantId.Length == 0 || operation.ActionRunId is not null)
            return Conflict<WttOperationView>("Only a tenant-started, non-workflow operation can be taken over by its owner.");
        if (operation.Version != command.ExpectedVersion) return Changed<WttOperationView>();
        if (operation.Phase != "Target" || operation.State != "Ready")
            return Conflict<WttOperationView>("Owner takeover applies once the verified proof awaits target authorization and before any target send.");
        var signing = await authorities.BindDirectAsync(credential, owner.TargetAuthority.Context.WalletId, owner.Target.ChainType,
            owner.TargetAuthority.Address, [AzoaScopes.WttTargetSign], ct);
        if (signing.IsError || signing.Result is null) return Forbidden<WttOperationView>(signing.Message);
        var saved = await store.TakeOverAsync(operationId, operation.Version, Canonical(owner),
            new(AzoaScopes.WttTargetSign, signing.Result, null), ct);
        return saved is null ? Conflict<WttOperationView>("A target stage was sent or disclosed, or the operation changed; reload its state.")
            : AZOAResult<WttOperationView>.Success(await ProjectAsync(saved, ct));
    }

    private WttOperation Candidate(WttOffer offer, WttActionBinding? action)
    {
        var now = clock.GetUtcNow();
        return new WttOperation
        {
            Id = offer.OperationId, ActorUserId = offer.ActorUserId, TenantId = offer.TenantId, RequestKeyHash = offer.RequestKeyHash,
            OfferId = offer.Id, OfferHash = offer.OfferHash, IntentJson = offer.IntentJson, IntentHash = offer.IntentHash,
            SourceQuoteJson = offer.SourceQuoteJson, ReturnOfOperationId = offer.ReturnOfOperationId,
            ActionRunId = action?.RunId, ActionNodeId = action?.NodeId, ActionIntentHash = action?.IntentHash,
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

    /// <summary>Whether the address is the user's own wallet: key custody actually held, or a signature-proven External wallet.</summary>
    private async Task<RecipientKind> OwnRecipientAsync(Guid actor, string chain, string address, CancellationToken ct)
    {
        var owned = await wallets.GetByUserAsync(actor, ct);
        if (owned.IsError || owned.Result is null) return RecipientKind.None;
        var proven = false;
        foreach (var wallet in owned.Result.Where(candidate => candidate.UserId == actor
            && string.Equals(candidate.ChainType, chain, StringComparison.OrdinalIgnoreCase)
            && ChainAddressIdentity.Equivalent(chain, candidate.Address, address)))
        {
            if (wallet.WalletType == WalletType.Platform && await custody.CanSignAsync(wallet.Id, actor) is { IsError: false, Result: true })
                return RecipientKind.Custodial;
            if (wallet.WalletType == WalletType.External && ownership is not null && await ownership.IsSignatureProvenAsync(wallet.Id, actor, ct))
                proven = true;
        }
        return proven ? RecipientKind.ProvenExternal : RecipientKind.None;
    }

    private Task<AZOAResult<SigningAdmissionAuthority>> BindSigningAsync(WttPrincipal principal, Guid walletId, string chain, string address,
        string scope, CancellationToken ct)
        => principal.Workflow is { } witness
            ? authorities.BindWorkflowAsync(witness, walletId, chain, address, ct)
            : authorities.BindDirectAsync(principal.Credential, walletId, chain, address, [scope], ct);

    /// <summary>Message when a route's stage-key module cannot admit new work because the keyring is degraded.</summary>
    private string? StageKeysDegraded(string sourceChain, ChainNetwork sourceNetwork, string targetChain, ChainNetwork targetNetwork)
    {
        if (stageKeys is null || stageKeys.Ready) return null;
        if (modules.Resolve<IWttStageKeyModule>(sourceChain, sourceNetwork) is null && modules.Resolve<IWttStageKeyModule>(targetChain, targetNetwork) is null)
            return null;
        return "WTT stage-key encryption is degraded, so new transfers on this route are refused: "
            + (stageKeys.Current?.Detail ?? "the keyring has not been checked yet.");
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

    private static bool RequestKeyFits(WttPrincipal principal, string requestKey)
        => principal.Workflow is { } witness
            ? requestKey == WorkflowRequestKey(witness.Action.RunId, witness.Action.NodeId)
            : !requestKey.StartsWith(ActionRequestKeyPrefix, StringComparison.Ordinal);

    /// <summary>The single request key a workflow node may use; it fixes the operation identity before any quote.</summary>
    internal static string WorkflowRequestKey(Guid run, Guid node) => $"{ActionRequestKeyPrefix}{run:N}:{node:N}:wtt";

    private static bool WorkflowScopesSelected(WorkflowSigningWitness witness)
        => witness.RequiredScopes.Order(StringComparer.Ordinal)
            .SequenceEqual(WttSigningAuthorityFence.WorkflowScopes.Append(AzoaScopes.ActionExecute).Order(StringComparer.Ordinal));

    private static WttActionBinding? ActionOf(WttOperation operation)
        => WttActionBinding.Of(operation.ActionRunId, operation.ActionNodeId, operation.ActionIntentHash);

    private static SigningContext Context(SigningCredentialWitness credential, Guid wallet, string scope, string chain)
        => new(credential.ActorUserId, wallet, false, credential.TenantId ?? Guid.Empty,
            credential.TenantId is null ? Guid.Empty : credential.ActorUserId, scope, chain);

    /// <summary>Tenant rows are in scope for their tenant; the direct owner reads them, and continues them after a takeover.</summary>
    private static bool InScope(WttOperation row, SigningCredentialWitness credential, bool directOwner)
        => credential.ActorUserId != Guid.Empty && credential.TenantId != Guid.Empty && row.ActorUserId == credential.ActorUserId.ToString("N")
            && (row.TenantId == Tenant(credential.TenantId) || (directOwner || row.OwnerIntentJson is not null) && credential.TenantId is null);

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

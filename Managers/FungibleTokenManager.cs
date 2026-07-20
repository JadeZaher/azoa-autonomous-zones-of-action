// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Composes the participant readiness gate, wallet provisioning, and the Algorand ASA
/// capability module into one idempotent, readiness-gated, tenant-callable seam for
/// launching a FUNGIBLE token (real supply + decimals) — the parallel to the
/// supply-1 mint path that <see cref="AllocationManager"/> drives (see
/// <see cref="IFungibleTokenManager"/>). Holds no payment-provider secret and runs
/// no economics — the tenant decides the total supply and decimals; AZOA
/// materialises the custodial wallet and creates the on-chain asset exactly once.
/// </summary>
public sealed class FungibleTokenManager : IFungibleTokenManager
{
    private const string OperationType = "fungible_token_create";
    private const string UnexpectedFailureMessage = "Fungible token creation failed unexpectedly.";

    /// <summary>Algorand ASA decimals are constrained to 0..19 by the protocol.</summary>
    private const int MaxDecimals = 19;

    private readonly IValueAccessService _valueAccess;
    private readonly IWalletManager _walletManager;
    private readonly IWalletStore _walletStore;
    private readonly IBlockchainProviderFactory _providerFactory;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly INodeGovernanceGuard _nodeGovernance;

    public FungibleTokenManager(
        IValueAccessService valueAccess,
        IWalletManager walletManager,
        IWalletStore walletStore,
        IBlockchainProviderFactory providerFactory,
        IIdempotencyStore idempotencyStore,
        INodeGovernanceGuard? nodeGovernance = null)
    {
        _valueAccess = valueAccess ?? throw new ArgumentNullException(nameof(valueAccess));
        _walletManager = walletManager ?? throw new ArgumentNullException(nameof(walletManager));
        _walletStore = walletStore ?? throw new ArgumentNullException(nameof(walletStore));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _nodeGovernance = nodeGovernance ?? NodeGovernanceGuard.Unrestricted;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FungibleTokenResult>> CreateAsync(
        Guid avatarId,
        FungibleTokenCreateRequest request,
        Guid callerAvatarId,
        string? clientIdempotencyKey,
        string apiKeyId,
        Guid? actingTenantId = null)
    {
        if (request is null)
            return AZOAResult<FungibleTokenResult>.Failure("Fungible token request is required.");
        if (string.IsNullOrWhiteSpace(request.ChainType))
            return AZOAResult<FungibleTokenResult>.Failure("ChainType is required.");
        if (string.IsNullOrWhiteSpace(request.Name))
            return AZOAResult<FungibleTokenResult>.Failure("Token name is required.");
        if (string.IsNullOrWhiteSpace(request.UnitName))
            return AZOAResult<FungibleTokenResult>.Failure("Token unit name is required.");
        if (string.IsNullOrWhiteSpace(apiKeyId))
            return AZOAResult<FungibleTokenResult>.Failure("Caller API key context is required.");

        var governance = await _nodeGovernance.EnsureAllowedAsync(
            request.ChainType,
            NodeGovernanceAssetTypes.FungibleToken,
            "fungible-token:create");
        if (governance.IsError)
            return AZOAResult<FungibleTokenResult>.Failure(governance.Message);

        // ── Step 1: idempotency key ───────────────────────────────────────────
        // Client key wins; absent ⇒ deterministic content key over the token
        // descriptor. NEVER a random per-request key. The whole key is partitioned
        // by apiKeyId so two tenants reusing the same human-friendly key cannot
        // collide.
        var idempotencyKey = BuildIdempotencyKey(apiKeyId, avatarId, request, clientIdempotencyKey);

        // ── Step 2: idempotency claim ─────────────────────────────────────────
        // TryClaim BEFORE any irreversible effect. On a lost claim we replay the
        // stored original result and create NO second token.
        var claim = await _idempotencyStore.TryClaimAsync(idempotencyKey, OperationType, CancellationToken.None);
        if (!claim.Won)
            return ReplayFromRecord(claim.Record, idempotencyKey);

        var effectMayHaveStarted = false;
        try
        {
            // ── Step 3: KYC gate (fail-closed) ────────────────────────────────
            // A rejected avatar produces NO side effect at all under a won claim.
            var gate = await _valueAccess.RequireValueAccessAsync(avatarId, actingTenantId);
            if (gate.IsError)
            {
                await _idempotencyStore.FailAsync(idempotencyKey, gate.Message, CancellationToken.None);
                return AZOAResult<FungibleTokenResult>.Failure(gate.Message);
            }

            // ── Step 4: validate supply + decimals — reject BEFORE any broadcast.
            // A bad total/decimals fails the idempotency key here so the claim is
            // terminal (Failed), never leaked InProgress, and nothing is broadcast.
            if (!TryValidateSupply(request, out var supplyError))
            {
                await _idempotencyStore.FailAsync(idempotencyKey, supplyError, CancellationToken.None);
                return AZOAResult<FungibleTokenResult>.Failure(supplyError);
            }

            // ── Step 5: provision-if-absent ───────────────────────────────────
            var (wallet, provisioned, walletError) = await EnsureWalletAsync(avatarId, request.ChainType);
            if (wallet is null)
            {
                await _idempotencyStore.FailAsync(idempotencyKey, walletError, CancellationToken.None);
                return AZOAResult<FungibleTokenResult>.Failure(walletError);
            }

            // ── Step 6: resolve the Algorand ASA capability module ────────────
            // Devnet matches the bridge/reconciliation precedent. The custodial
            // wallet address is used for manager/reserve/freeze/clawback/wallet —
            // mechanism only; configurable roles are a follow-up.
            if (!TryGetAsaModule(request.ChainType, out var asa, out var moduleError))
            {
                await _idempotencyStore.FailAsync(idempotencyKey, moduleError, CancellationToken.None);
                return AZOAResult<FungibleTokenResult>.Failure(moduleError);
            }

            // ── Step 7: create the fungible ASA (the irreversible effect) ─────
            var total = checked((int)request.Total);
            // AC4b: a FungibleTokenCreate is platform-SIGNED but may be tenant-DRIVEN.
            // When an acting tenant is present, build a tenant-driven platform context
            // (grantor = the on-whose-behalf avatar, scope = token:create:sign) so the
            // custody seam runs the live consent check before key decrypt; otherwise a
            // plain platform context (no grant required).
            var signingContext = actingTenantId is { } tenant && tenant != Guid.Empty
                ? SigningContext.Platform.ActingAs(tenant, AzoaScopes.TokenCreateSign, avatarId)
                : SigningContext.Platform;
            effectMayHaveStarted = true;
            var asaResult = await asa!.CreateASAAsync(
                request.Name,
                request.UnitName,
                total,
                request.Decimals,
                managerAddress: wallet.Address,
                reserveAddress: wallet.Address,
                freezeAddress: wallet.Address,
                clawbackAddress: wallet.Address,
                walletAddress: wallet.Address,
                signingContext,
                CancellationToken.None);

            if (asaResult.IsError || string.IsNullOrWhiteSpace(asaResult.Result))
            {
                var msg = asaResult.IsError ? asaResult.Message : "Fungible token creation produced no asset id.";
                await _idempotencyStore.FailAsync(idempotencyKey, msg, CancellationToken.None);
                return AZOAResult<FungibleTokenResult>.Failure(msg);
            }

            var result = new FungibleTokenResult
            {
                AvatarId = avatarId,
                WalletId = wallet.Id,
                WalletAddress = wallet.Address,
                WalletProvisioned = provisioned,
                AssetId = asaResult.Result!,
                Replayed = false
            };

            // ── Step 8: settle the idempotency key — Complete only with an asset id.
            await _idempotencyStore.CompleteAsync(
                idempotencyKey, SerializeForReplay(result), CancellationToken.None);

            return new AZOAResult<FungibleTokenResult> { Result = result, Message = "Fungible token created." };
        }
        catch (Exception ex)
        {
            if (!effectMayHaveStarted)
                await IdempotencyClaimRecovery.TryFailAsync(
                    _idempotencyStore, idempotencyKey, UnexpectedFailureMessage, ex);
            throw;
        }
    }

    // ── Provision-if-absent (mirrors AllocationManager.EnsureWalletAsync) ──────

    /// <summary>
    /// Returns the avatar's existing wallet for the chain, or generates one.
    /// Never duplicates (uniqueness is also guarded inside GenerateWalletAsync).
    /// </summary>
    private async Task<(IWallet? Wallet, bool Provisioned, string Error)> EnsureWalletAsync(
        Guid avatarId, string chainType)
    {
        var existing = await _walletStore.GetByAvatarAsync(avatarId);
        // Fail closed on a query error: silently provisioning a new wallet would
        // risk a duplicate and mask the underlying database fault.
        if (existing.IsError)
            return (null, false, existing.Message ?? "Failed to query existing wallets.");

        if (existing.Result is not null)
        {
            var match = existing.Result.FirstOrDefault(w =>
                string.Equals(w.ChainType, chainType, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return (match, false, string.Empty);
        }

        var gen = await _walletManager.GenerateWalletAsync(
            new WalletGenerateRequest { ChainType = chainType }, avatarId);
        if (gen.IsError || gen.Result is null)
            return (null, false, gen.IsError ? gen.Message : "Wallet provisioning failed.");

        return (gen.Result, true, string.Empty);
    }

    // ── Capability resolution ──────────────────────────────────────────────────

    /// <summary>
    /// Resolves the Algorand ASA capability module for the requested chain. Fails
    /// closed when the chain/provider does not expose the module (e.g. a non-ASA
    /// chain) so the claim is settled Failed before any side effect.
    /// </summary>
    private bool TryGetAsaModule(string chainType, out IAlgorandASAModule? module, out string error)
    {
        module = null;
        error = string.Empty;
        try
        {
            var provider = _providerFactory.GetProvider(chainType, ChainNetwork.Devnet);
            if (!provider.TryGetModule<IAlgorandASAModule>(out module) || module is null)
            {
                error = $"Chain '{chainType}' does not expose the Algorand ASA capability required to launch a fungible token.";
                return false;
            }
            return true;
        }
        catch (BlockchainProviderNotFoundException)
        {
            error = $"No fungible-token provider is available for chain '{chainType}'.";
            return false;
        }
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the tenant-supplied supply + decimals before any broadcast. Rejects
    /// a zero total, an out-of-range decimals (0..19), and a total that overflows the
    /// provider's int surface — with a clear error so the idempotency key is failed
    /// (no leak) before any effect.
    /// </summary>
    private static bool TryValidateSupply(FungibleTokenCreateRequest request, out string error)
    {
        error = string.Empty;

        if (request.Total == 0)
        {
            error = "Total supply must be a positive integer base-unit amount.";
            return false;
        }

        if (request.Decimals < 0 || request.Decimals > MaxDecimals)
        {
            error = $"Decimals '{request.Decimals}' is out of range; an Algorand ASA supports 0..{MaxDecimals} decimal places.";
            return false;
        }

        // The provider surface is int (Algorand AssetParams.Total fits, but the
        // module signature is int); reject a total that would overflow it rather
        // than truncating silently.
        if (request.Total > int.MaxValue)
        {
            error = $"Total supply '{request.Total}' exceeds the maximum supported by the provider surface (max {int.MaxValue}).";
            return false;
        }

        return true;
    }

    // ── Idempotency helpers (mirror AllocationManager) ────────────────────────

    /// <summary>
    /// Builds the partitioned idempotency key. Always prefixed with the API-key id
    /// so the dedup namespace is per-tenant. The tail is the client key when
    /// present, else a deterministic SHA-256 over the token descriptor.
    /// </summary>
    private static string BuildIdempotencyKey(
        string apiKeyId, Guid avatarId, FungibleTokenCreateRequest request, string? clientIdempotencyKey)
    {
        var tail = !string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? clientIdempotencyKey.Trim()
            : DeterministicContentKey(avatarId, request);
        return $"fungible:{apiKeyId}:{tail}";
    }

    private static string DeterministicContentKey(Guid avatarId, FungibleTokenCreateRequest request)
    {
        var canonical = string.Join('|',
            avatarId.ToString("N"),
            request.ChainType.ToLowerInvariant(),
            request.Name,
            request.UnitName,
            request.Total.ToString(),
            request.Decimals.ToString());
        return IdempotencyReplay.ContentHash(canonical);
    }

    private static AZOAResult<FungibleTokenResult> ReplayFromRecord(
        IdempotencyRecord record, string idempotencyKey)
        => IdempotencyReplay.ReplayFromRecord<FungibleTokenResult>(
            record,
            DeserializeForReplay,
            r => r.Replayed = true,
            "Duplicate request: returning the result of the original token launch (not re-executed).",
            "Duplicate request: original token launch result could not be replayed.",
            "Original token launch failed.",
            $"Fungible token launch for key '{idempotencyKey}' is already in progress; " +
            "retry once the original request settles.");

    private static string SerializeForReplay(FungibleTokenResult result)
        => IdempotencyReplay.SerializeForReplay(result);

    private static FungibleTokenResult? DeserializeForReplay(string payload)
        => IdempotencyReplay.DeserializeForReplay<FungibleTokenResult>(payload);

}

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
using AZOA.WebAPI.Services.Reserve;
using System.Numerics;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Composes the participant readiness gate, wallet provisioning, and the Algorand ASA
/// capability module into one idempotent, readiness-gated, tenant-callable seam for
/// launching a FUNGIBLE token (real supply + decimals) — the parallel to the
/// supply-1 mint path that <see cref="AllocationManager"/> drives (see
/// <see cref="IFungibleTokenManager"/>). Holds no payment-provider secret and runs
/// no economics — the tenant decides the total supply and decimals; AZOA
/// materialises the custodial wallet and creates the on-chain asset exactly once.
/// <para>
/// W3.1: the mint DESTINATION is node policy, not a caller choice. Under the default
/// <c>Reserve:FungibleMintTarget = PlatformReserve</c> the asset is created BY the
/// platform reserve, with the reserve as manager / reserve / freeze / clawback, and a
/// reserve ledger row is opened for it. That is also the only shape that can broadcast:
/// an ASA-create is signed with the PLATFORM key, so a create whose sender is the
/// caller's custodial wallet has a sender the signature does not match.
/// </para>
/// </summary>
public sealed class FungibleTokenManager : IFungibleTokenManager
{
    private const string OperationType = "fungible_token_create";
    private const string UnexpectedFailureMessage = "Fungible token creation failed unexpectedly.";

    /// <summary>Algorand ASA decimals are constrained to 0..19 by the protocol.</summary>
    private const int MaxDecimals = 19;

    /// <summary>
    /// Stable discriminator for the one partial outcome this path can produce: the asset
    /// exists on chain but its reserve ledger row could not be written. Nothing can be
    /// drawn from such an asset — <c>PlatformReserveService.DrawAsync</c> fails closed on
    /// a missing row — so the gap is safe, but it must be repaired before any allocation.
    /// </summary>
    public const string ReserveLedgerUnregisteredCode = "RESERVE_LEDGER_UNREGISTERED";

    private readonly IValueAccessService _valueAccess;
    private readonly IWalletManager _walletManager;
    private readonly IWalletStore _walletStore;
    private readonly IBlockchainProviderFactory _providerFactory;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly INodeGovernanceGuard _nodeGovernance;
    private readonly IPlatformReserve _platformReserve;
    private readonly ReserveOptions _reserveOptions;

    public FungibleTokenManager(
        IValueAccessService valueAccess,
        IWalletManager walletManager,
        IWalletStore walletStore,
        IBlockchainProviderFactory providerFactory,
        IIdempotencyStore idempotencyStore,
        IPlatformReserve platformReserve,
        ReserveOptions reserveOptions,
        INodeGovernanceGuard? nodeGovernance = null)
    {
        _valueAccess = valueAccess ?? throw new ArgumentNullException(nameof(valueAccess));
        _walletManager = walletManager ?? throw new ArgumentNullException(nameof(walletManager));
        _walletStore = walletStore ?? throw new ArgumentNullException(nameof(walletStore));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _platformReserve = platformReserve ?? throw new ArgumentNullException(nameof(platformReserve));
        _reserveOptions = reserveOptions ?? throw new ArgumentNullException(nameof(reserveOptions));
        _nodeGovernance = nodeGovernance ?? NodeGovernanceGuard.Unrestricted;
    }

    /// <inheritdoc />
    public Task<AZOAResult<FungibleTokenResult>> CreateAsync(
        Guid userId,
        FungibleTokenCreateRequest request,
        Guid callerUserId,
        string? clientIdempotencyKey,
        string apiKeyId,
        Guid? actingTenantId = null)
        => CreateAsync(
            userId, request, callerUserId, clientIdempotencyKey, apiKeyId, actingTenantId,
            ResolveConfiguredMintTarget());

    /// <summary>
    /// Mint-destination-explicit launch. <paramref name="mintTarget"/> decides which
    /// account creates the ASA and holds all four ASA authorities;
    /// <see cref="FungibleMintTarget.PlatformReserve"/> also opens the asset's reserve
    /// ledger row. The interface overload resolves the target from
    /// <c>Reserve:FungibleMintTarget</c>; this one is for callers that must not depend on
    /// node configuration (platform-token genesis).
    /// </summary>
    public async Task<AZOAResult<FungibleTokenResult>> CreateAsync(
        Guid userId,
        FungibleTokenCreateRequest request,
        Guid callerUserId,
        string? clientIdempotencyKey,
        string apiKeyId,
        Guid? actingTenantId,
        FungibleMintTarget mintTarget)
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
        // OI-30 (remediation): this used to return the guard's raw sentence - "Node governance
        // disallows fungible-token:create on chain 'Algorand'. Allowed chains: ..." - which is
        // the exact wording the rail-vocabulary guard flags on three terms. The decision now
        // arrives scrubbed from the counter, so there is nothing here left to forget.
        if (governance.IsRefused)
            return AZOAResult<FungibleTokenResult>.FailureWithCode(
                governance.ConsumerMessage, governance.Code ?? ConsumerSafeErrorCodes.NotPermitted);

        // ── Step 1: idempotency key ───────────────────────────────────────────
        // Client key wins; absent ⇒ deterministic content key over the token
        // descriptor. NEVER a random per-request key. The whole key is partitioned
        // by apiKeyId so two tenants reusing the same human-friendly key cannot
        // collide.
        var idempotencyKey = BuildIdempotencyKey(apiKeyId, userId, request, clientIdempotencyKey, mintTarget);

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
            // A rejected user produces NO side effect at all under a won claim.
            var gate = await _valueAccess.RequireValueAccessAsync(userId, actingTenantId);
            if (gate.IsError)
            {
                await _idempotencyStore.FailAsync(idempotencyKey, gate.Message, CancellationToken.None);
                return AZOAResult<FungibleTokenResult>.Failure(gate.Message);
            }

            // ── Step 4: validate supply + decimals — reject BEFORE any broadcast.
            // A bad total/decimals fails the idempotency key here so the claim is
            // terminal (Failed), never leaked InProgress, and nothing is broadcast.
            if (!TryValidateSupply(request, out var railTotal, out var supplyError))
            {
                await _idempotencyStore.FailAsync(idempotencyKey, supplyError, CancellationToken.None);
                return AZOAResult<FungibleTokenResult>.Failure(supplyError);
            }

            // ── Step 5: provision-if-absent ───────────────────────────────────
            var (wallet, provisioned, walletError) = await EnsureWalletAsync(userId, request.ChainType);
            if (wallet is null)
            {
                await _idempotencyStore.FailAsync(idempotencyKey, walletError, CancellationToken.None);
                return AZOAResult<FungibleTokenResult>.Failure(walletError);
            }

            // ── Step 6: resolve the Algorand ASA capability module ────────────
            if (!TryGetAsaModule(request.ChainType, out var asa, out var moduleError))
            {
                await _idempotencyStore.FailAsync(idempotencyKey, moduleError, CancellationToken.None);
                return AZOAResult<FungibleTokenResult>.Failure(moduleError);
            }

            // ── Step 6b: resolve the mint destination (Q-X3) ──────────────────
            // The creator address is ALSO the manager/reserve/freeze/clawback address:
            // whoever creates the asset is the authority over it. For the reserve target
            // that is the platform reserve account, which is the account whose key signs
            // the create, so sender and signature agree. Resolving it BEFORE the broadcast
            // means an unresolvable reserve fails the idempotency key with no side effect.
            var (creatorAddress, creatorError) = await ResolveCreatorAddressAsync(
                mintTarget, request.ChainType, wallet.Address);
            if (creatorAddress is null)
            {
                await _idempotencyStore.FailAsync(idempotencyKey, creatorError, CancellationToken.None);
                return AZOAResult<FungibleTokenResult>.Failure(creatorError);
            }

            // ── Step 7: create the fungible ASA (the irreversible effect) ─────
            // OI-26: the total handed to the rail is the one step 4 validated (railTotal),
            // not a re-derived or narrowed copy. The old `checked((int)request.Total)` cast
            // capped every asset this node can ever mint at 2,147,483,647 base units, and an
            // ASA total cannot be raised afterwards — a bigger supply means a second asset.
            // AC4b: a FungibleTokenCreate is platform-SIGNED but may be tenant-DRIVEN.
            // When an acting tenant is present, build a tenant-driven platform context
            // (grantor = the on-whose-behalf user, scope = token:create:sign) so the
            // custody seam runs the live consent check before key decrypt; otherwise a
            // plain platform context (no grant required).
            var signingContext = actingTenantId is { } tenant && tenant != Guid.Empty
                ? SigningContext.Platform.ActingAs(tenant, AzoaScopes.TokenCreateSign, userId)
                : SigningContext.Platform;
            effectMayHaveStarted = true;
            var asaResult = await asa!.CreateASAAsync(
                request.Name,
                request.UnitName,
                railTotal,
                request.Decimals,
                managerAddress: creatorAddress,
                reserveAddress: creatorAddress,
                freezeAddress: creatorAddress,
                clawbackAddress: creatorAddress,
                walletAddress: creatorAddress,
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
                UserId = userId,
                WalletId = wallet.Id,
                WalletAddress = wallet.Address,
                WalletProvisioned = provisioned,
                AssetId = asaResult.Result!,
                Replayed = false
            };

            // ── Step 8: open the reserve ledger row for the new asset ─────────
            // Only for a reserve-held asset: partitions are how the node refuses an
            // oversubscribed draw later, and a caller-held asset has no reserve budget to
            // partition. Registration is idempotent (create-if-absent), so a retry of the
            // repair path below cannot open a second row.
            var ledgerWarning = string.Empty;
            if (mintTarget == FungibleMintTarget.PlatformReserve)
            {
                var registration = await _platformReserve.RegisterAssetAsync(
                    new ReserveAssetRegistration(
                        request.ChainType,
                        result.AssetId,
                        request.UnitName,
                        request.Decimals,
                        // The ledger records the total that was MINTED, i.e. the one
                        // step 4 validated onto the rail -- never a second derivation.
                        new BigInteger(railTotal),
                        actingTenantId),
                    CancellationToken.None);

                if (registration.IsError)
                    ledgerWarning =
                        $" WARNING: the asset exists on chain but its reserve ledger row was not written " +
                        $"({registration.Message}). No units can be drawn from it until the row is registered.";
            }

            // ── Step 9: settle the idempotency key — Complete only with an asset id.
            // Completed even when step 8 warned: the mint is irreversible, so a retry must
            // replay this result rather than create a second asset.
            await _idempotencyStore.CompleteAsync(
                idempotencyKey, SerializeForReplay(result), CancellationToken.None);

            return new AZOAResult<FungibleTokenResult>
            {
                Result = result,
                Message = "Fungible token created." + ledgerWarning,
                Code = ledgerWarning.Length == 0 ? null : ReserveLedgerUnregisteredCode,
            };
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
    /// Returns the user's existing wallet for the chain, or generates one.
    /// Never duplicates (uniqueness is also guarded inside GenerateWalletAsync).
    /// </summary>
    private async Task<(IWallet? Wallet, bool Provisioned, string Error)> EnsureWalletAsync(
        Guid userId, string chainType)
    {
        var existing = await _walletStore.GetByUserAsync(userId);
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
            new WalletGenerateRequest { ChainType = chainType }, userId);
        if (gen.IsError || gen.Result is null)
            return (null, false, gen.IsError ? gen.Message : "Wallet provisioning failed.");

        return (gen.Result, true, string.Empty);
    }

    // ── Mint destination (Q-X3) ───────────────────────────────────────────────

    /// <summary>
    /// Reads the node's configured mint destination. An unrecognised value resolves to
    /// <see cref="FungibleMintTarget.PlatformReserve"/> rather than silently falling back
    /// to the legacy caller-wallet shape, which cannot broadcast.
    /// </summary>
    private FungibleMintTarget ResolveConfiguredMintTarget() =>
        string.Equals(
            _reserveOptions.FungibleMintTarget?.Trim(),
            ReserveMintTargets.CallerWallet,
            StringComparison.OrdinalIgnoreCase)
            ? FungibleMintTarget.CallerWallet
            : FungibleMintTarget.PlatformReserve;

    /// <summary>
    /// Resolves the account that creates the ASA and therefore holds all four ASA
    /// authorities. The reserve target fails CLOSED when no reserve account resolves for
    /// the chain: creating a project's asset under an account the node cannot sign for
    /// would strand every unit that asset was meant to carry.
    /// </summary>
    private async Task<(string? Address, string Error)> ResolveCreatorAddressAsync(
        FungibleMintTarget mintTarget, string chainType, string callerWalletAddress)
    {
        if (mintTarget == FungibleMintTarget.CallerWallet)
            return (callerWalletAddress, string.Empty);

        var account = await _platformReserve.GetAccountAsync(chainType);
        if (account.IsError || account.Result is null || string.IsNullOrWhiteSpace(account.Result.Address))
            return (null,
                $"Cannot mint into the platform reserve on '{chainType}': {account.Message}");

        return (account.Result.Address, string.Empty);
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
            // The configured default network, never a hardcoded Devnet: pinning this to
            // devnet made every production fungible-token launch resolve a devnet provider.
            var provider = _providerFactory.GetProvider(chainType, _providerFactory.DefaultNetwork);
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
    /// Validates the tenant-supplied supply + decimals before any broadcast and yields the
    /// exact base-unit total the ASA rail will be handed. Rejects a non-positive total, an
    /// out-of-range decimals (0..19), and anything the rail cannot carry — with a clear
    /// error so the idempotency key is failed (no leak) before any effect.
    /// <para>OI-26: the ceiling is the RAIL's own uint64 supply field
    /// (<see cref="AlgorandAsaSupplyLimits.MaxTotalBaseUnits"/>), no longer the
    /// <c>int.MaxValue</c> of the old module signature. The narrowing runs through
    /// <see cref="AlgorandAsaSupplyLimits.TryNarrowToRailTotal"/> and its RESULT is what
    /// step 7 mints, so widening <see cref="FungibleTokenCreateRequest.Total"/> later
    /// cannot reopen a silent truncation: the conversion is the refusal.</para>
    /// </summary>
    private static bool TryValidateSupply(
        FungibleTokenCreateRequest request, out ulong railTotal, out string error)
    {
        railTotal = 0UL;
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

        return AlgorandAsaSupplyLimits.TryNarrowToRailTotal(request.Total, out railTotal, out error);
    }

    // ── Idempotency helpers (mirror AllocationManager) ────────────────────────

    /// <summary>
    /// Builds the partitioned idempotency key. Always prefixed with the API-key id
    /// so the dedup namespace is per-tenant. The tail is the client key when
    /// present, else a deterministic SHA-256 over the token descriptor.
    /// </summary>
    private static string BuildIdempotencyKey(
        string apiKeyId,
        Guid userId,
        FungibleTokenCreateRequest request,
        string? clientIdempotencyKey,
        FungibleMintTarget mintTarget)
    {
        var tail = !string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? clientIdempotencyKey.Trim()
            : DeterministicContentKey(userId, request, mintTarget);
        return $"fungible:{apiKeyId}:{tail}";
    }

    /// <summary>
    /// The content key includes the mint target: the same descriptor minted into the
    /// reserve and into a caller wallet are two different assets, and collapsing them
    /// onto one key would replay the wrong one back.
    /// </summary>
    private static string DeterministicContentKey(
        Guid userId, FungibleTokenCreateRequest request, FungibleMintTarget mintTarget)
    {
        var canonical = string.Join('|',
            userId.ToString("N"),
            request.ChainType.ToLowerInvariant(),
            request.Name,
            request.UnitName,
            request.Total.ToString(),
            request.Decimals.ToString(),
            mintTarget.ToString());
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

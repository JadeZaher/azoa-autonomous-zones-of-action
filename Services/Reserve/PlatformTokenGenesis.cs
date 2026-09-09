// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

// AZOA.WebAPI.Helpers also declares an Encoding (base32/base64 chain helpers), so the
// bare name is ambiguous here. Every use below is UTF-8 byte counting/hashing.
using Encoding = System.Text.Encoding;

// Aliased rather than importing AZOA.WebAPI.Interfaces wholesale: this file only needs the
// ASA rail's supply ceiling from it, and the namespace carries names that collide here.
using AlgorandAsaSupplyLimits = AZOA.WebAPI.Interfaces.AlgorandAsaSupplyLimits;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>
/// The N-X2 genesis parameters, as constants rather than configuration. Supply, decimals
/// and unit name are decided ONCE: an Algorand ASA cannot be re-scaled, so changing any of
/// them produces a second asset instead of an amended one. Keeping them in code (and
/// echoing them into the <c>platform_token</c> row at claim time) means the devnet proof
/// and the mainnet promotion run the identical definition.
/// </summary>
/// <remarks>Promotion checklist + the decide-once warning: <c>docs/PLATFORM-TOKEN-GENESIS.md</c>.</remarks>
public static class PlatformTokenGenesisDefaults
{
    /// <summary>Chain the platform unit lives on. Algorand-only today (the ASA module is Algorand-specific).</summary>
    public const string ChainType = "Algorand";

    /// <summary>On-chain asset name. Never surfaced to a sponsor -- the product says "funds", not an asset name.</summary>
    public const string Name = "Arda";

    /// <summary>On-chain unit name / ticker.</summary>
    public const string UnitName = "ARDA";

    /// <summary>Base-unit scale. 2 keeps every balance a whole number of cents.</summary>
    public const int Decimals = 2;

    /// <summary>
    /// Genesis total in BASE units when the operator supplies none: 2,000,000,000 base
    /// units = 20,000,000.00 units at <see cref="Decimals"/>. It is a CEILING on issuance,
    /// not a circulating supply: circulating supply is reserve outflow and starts at zero.
    /// <para>OI-26: this value is now a DELIBERATE CHOICE, not a constraint. It was picked as
    /// the largest round total the old <c>int</c> mint surface could carry; that surface is
    /// gone (<see cref="ProviderSupplyCeiling"/> is the rail's own uint64 ceiling), so an
    /// operator who wants <see cref="IntendedTotalBaseUnits"/> must now pass it explicitly on
    /// the genesis request. Settle which one BEFORE genesis: an ASA total cannot be raised
    /// afterwards, and a bigger supply then means a SECOND, confusing unit.</para>
    /// </summary>
    public const ulong TotalBaseUnits = 2_000_000_000UL;

    /// <summary>
    /// The total N-X2 names (10^15 base units). REACHABLE since OI-26 widened
    /// <c>IAlgorandASAModule.CreateASAAsync</c> to the rail's <c>ulong</c> total: it is
    /// comfortably inside <see cref="ProviderSupplyCeiling"/> and a genesis request carrying
    /// it is accepted. It is NOT the default -- <see cref="TotalBaseUnits"/> still is -- so
    /// an operator who intends this total must state it.
    /// </summary>
    public const ulong IntendedTotalBaseUnits = 1_000_000_000_000_000UL;

    /// <summary>
    /// Largest total the mint path can carry: the ASA rail's own uint64 supply field
    /// (<see cref="AlgorandAsaSupplyLimits.MaxTotalBaseUnits"/>). Aliased here rather than
    /// re-typed so the genesis refusal and the rail can never drift apart.
    /// </summary>
    public const ulong ProviderSupplyCeiling = AlgorandAsaSupplyLimits.MaxTotalBaseUnits;

    /// <summary>Algorand caps an ASA unit name at 8 bytes.</summary>
    public const int MaxUnitNameBytes = 8;

    /// <summary>Algorand caps an ASA asset name at 32 bytes.</summary>
    public const int MaxAssetNameBytes = 32;

    /// <summary>Algorand constrains ASA decimals to 0..19 (mirrors <c>FungibleTokenManager</c>).</summary>
    public const int MaxDecimals = 19;

    /// <summary>Idempotency partition the genesis mint dedups under; never a tenant API key.</summary>
    public const string IdempotencyPartition = "platform-token-genesis";

    /// <summary>Test units dispensed to the reserve on a faucet-capable dev/test network.</summary>
    public const decimal FaucetTopUpAmount = 5m;

    /// <summary>
    /// Reserve native balance (base units) below which a dev/test genesis tops up first.
    /// 1,000,000 microAlgos = 1 ALGO, comfortably above the create fee plus the
    /// min-balance bump an ASA adds to the creating account.
    /// </summary>
    public const long MinimumNativeBalanceBaseUnits = 1_000_000L;
}

/// <summary>Stable wire discriminators for the genesis path's actionable outcomes.</summary>
public static class PlatformTokenGenesisCodes
{
    /// <summary>A platform unit already exists on this network and the request contradicts it.</summary>
    public const string AlreadyComplete = "PLATFORM_TOKEN_ALREADY_EXISTS";

    /// <summary>A claimed-but-unminted genesis exists and the request contradicts its frozen definition.</summary>
    public const string DefinitionFrozen = "PLATFORM_TOKEN_DEFINITION_FROZEN";

    /// <summary>The requested total exceeds what the ASA module's int surface can carry.</summary>
    public const string SupplyCeiling = "PLATFORM_TOKEN_SUPPLY_CEILING";

    /// <summary>The operator's asserted network is not the network this node is pointed at.</summary>
    public const string NetworkMismatch = "PLATFORM_TOKEN_NETWORK_MISMATCH";

    /// <summary>The irreversibility acknowledgement was missing.</summary>
    public const string NotConfirmed = "PLATFORM_TOKEN_NOT_CONFIRMED";

    /// <summary>No platform unit has been created on this network yet.</summary>
    public const string NotGenesised = "PLATFORM_TOKEN_NOT_GENESISED";

    /// <summary>The asset exists on chain but the node-state record does not name it yet.</summary>
    public const string Unrecorded = "PLATFORM_TOKEN_UNRECORDED";

    /// <summary>Circulating supply could not be read; it is reported unavailable, never zero.</summary>
    public const string SupplyUnavailable = "PLATFORM_TOKEN_SUPPLY_UNAVAILABLE";
}

/// <summary>The genesis definition: everything frozen by the first accepted call.</summary>
public sealed record PlatformTokenGenesisDefinition(
    string ChainType,
    string Name,
    string UnitName,
    int Decimals,
    BigInteger TotalSupply);

/// <summary>The platform unit as it exists on chain, once genesis has completed.</summary>
public sealed record PlatformTokenDefinition(
    string ChainType,
    string Network,
    string AssetId,
    string Name,
    string UnitName,
    int Decimals,
    BigInteger TotalSupply,
    string ReserveAddress,
    DateTimeOffset? ActivatedAt);

/// <summary>
/// Supply, all in base units. <see cref="Circulating"/> is reserve OUTFLOW derived from
/// the reserve ledger's partitions, never a stored counter, so it cannot drift from what
/// the reserve has actually issued.
/// </summary>
public sealed record PlatformTokenSupply(
    BigInteger Total,
    BigInteger Circulating,
    BigInteger Held,
    BigInteger InReserve);

/// <summary>What a read of the platform unit returns: the definition plus live supply.</summary>
public sealed record PlatformTokenState(PlatformTokenDefinition Definition, PlatformTokenSupply Supply);

/// <summary>What a genesis run did. <c>Created</c> is false when it replayed a completed genesis.</summary>
public sealed record PlatformTokenGenesisOutcome(
    PlatformTokenDefinition Definition,
    bool Created,
    string? OperationsNote);

/// <summary>
/// The one-time operator command that gives this node its platform unit, and the read
/// every platform-unit balance, rate and cash-out depends on.
/// </summary>
public interface IPlatformTokenGenesis
{
    /// <summary>
    /// Runs genesis, or replays a completed one. Idempotent by the <c>platform_token</c>
    /// claim: a second genesis with a DIFFERENT definition is refused, and a retry of the
    /// same one resumes rather than minting a second asset.
    /// </summary>
    Task<AZOAResult<PlatformTokenGenesisOutcome>> RunAsync(
        PlatformTokenGenesisRequest request, Guid operatorUserId, CancellationToken ct = default);

    /// <summary>The platform unit on this node's active network, with live supply figures.</summary>
    Task<AZOAResult<PlatformTokenState>> GetStateAsync(CancellationToken ct = default);
}

/// <summary>
/// The mint seam genesis drives. It exists so genesis depends on a one-method surface
/// rather than on the sealed <see cref="FungibleTokenManager"/>, and so the
/// mint-into-reserve target is pinned here instead of read from node configuration --
/// genesis must not change destination because an operator edited
/// <c>Reserve:FungibleMintTarget</c>.
/// </summary>
public interface IPlatformTokenMinter
{
    /// <summary>Creates the asset with the platform reserve as creator and sole authority.</summary>
    Task<AZOAResult<FungibleTokenResult>> MintIntoReserveAsync(
        Guid operatorUserId,
        FungibleTokenCreateRequest request,
        string operationKey,
        CancellationToken ct = default);
}

/// <summary>Drives the existing fungible-token path (D49) with the reserve target pinned.</summary>
public sealed class ReserveFungibleTokenMinter : IPlatformTokenMinter
{
    private readonly FungibleTokenManager _fungibleTokens;

    public ReserveFungibleTokenMinter(FungibleTokenManager fungibleTokens)
    {
        _fungibleTokens = fungibleTokens ?? throw new ArgumentNullException(nameof(fungibleTokens));
    }

    /// <inheritdoc />
    public Task<AZOAResult<FungibleTokenResult>> MintIntoReserveAsync(
        Guid operatorUserId,
        FungibleTokenCreateRequest request,
        string operationKey,
        CancellationToken ct = default)
        => _fungibleTokens.CreateAsync(
            operatorUserId,
            request,
            operatorUserId,
            operationKey,
            PlatformTokenGenesisDefaults.IdempotencyPartition,
            actingTenantId: null,
            FungibleMintTarget.PlatformReserve);
}

/// <summary>Persistence seam for the platform-unit node-state record.</summary>
public interface IPlatformTokenStore
{
    /// <summary>Every platform-unit row on one network. Genesis reasons over the whole network, not one chain.</summary>
    Task<AZOAResult<IReadOnlyList<PlatformToken>>> ListForNetworkAsync(
        string network, CancellationToken ct = default);

    /// <summary>Creates the row only if it is absent. A losing racer gets the winner's row and <c>Created = false</c>.</summary>
    Task<AZOAResult<PlatformTokenClaim>> ClaimIfAbsentAsync(
        PlatformToken row, CancellationToken ct = default);

    /// <summary>Compare-and-swap write; a lost race is an error, never a silent overwrite.</summary>
    Task<AZOAResult<PlatformToken>> UpdateIfVersionAsync(
        PlatformToken row, long expectedVersion, CancellationToken ct = default);
}

/// <summary>Outcome of a claim: the row, plus whether this caller created it.</summary>
public sealed record PlatformTokenClaim(PlatformToken Row, bool Created);

/// <summary>
/// Platform-unit genesis: claim, fund, mint, record. The mint itself is the path that
/// already existed (D49) -- this type adds the three things a one-time irreversible act
/// needs and that path does not have: a single-winner claim so a second genesis is
/// refusable, a frozen definition so a retry cannot quietly create a different asset, and
/// a node-state record naming the asset id, which <c>appsettings.json</c> cannot hold
/// because the id does not exist until the mint returns.
/// </summary>
public sealed class PlatformTokenGenesis : IPlatformTokenGenesis
{
    private readonly IPlatformReserve _reserve;
    private readonly IPlatformTokenMinter _minter;
    private readonly IPlatformTokenStore _store;
    private readonly IBlockchainProviderFactory _providerFactory;

    public PlatformTokenGenesis(
        IPlatformReserve reserve,
        IPlatformTokenMinter minter,
        IPlatformTokenStore store,
        IBlockchainProviderFactory providerFactory)
    {
        _reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        _minter = minter ?? throw new ArgumentNullException(nameof(minter));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
    }

    // ── Genesis ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AZOAResult<PlatformTokenGenesisOutcome>> RunAsync(
        PlatformTokenGenesisRequest request, Guid operatorUserId, CancellationToken ct = default)
    {
        if (request is null)
            return AZOAResult<PlatformTokenGenesisOutcome>.Failure("A platform-token genesis request is required.");
        if (operatorUserId == Guid.Empty)
            return AZOAResult<PlatformTokenGenesisOutcome>.Failure(
                "A node operator identity is required to run platform-token genesis.");

        var network = _providerFactory.DefaultNetwork.ToString();

        // ── Step 1: resolve + validate the definition ─────────────────────────
        // Everything here is refused BEFORE a row is claimed and long before anything is
        // broadcast, so a rejected request leaves no trace and no asset.
        if (!TryResolveDefinition(request, out var definition, out var validationError, out var validationCode))
            return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode(validationError, validationCode);

        // ── Step 2: the promotion guard ───────────────────────────────────────
        // An operator typing the mainnet definition against a node still wired to devnet
        // gets a refusal instead of the wrong asset on the wrong network.
        if (!string.IsNullOrWhiteSpace(request.ExpectedNetwork) &&
            !string.Equals(request.ExpectedNetwork.Trim(), network, StringComparison.OrdinalIgnoreCase))
        {
            return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode(
                $"This node is pointed at '{network}', not '{request.ExpectedNetwork.Trim()}'. Refusing genesis: " +
                "the platform unit would be created on the wrong network and could not be moved.",
                PlatformTokenGenesisCodes.NetworkMismatch);
        }

        if (!request.ConfirmIrreversible)
        {
            return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode(
                "Genesis is irreversible: total supply, decimals and unit name cannot be changed afterwards, and a " +
                "different definition means a second asset. Set confirmIrreversible = true to proceed.",
                PlatformTokenGenesisCodes.NotConfirmed);
        }

        // ── Step 3: what already exists on this network ───────────────────────
        // Claim vs. resume vs. refuse is decided here. An unreadable state FAILS CLOSED:
        // minting on top of a state we cannot read is exactly how a second platform unit
        // gets created.
        var existing = await _store.ListForNetworkAsync(network, ct);
        if (existing.IsError)
            return AZOAResult<PlatformTokenGenesisOutcome>.Failure(
                $"Refusing genesis: the platform-token state on '{network}' could not be read ({existing.Message}).");

        var rows = existing.Result ?? Array.Empty<PlatformToken>();

        var active = rows.FirstOrDefault(row => row.IsActive);
        if (active is not null)
        {
            if (!MatchesDefinition(active, definition!))
                return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode(
                    SecondGenesisMessage(active, definition!), PlatformTokenGenesisCodes.AlreadyComplete);

            return AZOAResult<PlatformTokenGenesisOutcome>.Success(
                new PlatformTokenGenesisOutcome(ToDefinition(active), Created: false, OperationsNote: null),
                "Platform-token genesis has already completed; returning the existing definition unchanged.");
        }

        var pending = rows.FirstOrDefault(row =>
            string.Equals(row.Status, PlatformToken.StatusPending, StringComparison.Ordinal));
        if (pending is not null && !MatchesDefinition(pending, definition!))
            return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode(
                FrozenDefinitionMessage(pending, definition!), PlatformTokenGenesisCodes.DefinitionFrozen);

        // ── Step 4: the reserve account ───────────────────────────────────────
        // Resolved before the claim so a node with no platform key for the chain never
        // opens a genesis row it cannot finish.
        var account = await _reserve.GetAccountAsync(definition!.ChainType, ct);
        if (account.IsError || account.Result is null || string.IsNullOrWhiteSpace(account.Result.Address))
            return AZOAResult<PlatformTokenGenesisOutcome>.Failure(
                $"Cannot run platform-token genesis on '{definition.ChainType}': {account.Message}");

        // ── Step 5: claim (or resume) the single-winner row ───────────────────
        PlatformToken row;
        if (pending is not null)
        {
            row = pending;
        }
        else
        {
            var claim = await ClaimAsync(definition, network, account.Result.Address, operatorUserId, ct);
            if (claim.IsError || claim.Result is null)
                return AZOAResult<PlatformTokenGenesisOutcome>.Failure(claim.Message);

            row = claim.Result.Row;

            // Losing the claim race is not a failure -- but the winner's definition governs.
            if (!claim.Result.Created)
            {
                if (!MatchesDefinition(row, definition))
                    return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode(
                        FrozenDefinitionMessage(row, definition), PlatformTokenGenesisCodes.DefinitionFrozen);

                if (row.IsActive)
                    return AZOAResult<PlatformTokenGenesisOutcome>.Success(
                        new PlatformTokenGenesisOutcome(ToDefinition(row), Created: false, OperationsNote: null),
                        "Platform-token genesis completed concurrently; returning that definition.");
            }
        }

        // ── Step 6: fees (the devnet faucet path, D49) ────────────────────────
        // On a faucet-capable dev/test network the reserve is topped up so the create fee
        // and the ASA min-balance bump are covered. Never on mainnet.
        var operationsNote = await TryTopUpReserveForFeesAsync(definition.ChainType, account.Result.Address, ct);

        // ── Step 7: the irreversible act ──────────────────────────────────────
        // The definition is narrowed onto the rail HERE, at the counter, so a total the rail
        // cannot carry refuses by name instead of truncating into a smaller permanent unit.
        if (!TryToCreateRequest(definition, out var createRequest, out var narrowError))
        {
            await RecordFailureAsync(row, narrowError, ct);
            return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode(
                narrowError, PlatformTokenGenesisCodes.SupplyCeiling);
        }

        // Anchored on the row's frozen operation key, so a resumed genesis REPLAYS the
        // original mint through the fungible-token idempotency store instead of creating a
        // second asset.
        var mint = await _minter.MintIntoReserveAsync(
            operatorUserId, createRequest!, row.GenesisOperationKey, ct);

        if (mint.IsError || mint.Result is null || string.IsNullOrWhiteSpace(mint.Result.AssetId))
        {
            var reason = mint.IsError ? mint.Message : "the mint returned no asset id";
            await RecordFailureAsync(row, reason, ct);
            return AZOAResult<PlatformTokenGenesisOutcome>.Failure(
                $"Platform-token genesis did not mint: {reason}. The genesis claim stays open, so re-running with the " +
                "SAME definition resumes it." + Suffix(operationsNote));
        }

        // ── Step 8: record the asset id ───────────────────────────────────────
        // A failure here is a REPORTED partial outcome, never a silent one: the asset
        // exists, so the operator is told the id and told that a re-run records it rather
        // than minting again.
        var activated = await ActivateAsync(row, mint.Result.AssetId, ct);
        if (activated.IsError || activated.Result is null)
        {
            return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode(
                $"The platform unit WAS created on chain as asset '{mint.Result.AssetId}', but the platform_token " +
                $"record was not updated ({activated.Message}). Re-run genesis with the same definition to record it; " +
                "the mint replays from its idempotency key and creates no second asset.",
                PlatformTokenGenesisCodes.Unrecorded);
        }

        return AZOAResult<PlatformTokenGenesisOutcome>.Success(
            new PlatformTokenGenesisOutcome(ToDefinition(activated.Result), Created: true, operationsNote),
            $"Platform-token genesis complete on '{network}': asset '{mint.Result.AssetId}'." + Suffix(operationsNote));
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AZOAResult<PlatformTokenState>> GetStateAsync(CancellationToken ct = default)
    {
        var network = _providerFactory.DefaultNetwork.ToString();

        var rows = await _store.ListForNetworkAsync(network, ct);
        if (rows.IsError)
            return AZOAResult<PlatformTokenState>.Failure(
                $"The platform-token state on '{network}' could not be read: {rows.Message}");

        var active = (rows.Result ?? Array.Empty<PlatformToken>()).FirstOrDefault(row => row.IsActive);
        if (active is null)
            return AZOAResult<PlatformTokenState>.FailureWithCode(
                $"No platform unit has been created on '{network}' yet. Until genesis runs, every balance, rate and " +
                "cash-out reads zero.",
                PlatformTokenGenesisCodes.NotGenesised);

        var definition = ToDefinition(active);
        var supply = await ReadSupplyAsync(definition, ct);
        if (supply.IsError || supply.Result is null)
            return AZOAResult<PlatformTokenState>.FailureWithCode(
                supply.Message, PlatformTokenGenesisCodes.SupplyUnavailable);

        return AZOAResult<PlatformTokenState>.Success(new PlatformTokenState(definition, supply.Result));
    }

    /// <summary>
    /// Derives supply from the reserve ledger. Circulating supply is reserve OUTFLOW
    /// (issued minus returned, summed over every partition), so it is computed here rather
    /// than stored -- a stored counter could disagree with what the reserve actually
    /// issued. An unreadable ledger is an ERROR, never zero: the backing-derived rate
    /// divides by circulating supply, so a fabricated zero would price the unit off a
    /// supply nobody knows.
    /// </summary>
    private async Task<AZOAResult<PlatformTokenSupply>> ReadSupplyAsync(
        PlatformTokenDefinition definition, CancellationToken ct)
    {
        var ledger = await _reserve.GetLedgerAsync(definition.ChainType, definition.AssetId, ct);
        if (ledger.IsError || ledger.Result is null)
            return AZOAResult<PlatformTokenSupply>.Failure(
                $"The platform unit exists as asset '{definition.AssetId}', but its reserve ledger row could not be " +
                $"read ({ledger.Message}). Circulating supply is reserve outflow and is derived from that row, so it " +
                "is reported as unavailable rather than zero.");

        var partitions = ReservePartitionCodec.Decode(ledger.Result.Partitions, out var decodeError);
        if (partitions is null)
            return AZOAResult<PlatformTokenSupply>.Failure(decodeError);

        var circulating = BigInteger.Zero;
        var held = BigInteger.Zero;
        foreach (var counters in partitions.Values)
        {
            // Issued - Returned is the reserve's outflow. The ledger's own Return branch
            // refuses a return larger than what is outstanding, so this never goes negative.
            circulating += counters.Issued - counters.Returned;
            held += counters.Held;
        }

        return AZOAResult<PlatformTokenSupply>.Success(new PlatformTokenSupply(
            definition.TotalSupply,
            circulating,
            held,
            definition.TotalSupply - circulating));
    }

    // ── Definition ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills in the N-X2 defaults and refuses anything the mint path or the chain cannot
    /// carry. Every refusal here happens before a claim, so it costs nothing.
    /// </summary>
    private static bool TryResolveDefinition(
        PlatformTokenGenesisRequest request,
        out PlatformTokenGenesisDefinition? definition,
        out string error,
        out string code)
    {
        definition = null;
        error = string.Empty;
        code = AzoaErrorCodes.InvalidRequest;

        var chain = Blank(request.ChainType) ? PlatformTokenGenesisDefaults.ChainType : request.ChainType!.Trim();
        var name = Blank(request.Name) ? PlatformTokenGenesisDefaults.Name : request.Name!.Trim();
        var unitName = Blank(request.UnitName) ? PlatformTokenGenesisDefaults.UnitName : request.UnitName!.Trim();
        var decimals = request.Decimals ?? PlatformTokenGenesisDefaults.Decimals;
        var total = request.Total ?? PlatformTokenGenesisDefaults.TotalBaseUnits;

        if (Encoding.UTF8.GetByteCount(name) > PlatformTokenGenesisDefaults.MaxAssetNameBytes)
        {
            error = $"Asset name '{name}' exceeds the {PlatformTokenGenesisDefaults.MaxAssetNameBytes}-byte Algorand " +
                    "limit for an ASA name.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(unitName) > PlatformTokenGenesisDefaults.MaxUnitNameBytes)
        {
            error = $"Unit name '{unitName}' exceeds the {PlatformTokenGenesisDefaults.MaxUnitNameBytes}-byte " +
                    "Algorand limit for an ASA unit name.";
            return false;
        }

        if (decimals < 0 || decimals > PlatformTokenGenesisDefaults.MaxDecimals)
        {
            error = $"Decimals '{decimals}' is out of range; an Algorand ASA supports " +
                    $"0..{PlatformTokenGenesisDefaults.MaxDecimals} decimal places.";
            return false;
        }

        if (total == 0)
        {
            error = "Total supply must be a positive base-unit amount.";
            return false;
        }

        // OI-26: the ceiling is now the ASA rail's own uint64 supply field, checked through
        // the SAME predicate the mint-time narrowing uses (TryToCreateRequest), so an
        // over-rail total can never be refused in one place and accepted in the other.
        // Run here because every refusal above happens before a claim and so costs nothing.
        // While `request.Total` is a ulong this cannot fire from a request body; it is what
        // keeps a later widening of that DTO from reaching a claim it cannot finish.
        if (!AlgorandAsaSupplyLimits.TryNarrowToRailTotal(total, out _, out var railError))
        {
            error = railError + " Settle the genesis total BEFORE minting: an ASA total cannot be raised " +
                    "afterwards, so a larger supply would mean a second asset.";
            code = PlatformTokenGenesisCodes.SupplyCeiling;
            return false;
        }

        definition = new PlatformTokenGenesisDefinition(chain, name, unitName, decimals, new BigInteger(total));
        return true;
    }

    /// <summary>
    /// The deterministic anchor a resumed genesis reuses. Derived from the frozen
    /// definition, so the same definition always replays the same mint, and a different
    /// definition could never replay onto this one even if the claim were bypassed.
    /// </summary>
    private static string OperationKeyFor(PlatformTokenGenesisDefinition definition, string network)
    {
        var canonical = string.Join('|',
            "platform-token-genesis",
            definition.ChainType.Trim().ToLowerInvariant(),
            network.Trim().ToLowerInvariant(),
            definition.Name,
            definition.UnitName,
            definition.Decimals.ToString(CultureInfo.InvariantCulture),
            definition.TotalSupply.ToString(CultureInfo.InvariantCulture));

        return "genesis:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>
    /// Narrows the frozen definition onto the mint request, refusing rather than truncating.
    /// <para>OI-26: a <see cref="PlatformTokenGenesisDefinition"/> carries a
    /// <see cref="BigInteger"/> total while the ASA rail's supply field is a uint64, so this
    /// is the one place left in the genesis path where a total can be lost. It used to be an
    /// unchecked <c>(ulong)</c> cast: a definition above the rail would have minted a
    /// silently SMALLER unit, permanently, since an ASA total cannot be raised afterwards.</para>
    /// </summary>
    private static bool TryToCreateRequest(
        PlatformTokenGenesisDefinition definition,
        out FungibleTokenCreateRequest? request,
        out string error)
    {
        request = null;

        if (!AlgorandAsaSupplyLimits.TryNarrowToRailTotal(definition.TotalSupply, out var railTotal, out error))
            return false;

        request = new FungibleTokenCreateRequest
        {
            ChainType = definition.ChainType,
            Name = definition.Name,
            UnitName = definition.UnitName,
            Total = railTotal,
            Decimals = definition.Decimals,
        };
        return true;
    }

    /// <summary>True when a persisted row carries exactly the definition being requested.</summary>
    private static bool MatchesDefinition(PlatformToken row, PlatformTokenGenesisDefinition definition) =>
        string.Equals(row.Chain, definition.ChainType, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(row.Name, definition.Name, StringComparison.Ordinal) &&
        string.Equals(row.UnitName, definition.UnitName, StringComparison.Ordinal) &&
        row.Decimals == definition.Decimals &&
        string.Equals(
            row.TotalSupply,
            definition.TotalSupply.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    private static PlatformTokenDefinition ToDefinition(PlatformToken row) => new(
        row.Chain,
        row.Network,
        row.AssetId ?? string.Empty,
        row.Name,
        row.UnitName,
        row.Decimals,
        BigInteger.TryParse(row.TotalSupply, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var total)
            ? total
            : BigInteger.Zero,
        row.ReserveAddress,
        row.ActivatedAt);

    private static string SecondGenesisMessage(PlatformToken active, PlatformTokenGenesisDefinition wanted) =>
        $"A platform unit already exists on '{active.Network}': asset '{active.AssetId}' ({active.UnitName}, " +
        $"{active.Decimals} decimals, total {active.TotalSupply} base units on {active.Chain}). The requested " +
        $"definition ({wanted.UnitName}, {wanted.Decimals} decimals, total {wanted.TotalSupply} base units on " +
        $"{wanted.ChainType}) is different, and an Algorand ASA cannot be re-scaled -- honouring it would create a " +
        "SECOND platform unit. Refusing.";

    private static string FrozenDefinitionMessage(PlatformToken claimed, PlatformTokenGenesisDefinition wanted) =>
        $"Genesis on '{claimed.Network}' was already claimed with a different definition ({claimed.UnitName}, " +
        $"{claimed.Decimals} decimals, total {claimed.TotalSupply} base units on {claimed.Chain}); the requested one " +
        $"is {wanted.UnitName}, {wanted.Decimals} decimals, total {wanted.TotalSupply} base units on " +
        $"{wanted.ChainType}. The claimed definition is frozen: re-run with it, or delete the claimed platform_token " +
        "row deliberately if nothing was ever minted.";

    private static string Suffix(string? note) => string.IsNullOrWhiteSpace(note) ? string.Empty : " " + note;

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    // ── Row lifecycle ─────────────────────────────────────────────────────────

    private Task<AZOAResult<PlatformTokenClaim>> ClaimAsync(
        PlatformTokenGenesisDefinition definition,
        string network,
        string reserveAddress,
        Guid operatorUserId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var row = new PlatformToken
        {
            Id = PlatformToken.RecordIdFor(definition.ChainType, network),
            Chain = definition.ChainType,
            Network = network,
            Status = PlatformToken.StatusPending,
            AssetId = null,
            Name = definition.Name,
            UnitName = definition.UnitName,
            Decimals = definition.Decimals,
            TotalSupply = definition.TotalSupply.ToString(CultureInfo.InvariantCulture),
            ReserveAddress = reserveAddress,
            GenesisOperationKey = OperationKeyFor(definition, network),
            GenesisUserId = operatorUserId.ToString("N"),
            LastFailureReason = null,
            Version = 0,
            ClaimedAt = now,
            ActivatedAt = null,
            UpdatedAt = now,
        };

        return _store.ClaimIfAbsentAsync(row, ct);
    }

    private Task<AZOAResult<PlatformToken>> ActivateAsync(PlatformToken row, string assetId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var next = Clone(row);
        next.Status = PlatformToken.StatusActive;
        next.AssetId = assetId.Trim();
        next.LastFailureReason = null;
        next.ActivatedAt = now;
        next.UpdatedAt = now;
        next.Version = row.Version + 1;

        return _store.UpdateIfVersionAsync(next, row.Version, ct);
    }

    /// <summary>
    /// Records why the last attempt did not finish. Best effort and deliberately NOT
    /// terminal: a mint that reported an error may still have broadcast, so the row stays
    /// Pending and the retry replays through the mint path's idempotency key.
    /// </summary>
    private async Task RecordFailureAsync(PlatformToken row, string reason, CancellationToken ct)
    {
        var next = Clone(row);
        next.LastFailureReason = reason;
        next.UpdatedAt = DateTimeOffset.UtcNow;
        next.Version = row.Version + 1;

        await _store.UpdateIfVersionAsync(next, row.Version, ct);
    }

    private static PlatformToken Clone(PlatformToken row) => new()
    {
        Id = row.Id,
        Chain = row.Chain,
        Network = row.Network,
        Status = row.Status,
        AssetId = row.AssetId,
        Name = row.Name,
        UnitName = row.UnitName,
        Decimals = row.Decimals,
        TotalSupply = row.TotalSupply,
        ReserveAddress = row.ReserveAddress,
        GenesisOperationKey = row.GenesisOperationKey,
        GenesisUserId = row.GenesisUserId,
        LastFailureReason = row.LastFailureReason,
        Version = row.Version,
        ClaimedAt = row.ClaimedAt,
        ActivatedAt = row.ActivatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    // ── Operations: the devnet faucet path (D49) ──────────────────────────────

    /// <summary>
    /// Tops the reserve up so it can pay the ASA-create fee and the min-balance bump the
    /// new asset adds to the creating account. Runs only on a NON-mainnet network with a
    /// faucet-capable provider, and never fails the run: a faucet that cannot dispense
    /// returns a note, and the mint itself reports the real consequence.
    /// </summary>
    private async Task<string?> TryTopUpReserveForFeesAsync(
        string chainType, string reserveAddress, CancellationToken ct)
    {
        var network = _providerFactory.DefaultNetwork;
        if (network == ChainNetwork.Mainnet)
            return null;

        try
        {
            // A factory that yields NO provider for the chain is the same situation as one
            // that throws BlockchainProviderNotFoundException below: there is no faucet to
            // reach. This method promises never to fail the run, so it must not dereference
            // a null here — the mint reports the real consequence of an unfunded reserve.
            var provider = _providerFactory.GetProvider(chainType, network);
            if (provider is null || !provider.SupportsFaucet)
                return null;

            var balance = await _reserve.GetBalanceAsync(chainType, assetId: null, ct);
            if (!balance.IsError &&
                BigInteger.TryParse(
                    balance.Result, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var held) &&
                held >= new BigInteger(PlatformTokenGenesisDefaults.MinimumNativeBalanceBaseUnits))
            {
                return null;
            }

            var dispense = await provider.DispenseFromFaucetAsync(
                reserveAddress,
                PlatformTokenGenesisDefaults.FaucetTopUpAmount,
                $"platform-token-genesis:{network}:{reserveAddress}",
                ct);

            return dispense.IsError
                ? $"The {network} faucet could not top the reserve up ({dispense.Message}); the mint fails unless the " +
                  "reserve already holds enough native units for the create fee."
                : $"Topped the reserve up with {PlatformTokenGenesisDefaults.FaucetTopUpAmount} test units from the " +
                  $"{network} faucet to cover the create fee.";
        }
        catch (BlockchainProviderNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>
/// SurrealDB-backed platform-token store. Colocated with the service for the same reason
/// <c>SurrealPlatformReserveLedgerStore</c> is: the seam is
/// <see cref="IPlatformTokenStore"/>, so moving it into <c>Providers/Stores/Surreal/</c>
/// later is a file move.
/// </summary>
public sealed class SurrealPlatformTokenStore : IPlatformTokenStore
{
    private const string Table = PlatformToken.SchemaNameConst;

    private readonly ISurrealExecutor _executor;

    public SurrealPlatformTokenStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<PlatformToken>>> ListForNetworkAsync(
        string network, CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE network = $_network")
                .WithParam("_t", Table)
                .WithParam("_network", network);

            var rows = await _executor.QueryAsync<PlatformToken>(query, ct);
            return AZOAResult<IReadOnlyList<PlatformToken>>.Success(rows.ToList());
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<PlatformToken>>()
                .CaptureException(ex, $"Platform-token read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<PlatformTokenClaim>> ClaimIfAbsentAsync(
        PlatformToken row, CancellationToken ct = default)
    {
        try
        {
            var create = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", row.Id)
                .WithParam("_body", row);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(create, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            var created = response.GetValues<PlatformToken>(0).SingleOrDefault() ?? row;
            return AZOAResult<PlatformTokenClaim>.Success(
                new PlatformTokenClaim(created, Created: true), "Genesis claimed.");
        }
        catch (Exception ex)
        {
            // A CREATE that lost to an existing row is the idempotent case -- but only when
            // the row really is there now. Re-read before concluding that, so a transport
            // failure is never reported as "genesis already claimed".
            var existing = await GetAsync(row.Id, ct);
            if (!existing.IsError && existing.Result is not null)
                return AZOAResult<PlatformTokenClaim>.Success(
                    new PlatformTokenClaim(existing.Result, Created: false), "Genesis was already claimed.");

            return new AZOAResult<PlatformTokenClaim>()
                .CaptureException(ex, $"Genesis claim failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<PlatformToken>> UpdateIfVersionAsync(
        PlatformToken row, long expectedVersion, CancellationToken ct = default)
    {
        try
        {
            var update = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) CONTENT $_body WHERE version = $_expected RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", row.Id)
                .WithParam("_body", row)
                .WithParam("_expected", expectedVersion);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(update, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            if (response.Count == 0 || !response[0].IsOk || response[0].AffectedCount() != 1)
                return AZOAResult<PlatformToken>.Failure(
                    $"Platform-token row '{row.Id}' changed underneath this update (expected version {expectedVersion}).");

            var persisted = response.GetValues<PlatformToken>(0).SingleOrDefault() ?? row;
            return AZOAResult<PlatformToken>.Success(persisted, "Saved.");
        }
        catch (Exception ex)
        {
            return new AZOAResult<PlatformToken>()
                .CaptureException(ex, $"Platform-token update failed: {ex.Message}");
        }
    }

    private async Task<AZOAResult<PlatformToken?>> GetAsync(string recordId, CancellationToken ct)
    {
        try
        {
            var query = SurrealQuery<PlatformToken>.Key(recordId);
            var row = await _executor.QuerySingleAsync<PlatformToken>(query, ct);
            return new AZOAResult<PlatformToken?> { Result = row, Message = row is null ? "Not found." : "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<PlatformToken?>()
                .CaptureException(ex, $"Platform-token read failed: {ex.Message}");
        }
    }
}

/// <summary>
/// DI wiring for the genesis path. Lives here so <c>Program.cs</c> (W3.1's file) needs one
/// call rather than three registrations.
/// </summary>
public static class PlatformTokenGenesisRegistration
{
    /// <summary>Registers the platform-token store, the reserve-pinned minter, and the genesis service.</summary>
    public static IServiceCollection AddPlatformTokenGenesis(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IPlatformTokenStore, SurrealPlatformTokenStore>();
        services.AddScoped<IPlatformTokenMinter, ReserveFungibleTokenMinter>();
        services.AddScoped<IPlatformTokenGenesis, PlatformTokenGenesis>();
        return services;
    }
}

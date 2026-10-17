
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Stores.Surreal;
using Encoding = System.Text.Encoding;
using AlgorandAsaSupplyLimits = AZOA.WebAPI.Interfaces.AlgorandAsaSupplyLimits;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>Default immutable platform-unit definition and provider wire limits.</summary>
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

    /// <summary>Default genesis supply in integer base units.</summary>
    public const ulong TotalBaseUnits = 2_000_000_000UL;

    /// <summary>Optional larger supply explicitly selected before genesis.</summary>
    public const ulong IntendedTotalBaseUnits = 1_000_000_000_000_000UL;

    /// <summary>Maximum supply supported by the Algorand uint64 asset field.</summary>
    public const ulong ProviderSupplyCeiling = AlgorandAsaSupplyLimits.MaxTotalBaseUnits;

    /// <summary>Algorand caps an ASA unit name at 8 bytes.</summary>
    public const int MaxUnitNameBytes = 8;

    /// <summary>Algorand caps an ASA asset name at 32 bytes.</summary>
    public const int MaxAssetNameBytes = 32;

    /// <summary>Algorand constrains ASA decimals to 0..19 (provider wire limit).</summary>
    public const int MaxDecimals = 19;

    /// <summary>Idempotency partition the genesis mint dedups under; never a tenant API key.</summary>
    public const string IdempotencyPartition = "platform-token-genesis";

    /// <summary>Suggested explicit devnet faucet funding amount.</summary>
    public const decimal FaucetTopUpAmount = 5m;

    /// <summary>Suggested minimum devnet reserve funding in native base units.</summary>
    public const long MinimumNativeBalanceBaseUnits = 1_000_000L;
}

/// <summary>Stable wire discriminators for the genesis path's actionable outcomes.</summary>
public static class PlatformTokenGenesisCodes
{
    /// <summary>A platform unit already exists on this network and the request contradicts it.</summary>
    public const string AlreadyComplete = "PLATFORM_TOKEN_ALREADY_EXISTS";

    /// <summary>A claimed-but-unminted genesis exists and the request contradicts its frozen definition.</summary>
    public const string DefinitionFrozen = "PLATFORM_TOKEN_DEFINITION_FROZEN";

    /// <summary>The requested total exceeds the provider's uint64 supply limit.</summary>
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
    public const string EvidenceRequired = "PLATFORM_TOKEN_EVIDENCE_REQUIRED";
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

/// <summary>Live supply partitions in integer base units, derived from the reserve ledger.</summary>
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
    string? OperationsNote,
    AZOA.WebAPI.Models.Blockchain.FungibleCreationView? Operation = null);

/// <summary>Admits one immutable platform genesis and reads its finalized supply.</summary>
public interface IPlatformTokenGenesis
{
    /// <summary>Claims or replays the frozen definition and returns its actual durable creation state.</summary>
    Task<AZOAResult<PlatformTokenGenesisOutcome>> RunAsync(
        PlatformTokenGenesisRequest request, Guid operatorUserId, CancellationToken ct = default);

    /// <summary>The platform unit on this node's active network, with live supply figures.</summary>
    Task<AZOAResult<PlatformTokenState>> GetStateAsync(CancellationToken ct = default);
}

/// <summary>Admits a reserve-bound platform creation without accepting costs or signing.</summary>
public interface IPlatformTokenMinter
{
    /// <summary>Admits or replays the exact genesis creation with platform reserve roles.</summary>
    Task<AZOAResult<FungibleTokenResult>> MintIntoReserveAsync(
        Guid operatorUserId,
        FungibleTokenCreateRequest request,
        string operationKey,
        CancellationToken ct = default);
}

/// <summary>Persistence seam for the platform-unit node-state record.</summary>
public interface IPlatformTokenStore
{
    /// <summary>Checks exact completed creation, immutable issuance and reserve lineage for an active definition.</summary>
    Task<bool> HasVerifiedIssuanceAsync(PlatformToken row, CancellationToken ct = default);
    /// <summary>Every platform-unit row on one network. Genesis reasons over the whole network, not one chain.</summary>
    Task<AZOAResult<IReadOnlyList<PlatformToken>>> ListForNetworkAsync(
        string network, CancellationToken ct = default);

    /// <summary>Creates the row only if it is absent. A losing racer gets the winner's row and <c>Created = false</c>.</summary>
    Task<AZOAResult<PlatformTokenClaim>> ClaimIfAbsentAsync(
        PlatformToken row, CancellationToken ct = default, SigningCredentialWitness? credential = null);

    /// <summary>Compare-and-swap write; a lost race is an error, never a silent overwrite.</summary>
    Task<AZOAResult<PlatformToken>> UpdateIfVersionAsync(
        PlatformToken row, long expectedVersion, CancellationToken ct = default);
}

/// <summary>Outcome of a claim: the row, plus whether this caller created it.</summary>
public sealed record PlatformTokenClaim(PlatformToken Row, bool Created);

/// <inheritdoc/>
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

    /// <inheritdoc />
    public async Task<AZOAResult<PlatformTokenGenesisOutcome>> RunAsync(
        PlatformTokenGenesisRequest request, Guid operatorUserId, CancellationToken ct = default)
    {
        if (request is null)
            return AZOAResult<PlatformTokenGenesisOutcome>.Failure("A platform-token genesis request is required.");
        if (request.AuthenticationWitness is not { } credential || credential.ActorUserId != operatorUserId
            || PlatformGenesisFence.Operator(credential) is null)
            return AZOAResult<PlatformTokenGenesisOutcome>.Failure(
                "A node operator identity is required to run platform-token genesis.");

        var network = _providerFactory.DefaultNetwork.ToString();
        if (!TryResolveDefinition(request, out var definition, out var validationError, out var validationCode))
            return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode(validationError, validationCode);
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
        var existing = await _store.ListForNetworkAsync(network, ct);
        if (existing.IsError)
            return AZOAResult<PlatformTokenGenesisOutcome>.Failure(
                $"Refusing genesis: the platform-token state on '{network}' could not be read ({existing.Message}).");

        var rows = existing.Result ?? Array.Empty<PlatformToken>();

        var active = rows.FirstOrDefault(row => row.IsActive);
        if (active is not null)
        {
            if (!await _store.HasVerifiedIssuanceAsync(active, ct))
                return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode("The existing platform definition requires authoritative issuance evidence; it cannot be reissued or activated from asset ID text.", PlatformTokenGenesisCodes.EvidenceRequired);
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
        var account = await _reserve.GetAccountAsync(definition!.ChainType, ct);
        if (account.IsError || account.Result is null || string.IsNullOrWhiteSpace(account.Result.Address))
            return AZOAResult<PlatformTokenGenesisOutcome>.Failure(
                $"Cannot run platform-token genesis on '{definition.ChainType}': {account.Message}");
        try
        {
            if (!_providerFactory.GetProvider(definition.ChainType, _providerFactory.DefaultNetwork)
                .TryGetModule<AZOA.WebAPI.Interfaces.IFungibleCreationModule>(out _))
                return AZOAResult<PlatformTokenGenesisOutcome>.Failure("The selected provider has no durable fungible creation capability.");
        }
        catch (BlockchainProviderNotFoundException)
        {
            return AZOAResult<PlatformTokenGenesisOutcome>.Failure("The selected genesis provider is unavailable.");
        }
        PlatformToken row;
        if (pending is not null)
        {
            row = pending;
        }
        else
        {
            var claim = await ClaimAsync(definition, network, account.Result.Address, operatorUserId, credential, ct);
            if (claim.IsError || claim.Result is null)
                return AZOAResult<PlatformTokenGenesisOutcome>.Failure(claim.Message);

            row = claim.Result.Row;
            if (!claim.Result.Created)
            {
                if (!MatchesDefinition(row, definition))
                    return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode(
                        FrozenDefinitionMessage(row, definition), PlatformTokenGenesisCodes.DefinitionFrozen);

                if (row.IsActive && await _store.HasVerifiedIssuanceAsync(row, ct))
                    return AZOAResult<PlatformTokenGenesisOutcome>.Success(
                        new PlatformTokenGenesisOutcome(ToDefinition(row), Created: false, OperationsNote: null),
                        "Platform-token genesis completed concurrently; returning that definition.");
                if (row.IsActive)
                    return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode("The winning platform definition requires verified issuance evidence.", PlatformTokenGenesisCodes.EvidenceRequired);
            }
        }
        const string operationsNote = "Fund the displayed reserve network fee and minimum balance before explicit continuation; genesis polling does not request faucet funds.";
        if (!TryToCreateRequest(definition, out var createRequest, out var narrowError))
        {
            await RecordFailureAsync(row, narrowError, ct);
            return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode(
                narrowError, PlatformTokenGenesisCodes.SupplyCeiling);
        }
        createRequest!.AuthenticationWitness = request.AuthenticationWitness;
        createRequest.Network = _providerFactory.DefaultNetwork;
        var mint = await _minter.MintIntoReserveAsync(
            operatorUserId, createRequest!, row.GenesisOperationKey, ct);

        if (mint.IsError || mint.Result is null)
        {
            var reason = mint.IsError ? mint.Message : "the mint returned no asset id";
            await RecordFailureAsync(row, reason, ct);
            return AZOAResult<PlatformTokenGenesisOutcome>.Failure(
                $"Platform-token genesis did not mint: {reason}. The genesis claim stays open, so re-running with the " +
                "SAME definition resumes it." + Suffix(operationsNote));
        }
        if (mint.Result.Operation is null)
            return AZOAResult<PlatformTokenGenesisOutcome>.Failure("Genesis admission returned no durable operation receipt; refresh the original definition without issuing another asset.");
        if (mint.Result.Operation is not { State: "Completed" } operation)
            return AZOAResult<PlatformTokenGenesisOutcome>.Success(
                new PlatformTokenGenesisOutcome(ToDefinition(row), Created: false, operationsNote, mint.Result.Operation),
                "Platform genesis is pending explicit cost consent, chain confirmation and atomic accounting.");
        var activated = await _store.ListForNetworkAsync(network, ct);
        var activeProjection = activated.Result?.SingleOrDefault(value => value.Id == row.Id && value.IsActive
            && value.AssetId == operation.AssetId && value.CreationOperationId == operation.OperationId);
        if (activated.IsError || activeProjection is null)
        {
            return AZOAResult<PlatformTokenGenesisOutcome>.FailureWithCode(
                $"The platform unit WAS created on chain as asset '{mint.Result.AssetId}', but the platform_token " +
                "record does not match its atomic creation receipt. Refresh the original operation; do not admit another genesis.",
                PlatformTokenGenesisCodes.Unrecorded);
        }

        return AZOAResult<PlatformTokenGenesisOutcome>.Success(
            new PlatformTokenGenesisOutcome(ToDefinition(activeProjection), Created: true, operationsNote, operation),
            $"Platform-token genesis complete on '{network}': asset '{mint.Result.AssetId}'." + Suffix(operationsNote));
    }

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
        if (!await _store.HasVerifiedIssuanceAsync(active, ct))
            return AZOAResult<PlatformTokenState>.FailureWithCode("The active legacy platform definition lacks its exact verified issuance and reserve lineage.", PlatformTokenGenesisCodes.EvidenceRequired);

        var definition = ToDefinition(active);
        var supply = await ReadSupplyAsync(definition, ct);
        if (supply.IsError || supply.Result is null)
            return AZOAResult<PlatformTokenState>.FailureWithCode(
                supply.Message, PlatformTokenGenesisCodes.SupplyUnavailable);

        return AZOAResult<PlatformTokenState>.Success(new PlatformTokenState(definition, supply.Result));
    }

    /// <summary>Derives circulating, held and reserve supply from authoritative ledger partitions.</summary>
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
            circulating += counters.Issued - counters.Returned;
            held += counters.Held;
        }

        return AZOAResult<PlatformTokenSupply>.Success(new PlatformTokenSupply(
            definition.TotalSupply,
            circulating,
            held,
            definition.TotalSupply - circulating));
    }

    /// <summary>Validates the requested platform definition against provider wire limits.</summary>
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

    /// <summary>Derives the original request key from the frozen definition and network.</summary>
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

    /// <summary>Narrows the frozen definition onto the mint request, refusing rather than truncating.</summary>
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

    private Task<AZOAResult<PlatformTokenClaim>> ClaimAsync(
        PlatformTokenGenesisDefinition definition,
        string network,
        string reserveAddress,
        Guid operatorUserId,
        SigningCredentialWitness credential,
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

        return _store.ClaimIfAbsentAsync(row, ct, credential);
    }

    /// <summary>Records a pending diagnostic without changing issuance or activation authority.</summary>
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
        CreationOperationId = row.CreationOperationId,
        LastFailureReason = row.LastFailureReason,
        Version = row.Version,
        ClaimedAt = row.ClaimedAt,
        ActivatedAt = row.ActivatedAt,
        UpdatedAt = row.UpdatedAt,
    };

}

/// <summary>Registers the durable platform genesis services.</summary>
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

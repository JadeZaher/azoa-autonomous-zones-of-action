// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Signing;
using AlgorandAccount = Algorand.Algod.Model.Account;
using SolanaPublicKey = Solnet.Wallet.PublicKey;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>Operator configuration for platform reserve custody and ledger policy.</summary>
public sealed class ReserveOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Reserve";

    /// <summary>Configured reserve chains; an empty list uses ShippedChains.</summary>
    public List<string> Chains { get; set; } = new();

    /// <summary>Explicit fallback chains for an unconfigured reserve.</summary>
    public static readonly IReadOnlyList<string> ShippedChains = new[] { "Algorand" };

    /// <summary>Whether explicitly configured chains may omit expected reserve addresses.</summary>
    public bool AllowUndeclaredReserveAddress { get; set; }

    /// <summary>Default fungible creation custody target.</summary>
    public string FungibleMintTarget { get; set; } = ReserveMintTargets.PlatformReserve;

    /// <summary>Expected public reserve addresses for each configured chain.</summary>
    public Dictionary<string, string> ExpectedAddresses { get; set; } = new();

    /// <summary>Whether a separate fee payer is configured; currently unsupported.</summary>
    public bool SeparateFeePayerAccount { get; set; }

    /// <summary>Policy governing tenant draws from the platform unit.</summary>
    public string PlatformUnitDrawPolicy { get; set; } = ReservePlatformUnitDrawPolicies.OperatorOnly;

    /// <summary>Tenants allowed by the DeclaredTenants draw policy.</summary>
    public List<string> PlatformUnitDrawTenants { get; set; } = new();

    /// <summary>Effective deduplicated reserve chain list.</summary>
    public IReadOnlyList<string> EffectiveChains
    {
        get
        {
            var declared = Chains
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(entry => entry.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return declared.Count > 0 ? declared : ShippedChains;
        }
    }

    /// <summary>Tests whether a chain is permitted by reserve configuration.</summary>
    public bool PermitsChain(string? chain)
    {
        var candidate = chain?.Trim();
        if (string.IsNullOrEmpty(candidate))
            return false;

        var declared = Chains.Count > 0 ? Chains.AsEnumerable() : ShippedChains.AsEnumerable();
        return declared.Any(entry =>
            string.Equals(entry?.Trim(), candidate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the configured public reserve address for a chain.</summary>
    public string DeclaredAddressFor(string? chain)
    {
        var candidate = chain?.Trim();
        if (string.IsNullOrEmpty(candidate))
            return string.Empty;

        var declared = ExpectedAddresses
            .FirstOrDefault(entry =>
                string.Equals(entry.Key?.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
            .Value;
        return string.IsNullOrWhiteSpace(declared) ? string.Empty : declared.Trim();
    }

    /// <summary>Returns a refusal when a required reserve address declaration is absent.</summary>
    public string? RefuseUndeclaredReserveAddress(string? chain, string? derivedAddress = null)
    {
        if (AllowUndeclaredReserveAddress)
            return null;
        if (Chains.Count == 0)
            return null;

        var candidate = chain?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(DeclaredAddressFor(candidate)))
            return null;

        var hint = string.IsNullOrWhiteSpace(derivedAddress)
            ? "the address this node's platform key derives"
            : $"'{derivedAddress.Trim()}' (the address this node's platform key derives)";

        return
            $"Chain '{candidate}' is declared under {SectionName}:Chains but no reserve address is declared " +
            $"under {SectionName}:ExpectedAddresses:{candidate}, so the address-mismatch guard would be inert " +
            $"there. Set {SectionName}__ExpectedAddresses__{candidate} to {hint}. Refusing every reserve " +
            "operation on this chain until it is declared.";
    }
}

/// <summary>Accepted values for <see cref="ReserveOptions.FungibleMintTarget"/>.</summary>
public static class ReserveMintTargets
{
    /// <summary>Mint into the platform reserve; the reserve holds all four ASA authorities.</summary>
    public const string PlatformReserve = "PlatformReserve";

    /// <summary>Create with the caller's owned wallet and its explicit signing authority.</summary>
    public const string CallerWallet = "CallerWallet";
}

/// <inheritdoc/>
public sealed class PlatformReserveService : IPlatformReserve
{
    /// <summary>Bounded per-asset draw replay window; durable caller receipts remain required.</summary>
    public const int DrawReplayWindowSize = 128;

    /// <summary>Attempts a partition move makes before giving up on a compare-and-swap race.</summary>
    private const int MaxCasAttempts = 4;

    private readonly IKeyCustodyService _custody;
    private readonly IBlockchainProviderFactory _providerFactory;
    private readonly IPlatformReserveLedgerStore _ledgerStore;
    private readonly ReserveOptions _options;

    public PlatformReserveService(
        IKeyCustodyService custody,
        IBlockchainProviderFactory providerFactory,
        IPlatformReserveLedgerStore ledgerStore,
        ReserveOptions options)
    {
        _custody = custody ?? throw new ArgumentNullException(nameof(custody));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    // ── Account + signing ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AZOAResult<ReserveAccount>> GetAccountAsync(
        string chainType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chainType))
            return AZOAResult<ReserveAccount>.Failure("A chain is required to resolve the platform reserve account.");

        var chain = chainType.Trim();
        if (!IsChainApproved(chain))
            return AZOAResult<ReserveAccount>.Failure(
                $"Chain '{chain}' is not listed under {ReserveOptions.SectionName}:Chains, so this node holds no " +
                "platform reserve there. Add it deliberately before any value moves on that chain.");

        var derived = await _custody.WithPlatformSigningKeyAsync(
            chain,
            isPlatformContext: true,
            SigningContext.PlatformFor(chain),
            key => Task.FromResult(PlatformReserveAddressDerivation.Derive(chain, key)));

        if (derived.IsError)
            return AZOAResult<ReserveAccount>.Failure(
                $"No platform reserve account is available on '{chain}': {derived.Message}");

        var address = derived.Result;
        if (string.IsNullOrWhiteSpace(address))
            return AZOAResult<ReserveAccount>.Failure(
                $"No reserve address could be derived for chain '{chain}'. " +
                PlatformReserveAddressDerivation.SupportedChainsMessage);

        var undeclared = _options.RefuseUndeclaredReserveAddress(chain, address);
        if (undeclared is not null)
            return AZOAResult<ReserveAccount>.Failure(undeclared);

        if (!MatchesExpectedAddress(chain, address!, out var mismatch))
            return AZOAResult<ReserveAccount>.Failure(mismatch);

        return AZOAResult<ReserveAccount>.Success(
            new ReserveAccount(chain, address!, IsFeePayer: !_options.SeparateFeePayerAccount),
            "Platform reserve account resolved.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<string>> GetBalanceAsync(
        string chainType, string? assetId = null, CancellationToken ct = default)
    {
        var account = await GetAccountAsync(chainType, ct);
        if (account.IsError || account.Result is null)
            return AZOAResult<string>.Failure(account.Message);

        try
        {
            var provider = _providerFactory.GetProvider(account.Result.ChainType, _providerFactory.DefaultNetwork);
            return await provider.GetBalanceAsync(account.Result.Address, assetId, ct);
        }
        catch (BlockchainProviderNotFoundException)
        {
            return AZOAResult<string>.Failure(
                $"No provider is registered for chain '{account.Result.ChainType}', so the reserve balance cannot be read.");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<T>> SignAsReserveAsync<T>(
        string chainType, SigningContext signingContext, Func<byte[], Task<T>> sign)
    {
        if (string.IsNullOrWhiteSpace(chainType))
            return AZOAResult<T>.Failure("A chain is required to sign as the platform reserve.");
        if (sign is null)
            return AZOAResult<T>.Failure("A signing callback is required to sign as the platform reserve.");

        var chain = chainType.Trim();
        if (!IsChainApproved(chain))
            return AZOAResult<T>.Failure(
                $"Chain '{chain}' is not listed under {ReserveOptions.SectionName}:Chains; refusing to sign as the reserve there.");

        var undeclaredAddress = _options.RefuseUndeclaredReserveAddress(chain);
        if (undeclaredAddress is not null)
            return AZOAResult<T>.Failure(undeclaredAddress);

        var context = signingContext.ForChain(chain);
        if (!context.IsPlatform)
            return AZOAResult<T>.Failure(
                "Signing as the platform reserve requires a platform signing context.");

        return await _custody.WithPlatformSigningKeyAsync(chain, isPlatformContext: true, context, sign);
    }

    // ── Ledger ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AZOAResult<PlatformReserveLedger>> RegisterAssetAsync(
        ReserveAssetRegistration registration, CancellationToken ct = default)
    {
        if (registration is null)
            return AZOAResult<PlatformReserveLedger>.Failure("A reserve asset registration is required.");
        if (string.IsNullOrWhiteSpace(registration.AssetId))
            return AZOAResult<PlatformReserveLedger>.Failure("An asset id is required to register a reserve holding.");
        if (registration.TotalSupply <= BigInteger.Zero)
            return AZOAResult<PlatformReserveLedger>.Failure("Total supply must be a positive base-unit amount.");

        var account = await GetAccountAsync(registration.ChainType, ct);
        if (account.IsError || account.Result is null)
            return AZOAResult<PlatformReserveLedger>.Failure(account.Message);

        var network = _providerFactory.DefaultNetwork.ToString();
        var now = DateTimeOffset.UtcNow;
        var row = ReserveAssetRegistrationPlan.Create(registration with
        {
            ChainType = account.Result.ChainType, AssetId = registration.AssetId.Trim(),
            TenantId = registration.TenantId == Guid.Empty ? null : registration.TenantId,
        }, account.Result, network, null, now);

        var claim = await _ledgerStore.CreateIfAbsentAsync(row, ct);
        if (claim.IsError || claim.Result is null)
            return AZOAResult<PlatformReserveLedger>.Failure(claim.Message);
        if (!ReserveAssetRegistrationPlan.SameIdentity(claim.Result.Row, row))
            return AZOAResult<PlatformReserveLedger>.Failure("Reserve registration conflicts with the existing asset identity.");

        return AZOAResult<PlatformReserveLedger>.Success(
            claim.Result.Row,
            claim.Result.Created
                ? "Reserve ledger row created."
                : "Reserve ledger row already existed; returning it unchanged.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<PlatformReserveLedger>> GetLedgerAsync(
        string chainType, string assetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            return AZOAResult<PlatformReserveLedger>.Failure("An asset id is required to read a reserve ledger row.");

        var recordId = PlatformReserveLedger.RecordIdFor(
            chainType ?? string.Empty, _providerFactory.DefaultNetwork.ToString(), assetId);
        var row = await _ledgerStore.GetAsync(recordId, ct);
        if (row.IsError)
            return AZOAResult<PlatformReserveLedger>.Failure(row.Message);
        if (row.Result is null)
            return AZOAResult<PlatformReserveLedger>.FailureWithCode(
                $"No reserve ledger row exists for asset '{assetId}' on '{chainType}'.",
                AzoaErrorCodes.NotFound);

        return AZOAResult<PlatformReserveLedger>.Success(row.Result, "Success");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<PlatformReserveLedger>> DeclarePartitionsAsync(
        string chainType,
        string assetId,
        IReadOnlyDictionary<string, BigInteger> declaredByPartition,
        ReserveDrawAuthority authority,
        CancellationToken ct = default)
    {
        if (declaredByPartition is null || declaredByPartition.Count == 0)
            return AZOAResult<PlatformReserveLedger>.Failure("At least one partition declaration is required.");

        if (authority is null)
            return AZOAResult<PlatformReserveLedger>.FailureWithCode(
                $"A partition declaration on asset '{assetId}' arrived with no stated authority. " +
                "Nothing was declared.",
                AzoaErrorCodes.Forbidden);

        if (authority.IsSettlingGuardedCommitment)
            return AZOAResult<PlatformReserveLedger>.FailureWithCode(
                $"Settling a commitment cannot declare a partition on asset '{assetId}'. " +
                "Nothing was declared.",
                AzoaErrorCodes.Forbidden);

        for (var attempt = 1; attempt <= MaxCasAttempts; attempt++)
        {
            var current = await GetLedgerAsync(chainType, assetId, ct);
            if (current.IsError || current.Result is null)
                return current;

            var row = current.Result;

            var scopeRefusal = ReservePartitionScope.RefuseDeclaration(row, authority, row.AssetId, _options);
            if (scopeRefusal is not null)
                return AZOAResult<PlatformReserveLedger>.FailureWithCode(
                    scopeRefusal, AzoaErrorCodes.Forbidden);

            var partitions = ReservePartitionCodec.Decode(row.Partitions, out var decodeError);
            if (partitions is null)
                return AZOAResult<PlatformReserveLedger>.Failure(decodeError);

            var before = new Dictionary<string, ReservePartitionCounters>(partitions, StringComparer.Ordinal);

            foreach (var (rawName, declared) in declaredByPartition)
            {
                var name = ReservePartitionCodec.Normalize(rawName);
                if (string.IsNullOrEmpty(name))
                    return AZOAResult<PlatformReserveLedger>.Failure("A partition name cannot be blank.");
                if (declared < BigInteger.Zero)
                    return AZOAResult<PlatformReserveLedger>.Failure(
                        $"Partition '{name}' cannot declare a negative amount.");

                if (partitions.TryGetValue(name, out var existing))
                {
                    if (declared < existing.Committed)
                        return AZOAResult<PlatformReserveLedger>.Failure(
                            $"Partition '{name}' has already committed {existing.Committed} base units; " +
                            $"it cannot be re-declared at {declared}.");
                    partitions[name] = existing with { Declared = declared };
                }
                else
                {
                    partitions[name] = ReservePartitionCounters.Declaring(declared);
                }
            }

            if (SamePartitions(before, partitions))
                return AZOAResult<PlatformReserveLedger>.Success(
                    row, "Partitions already stand at these amounts; nothing was changed.");

            var totalDeclared = partitions.Values.Aggregate(BigInteger.Zero, (sum, p) => sum + p.Declared);
            var totalSupply = ParseBaseUnits(row.TotalSupply);
            if (totalDeclared > totalSupply)
                return AZOAResult<PlatformReserveLedger>.Failure(
                    $"Declared partitions total {totalDeclared} base units, which exceeds the asset's total supply " +
                    $"of {totalSupply}. Declare less, or the reserve would promise units it does not hold.");

            var expectedVersion = row.Version;
            row.Partitions = ReservePartitionCodec.Encode(partitions);
            row.Version = expectedVersion + 1;
            row.UpdatedAt = DateTimeOffset.UtcNow;

            var saved = await _ledgerStore.UpdateIfVersionAsync(row, expectedVersion, ct);
            if (!saved.IsError && saved.Result is not null)
                return AZOAResult<PlatformReserveLedger>.Success(saved.Result, "Partitions declared.");

            if (attempt == MaxCasAttempts)
                return AZOAResult<PlatformReserveLedger>.Failure(saved.Message);
        }

        return AZOAResult<PlatformReserveLedger>.Failure("Partition declaration lost every compare-and-swap attempt.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ReserveDrawOutcome>> DrawAsync(
        ReserveDrawRequest request, CancellationToken ct = default)
    {
        if (request is null)
            return AZOAResult<ReserveDrawOutcome>.Failure("A reserve draw request is required.");
        if (string.IsNullOrWhiteSpace(request.OperationKey))
            return AZOAResult<ReserveDrawOutcome>.Failure(
                "A reserve draw needs an operation key; without one a retry would double-count the partition.");

        var partitionName = ReservePartitionCodec.Normalize(request.Partition);
        if (string.IsNullOrEmpty(partitionName))
            return AZOAResult<ReserveDrawOutcome>.Failure("A partition name is required for a reserve draw.");

        var operationKey = request.OperationKey.Trim();

        for (var attempt = 1; attempt <= MaxCasAttempts; attempt++)
        {
            var current = await GetLedgerAsync(request.ChainType, request.AssetId, ct);
            if (current.IsError || current.Result is null)
                return AZOAResult<ReserveDrawOutcome>.Failure(current.Message);

            var row = current.Result;

            var scopeRefusal = ReservePartitionScope.Refuse(row, request.Authority, row.AssetId, _options);
            if (scopeRefusal is not null)
                return AZOAResult<ReserveDrawOutcome>.Failure(scopeRefusal);

            var partitions = ReservePartitionCodec.Decode(row.Partitions, out var decodeError);
            if (partitions is null)
                return AZOAResult<ReserveDrawOutcome>.Failure(decodeError);

            var window = row.RecentDrawKeys?.ToList() ?? new List<string>();

            if (window.Contains(operationKey, StringComparer.Ordinal))
            {
                var replayed = partitions.TryGetValue(partitionName, out var known)
                    ? known
                    : ReservePartitionCounters.Declaring(BigInteger.Zero);
                return AZOAResult<ReserveDrawOutcome>.Success(
                    new ReserveDrawOutcome(
                        row.AssetId, partitionName, request.Kind, request.Amount, replayed, row.Version, Replayed: true),
                    "Duplicate reserve draw: the original result is returned and nothing moved.");
            }

            if (!partitions.TryGetValue(partitionName, out var counters))
                return AZOAResult<ReserveDrawOutcome>.Failure(
                    $"Partition '{partitionName}' is not declared on asset '{row.AssetId}'. " +
                    "Declare it before drawing from it.");

            if (!counters.TryApply(request.Kind, request.Amount, partitionName, out var next, out var applyError))
                return AZOAResult<ReserveDrawOutcome>.Failure(applyError);

            partitions[partitionName] = next;
            window.Add(operationKey);
            while (window.Count > DrawReplayWindowSize)
                window.RemoveAt(0);

            var expectedVersion = row.Version;
            row.Partitions = ReservePartitionCodec.Encode(partitions);
            row.RecentDrawKeys = window;
            row.Version = expectedVersion + 1;
            row.UpdatedAt = DateTimeOffset.UtcNow;

            var saved = await _ledgerStore.UpdateIfVersionAsync(row, expectedVersion, ct);
            if (!saved.IsError && saved.Result is not null)
                return AZOAResult<ReserveDrawOutcome>.Success(
                    new ReserveDrawOutcome(
                        row.AssetId, partitionName, request.Kind, request.Amount, next, saved.Result.Version,
                        Replayed: false),
                    "Reserve draw applied.");

            if (attempt == MaxCasAttempts)
                return AZOAResult<ReserveDrawOutcome>.Failure(saved.Message);
        }

        return AZOAResult<ReserveDrawOutcome>.Failure("Reserve draw lost every compare-and-swap attempt.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ReserveDrawOutcome>> ReattributeReturnAsync(
        ReserveDrawRequest request, string toPartition, CancellationToken ct = default)
    {
        if (request is null || request.Kind != ReserveDrawKind.Return || request.Amount <= BigInteger.Zero)
            return AZOAResult<ReserveDrawOutcome>.Failure("A reattribution is a positive Return draw.");
        if (string.IsNullOrWhiteSpace(request.OperationKey))
            return AZOAResult<ReserveDrawOutcome>.Failure("A reattribution needs an operation key.");
        if (request.Authority is not { IsNodeOperator: true })
            return AZOAResult<ReserveDrawOutcome>.FailureWithCode(
                "Only the node's own rails may reattribute returned units.", AzoaErrorCodes.Forbidden);

        var source = ReservePartitionCodec.Normalize(request.Partition);
        var target = ReservePartitionCodec.Normalize(toPartition);
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) || source == target)
            return AZOAResult<ReserveDrawOutcome>.Failure("A reattribution needs two distinct partitions.");
        var operationKey = request.OperationKey.Trim();

        for (var attempt = 1; attempt <= MaxCasAttempts; attempt++)
        {
            var current = await GetLedgerAsync(request.ChainType, request.AssetId, ct);
            if (current.IsError || current.Result is null)
                return AZOAResult<ReserveDrawOutcome>.Failure(current.Message);
            var row = current.Result;

            var scopeRefusal = ReservePartitionScope.Refuse(row, request.Authority, row.AssetId, _options);
            if (scopeRefusal is not null)
                return AZOAResult<ReserveDrawOutcome>.Failure(scopeRefusal);
            var partitions = ReservePartitionCodec.Decode(row.Partitions, out var decodeError);
            if (partitions is null)
                return AZOAResult<ReserveDrawOutcome>.Failure(decodeError);

            var window = row.RecentDrawKeys?.ToList() ?? new List<string>();
            if (window.Contains(operationKey, StringComparer.Ordinal))
                return AZOAResult<ReserveDrawOutcome>.Success(new ReserveDrawOutcome(row.AssetId, target, request.Kind,
                    request.Amount, partitions.GetValueOrDefault(target) ?? ReservePartitionCounters.Declaring(BigInteger.Zero),
                    row.Version, Replayed: true), "Duplicate reattribution: nothing moved.");

            if (!partitions.TryGetValue(source, out var from))
                return AZOAResult<ReserveDrawOutcome>.Failure($"Partition '{source}' is not declared on asset '{row.AssetId}'.");
            if (!from.TryApply(ReserveDrawKind.Return, request.Amount, source, out var returned, out var applyError))
                return AZOAResult<ReserveDrawOutcome>.Failure(applyError);
            // The source's availability is unchanged (Declared - amount, Returned + amount); its promises stay covered.
            partitions[source] = returned with { Declared = returned.Declared - request.Amount };
            var to = partitions.GetValueOrDefault(target) ?? ReservePartitionCounters.Declaring(BigInteger.Zero);
            partitions[target] = to with { Declared = to.Declared + request.Amount };

            window.Add(operationKey);
            while (window.Count > DrawReplayWindowSize)
                window.RemoveAt(0);
            var expectedVersion = row.Version;
            row.Partitions = ReservePartitionCodec.Encode(partitions);
            row.RecentDrawKeys = window;
            row.Version = expectedVersion + 1;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            var saved = await _ledgerStore.UpdateIfVersionAsync(row, expectedVersion, ct);
            if (!saved.IsError && saved.Result is not null)
                return AZOAResult<ReserveDrawOutcome>.Success(new ReserveDrawOutcome(row.AssetId, target, request.Kind,
                    request.Amount, partitions[target], saved.Result.Version, Replayed: false), "Returned units reattributed.");
            if (attempt == MaxCasAttempts)
                return AZOAResult<ReserveDrawOutcome>.Failure(saved.Message);
        }

        return AZOAResult<ReserveDrawOutcome>.Failure("Reattribution lost every compare-and-swap attempt.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool IsChainApproved(string chain) => _options.PermitsChain(chain);

    private bool MatchesExpectedAddress(string chain, string derived, out string mismatch)
    {
        mismatch = string.Empty;

        var expected = _options.DeclaredAddressFor(chain);

        if (string.IsNullOrEmpty(expected))
            return true;

        if (string.Equals(expected, derived, StringComparison.Ordinal))
            return true;

        mismatch =
            $"The reserve address derived from the '{chain}' platform key does not match the address the operator " +
            $"declared under {ReserveOptions.SectionName}:ExpectedAddresses:{chain}. Refusing every reserve " +
            "operation on this chain until the configured key and the declared address agree.";
        return false;
    }

    /// <summary>True when a declaration left every partition's counters exactly as they were.</summary>
    private static bool SamePartitions(
        IReadOnlyDictionary<string, ReservePartitionCounters> before,
        IReadOnlyDictionary<string, ReservePartitionCounters> after)
    {
        if (before.Count != after.Count)
            return false;

        foreach (var (name, counters) in after)
        {
            if (!before.TryGetValue(name, out var previous) || previous != counters)
                return false;
        }

        return true;
    }

    private static BigInteger ParseBaseUnits(string? value) =>
        BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : BigInteger.Zero;
}

/// <summary>Derives a public reserve address from custody-supplied key bytes.</summary>
public static class PlatformReserveAddressDerivation
{
    /// <summary>Length of the Solana public key that trails the 64-byte secret.</summary>
    private const int SolanaPublicKeyLength = 32;

    /// <summary>Chains this derivation supports, for a caller-safe error message.</summary>
    public static string SupportedChainsMessage =>
        "Reserve addresses can be derived for: Algorand, Solana, Simulated.";

    /// <summary>Derives the reserve address for a chain, or null when the chain has no rule.</summary>
    public static string? Derive(string chainType, byte[] platformKey)
    {
        if (platformKey is null || platformKey.Length == 0)
            return null;

        try
        {
            if (string.Equals(chainType, "Algorand", StringComparison.OrdinalIgnoreCase))
                return new AlgorandAccount(platformKey).Address.ToString();

            if (string.Equals(chainType, "Solana", StringComparison.OrdinalIgnoreCase))
            {
                // Solnet's secret is seed ++ public key; the trailing 32 bytes ARE the address.
                if (platformKey.Length < SolanaPublicKeyLength)
                    return null;
                var publicKey = new byte[SolanaPublicKeyLength];
                Array.Copy(platformKey, platformKey.Length - SolanaPublicKeyLength, publicKey, 0, SolanaPublicKeyLength);
                return new SolanaPublicKey(publicKey).Key;
            }

            // Same rule a simulated wallet row uses, so the reserve is an ordinary sim: address.
            if (string.Equals(chainType, WalletKeyService.SimulatedChainType, StringComparison.OrdinalIgnoreCase))
                return WalletKeyService.SimulatedAddressFor(platformKey);

            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>Encodes exact partition counters using canonical partition names.</summary>
public static class ReservePartitionCodec
{
    private const string DeclaredKey = "declared";
    private const string IssuedKey = "issued";
    private const string HeldKey = "held";
    private const string ReturnedKey = "returned";

    /// <summary>Canonical partition name: trimmed, lower-cased invariantly.</summary>
    public static string Normalize(string? partitionName) =>
        partitionName?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>Serialises the partition map into the flexible object the row persists.</summary>
    public static JsonElement Encode(IReadOnlyDictionary<string, ReservePartitionCounters> partitions)
    {
        var wire = partitions.ToDictionary(
            entry => entry.Key,
            entry => new Dictionary<string, string>
            {
                [DeclaredKey] = entry.Value.Declared.ToString(CultureInfo.InvariantCulture),
                [IssuedKey] = entry.Value.Issued.ToString(CultureInfo.InvariantCulture),
                [HeldKey] = entry.Value.Held.ToString(CultureInfo.InvariantCulture),
                [ReturnedKey] = entry.Value.Returned.ToString(CultureInfo.InvariantCulture),
            },
            StringComparer.Ordinal);

        return JsonSerializer.SerializeToElement(wire);
    }

    /// <summary>Decodes partition counters or refuses malformed persisted accounting.</summary>
    public static Dictionary<string, ReservePartitionCounters>? Decode(
        JsonElement? partitions, out string error)
    {
        error = string.Empty;
        var result = new Dictionary<string, ReservePartitionCounters>(StringComparer.Ordinal);

        if (partitions is not { } element || element.ValueKind == JsonValueKind.Null ||
            element.ValueKind == JsonValueKind.Undefined)
        {
            return result;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "The reserve ledger's partitions field is not an object; refusing to read it as empty.";
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                error = $"Partition '{property.Name}' is malformed on the reserve ledger row.";
                return null;
            }

            if (!TryReadAmount(property.Value, DeclaredKey, out var declared) ||
                !TryReadAmount(property.Value, IssuedKey, out var issued) ||
                !TryReadAmount(property.Value, HeldKey, out var held) ||
                !TryReadAmount(property.Value, ReturnedKey, out var returned))
            {
                error = $"Partition '{property.Name}' carries an unreadable counter on the reserve ledger row.";
                return null;
            }

            result[Normalize(property.Name)] = new ReservePartitionCounters(declared, issued, held, returned);
        }

        return result;
    }

    private static bool TryReadAmount(JsonElement partition, string key, out BigInteger amount)
    {
        amount = BigInteger.Zero;
        if (!partition.TryGetProperty(key, out var value))
            return true;

        var raw = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };

        return raw is not null &&
               BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
    }
}

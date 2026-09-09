// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using AlgorandAccount = Algorand.Algod.Model.Account;
using SolanaPublicKey = Solnet.Wallet.PublicKey;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>
/// Operator configuration for the platform reserve. Bound from the <c>Reserve</c>
/// section; see <c>docs/NODE-HOST.md</c> §8.11.
/// </summary>
public sealed class ReserveOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Reserve";

    /// <summary>
    /// Chains the reserve is expected to operate on. A reserve operation on a chain
    /// absent from a NON-EMPTY list is refused before any key is touched, so a node
    /// cannot quietly start holding value on a chain its operator never approved. An
    /// empty list means "no restriction" and is the unconfigured default.
    /// </summary>
    public List<string> Chains { get; set; } = new();

    /// <summary>
    /// Where a fungible token this node creates is minted. <c>PlatformReserve</c> (the
    /// default) makes the reserve the creator AND the manager/reserve/freeze/clawback
    /// authority. <c>CallerWallet</c> is the legacy shape and is broken on chain: the
    /// creator would be the caller's custodial wallet while the ASA-create is signed by
    /// the platform key, so the sender and the signature disagree.
    /// </summary>
    public string FungibleMintTarget { get; set; } = ReserveMintTargets.PlatformReserve;

    /// <summary>
    /// Optional per-chain operator assertion of the reserve address. When an entry is
    /// present and non-blank, the address DERIVED from that chain's platform key must
    /// equal it exactly or every reserve operation on that chain fails closed. This is
    /// how a swapped or mistyped mnemonic is caught at first use rather than after value
    /// has moved to an account nobody expected.
    /// </summary>
    public Dictionary<string, string> ExpectedAddresses { get; set; } = new();

    /// <summary>
    /// Reserved for a future dedicated fee-payer account. It must stay <c>false</c>: no
    /// separate fee-payer key exists, so the reserve itself pays fees.
    /// </summary>
    public bool SeparateFeePayerAccount { get; set; }

    /// <summary>
    /// Who — besides the node's own rails — may draw a declared partition of the node's OWN
    /// unit, whose ledger row carries no tenant because the node minted it. One of
    /// <see cref="ReservePlatformUnitDrawPolicies"/>; an unrecognised value fails closed, and
    /// so does the DEFAULT — a node that says nothing keeps its own supply to its own rails.
    /// Under EVERY value a caller with no resolvable tenant is refused (OI-21).
    /// </summary>
    public string PlatformUnitDrawPolicy { get; set; } = ReservePlatformUnitDrawPolicies.OperatorOnly;

    /// <summary>
    /// The tenants admitted to draw the node's own unit when
    /// <see cref="PlatformUnitDrawPolicy"/> is <c>DeclaredTenants</c>. Ignored otherwise.
    /// </summary>
    public List<string> PlatformUnitDrawTenants { get; set; } = new();
}

/// <summary>Accepted values for <see cref="ReserveOptions.FungibleMintTarget"/>.</summary>
public static class ReserveMintTargets
{
    /// <summary>Mint into the platform reserve; the reserve holds all four ASA authorities.</summary>
    public const string PlatformReserve = "PlatformReserve";

    /// <summary>Legacy: mint with the caller's custodial wallet as creator. Broken on chain.</summary>
    public const string CallerWallet = "CallerWallet";
}

/// <summary>
/// The platform reserve: address resolution, sign-as-reserve, balance reads, and the
/// named-partition ledger. See <c>Services/Reserve/IPlatformReserve.cs</c> for what the
/// reserve is and why the partition check lives on the node.
/// </summary>
public sealed class PlatformReserveService : IPlatformReserve
{
    /// <summary>How many applied draw keys the replay window keeps, oldest evicted first.</summary>
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

        // Derivation runs INSIDE the custody door: the address is a by-product of proving
        // the node can actually sign as the reserve. A chain whose platform key is missing
        // or malformed therefore yields no address at all, rather than one that looks
        // fundable and can never spend.
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

        // The reserve is a platform-authority account. A caller-supplied context may still
        // carry an acting tenant + scope so the custody seam runs the live consent check
        // before decrypt; what it may NOT do is downgrade this to a per-user signature.
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
        var row = new PlatformReserveLedger
        {
            Id = PlatformReserveLedger.RecordIdFor(account.Result.ChainType, network, registration.AssetId),
            AssetId = registration.AssetId.Trim(),
            Chain = account.Result.ChainType,
            Network = network,
            ReserveAddress = account.Result.Address,
            TenantId = registration.TenantId is { } tenant && tenant != Guid.Empty
                ? tenant.ToString("N")
                : null,
            UnitName = registration.UnitName ?? string.Empty,
            Decimals = registration.Decimals,
            TotalSupply = registration.TotalSupply.ToString(CultureInfo.InvariantCulture),
            Partitions = ReservePartitionCodec.Encode(new Dictionary<string, ReservePartitionCounters>()),
            RecentDrawKeys = Array.Empty<string>(),
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var claim = await _ledgerStore.CreateIfAbsentAsync(row, ct);
        if (claim.IsError || claim.Result is null)
            return AZOAResult<PlatformReserveLedger>.Failure(claim.Message);

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
            return AZOAResult<PlatformReserveLedger>.Failure(
                $"No reserve ledger row exists for asset '{assetId}' on '{chainType}'.");

        return AZOAResult<PlatformReserveLedger>.Success(row.Result, "Success");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<PlatformReserveLedger>> DeclarePartitionsAsync(
        string chainType,
        string assetId,
        IReadOnlyDictionary<string, BigInteger> declaredByPartition,
        CancellationToken ct = default)
    {
        if (declaredByPartition is null || declaredByPartition.Count == 0)
            return AZOAResult<PlatformReserveLedger>.Failure("At least one partition declaration is required.");

        for (var attempt = 1; attempt <= MaxCasAttempts; attempt++)
        {
            var current = await GetLedgerAsync(chainType, assetId, ct);
            if (current.IsError || current.Result is null)
                return current;

            var row = current.Result;
            var partitions = ReservePartitionCodec.Decode(row.Partitions, out var decodeError);
            if (partitions is null)
                return AZOAResult<PlatformReserveLedger>.Failure(decodeError);

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
                    // Shrinking below what the partition has already promised would make the
                    // ledger claim availability that is already spoken for.
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

            // OI-20 / OI-21 — the ownership question, asked HERE because this is the one
            // place a partition counter moves. Seven seams reach this method; asking it at
            // each door instead left the exchange seam uncovered after two were fixed. The
            // row is the authority on who registered the asset, so the answer is read from
            // the ledger and never trusted off the request.
            var scopeRefusal = ReservePartitionScope.Refuse(row, request.Authority, row.AssetId, _options);
            if (scopeRefusal is not null)
                return AZOAResult<ReserveDrawOutcome>.Failure(scopeRefusal);

            var partitions = ReservePartitionCodec.Decode(row.Partitions, out var decodeError);
            if (partitions is null)
                return AZOAResult<ReserveDrawOutcome>.Failure(decodeError);

            var window = row.RecentDrawKeys?.ToList() ?? new List<string>();

            // Replay guard: a retried draw inside the window returns the counters as they
            // already stand and moves nothing. Outside the window the key is unknown and
            // the draw is applied, which is why callers keep their own idempotency anchor.
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

            // An undeclared partition fails CLOSED. Auto-creating one on first draw would
            // let a caller invent unlimited availability by naming a partition nobody
            // declared, which is exactly the oversubscription this ledger exists to refuse.
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool IsChainApproved(string chain) =>
        _options.Chains.Count == 0 ||
        _options.Chains.Any(candidate => string.Equals(candidate?.Trim(), chain, StringComparison.OrdinalIgnoreCase));

    private bool MatchesExpectedAddress(string chain, string derived, out string mismatch)
    {
        mismatch = string.Empty;
        var expected = _options.ExpectedAddresses
            .FirstOrDefault(entry => string.Equals(entry.Key?.Trim(), chain, StringComparison.OrdinalIgnoreCase))
            .Value;

        if (string.IsNullOrWhiteSpace(expected))
            return true;

        if (string.Equals(expected.Trim(), derived, StringComparison.Ordinal))
            return true;

        mismatch =
            $"The reserve address derived from the '{chain}' platform key does not match the address the operator " +
            $"declared under {ReserveOptions.SectionName}:ExpectedAddresses:{chain}. Refusing every reserve " +
            "operation on this chain until the configured key and the declared address agree.";
        return false;
    }

    private static BigInteger ParseBaseUnits(string? value) =>
        BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : BigInteger.Zero;
}

/// <summary>
/// Turns a chain's raw platform key bytes into that chain's address. It runs inside the
/// custody door, so it never reads a secret from configuration itself. A chain with no
/// rule here returns null and the caller fails closed naming the chain — the same
/// discipline <c>PlatformKeyDerivationRegistry</c> applies to the key itself.
/// </summary>
public static class PlatformReserveAddressDerivation
{
    /// <summary>Length of the Solana public key that trails the 64-byte secret.</summary>
    private const int SolanaPublicKeyLength = 32;

    /// <summary>Chains this derivation supports, for a caller-safe error message.</summary>
    public static string SupportedChainsMessage =>
        "Reserve addresses can be derived for: Algorand, Solana.";

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

            return null;
        }
        catch (Exception)
        {
            // Swallowed deliberately: chain SDKs echo key material in exception messages,
            // and nothing derived from the platform secret may escape this method.
            return null;
        }
    }
}

/// <summary>
/// Encodes and decodes the ledger's <c>partitions</c> object. Amounts round-trip as exact
/// decimal strings so a supply wider than i64 is never truncated, and partition names are
/// normalised in ONE place so a draw naming "Contributor" cannot open a second budget
/// beside "contributor".
/// </summary>
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

    /// <summary>
    /// Reads the persisted partition map. A malformed blob returns null with an error
    /// rather than an empty map: treating unreadable partitions as "no commitments" would
    /// hand a caller the entire supply.
    /// </summary>
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

/// <summary>
/// SurrealDB-backed reserve ledger. Colocated with the service because
/// <c>Providers/Stores/Surreal/</c> is outside this change's file boundary; the seam is
/// <see cref="IPlatformReserveLedgerStore"/>, so moving it later is a file move.
/// </summary>
public sealed class SurrealPlatformReserveLedgerStore : IPlatformReserveLedgerStore
{
    private const string Table = PlatformReserveLedger.SchemaNameConst;

    private readonly ISurrealExecutor _executor;

    public SurrealPlatformReserveLedgerStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<PlatformReserveLedger?>> GetAsync(
        string recordId, CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery<PlatformReserveLedger>.Key(recordId);
            var row = await _executor.QuerySingleAsync<PlatformReserveLedger>(query, ct);
            return new AZOAResult<PlatformReserveLedger?>
            {
                Result = row,
                Message = row is null ? "Not found." : "Success",
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<PlatformReserveLedger?>()
                .CaptureException(ex, $"Reserve ledger read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<PlatformReserveLedgerClaim>> CreateIfAbsentAsync(
        PlatformReserveLedger row, CancellationToken ct = default)
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

            var created = response.GetValues<PlatformReserveLedger>(0).SingleOrDefault() ?? row;
            return AZOAResult<PlatformReserveLedgerClaim>.Success(
                new PlatformReserveLedgerClaim(created, Created: true), "Created.");
        }
        catch (Exception ex)
        {
            // A CREATE that lost to an existing row is the idempotent case, not a fault —
            // but only when the row really is there now. Re-read before concluding that,
            // so a transport failure is never reported as "already registered".
            var existing = await GetAsync(row.Id, ct);
            if (!existing.IsError && existing.Result is not null)
                return AZOAResult<PlatformReserveLedgerClaim>.Success(
                    new PlatformReserveLedgerClaim(existing.Result, Created: false), "Already registered.");

            return new AZOAResult<PlatformReserveLedgerClaim>()
                .CaptureException(ex, $"Reserve ledger create failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<PlatformReserveLedger>> UpdateIfVersionAsync(
        PlatformReserveLedger row, long expectedVersion, CancellationToken ct = default)
    {
        try
        {
            // Single-winner conditional update (the ActionRun precedent): the write lands
            // only while the persisted version is still the one this caller reasoned over,
            // so two concurrent draws can never both spend the same availability.
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
                return AZOAResult<PlatformReserveLedger>.Failure(
                    $"Reserve ledger row '{row.Id}' changed underneath this update (expected version {expectedVersion}).");

            var persisted = response.GetValues<PlatformReserveLedger>(0).SingleOrDefault() ?? row;
            return AZOAResult<PlatformReserveLedger>.Success(persisted, "Saved.");
        }
        catch (Exception ex)
        {
            return new AZOAResult<PlatformReserveLedger>()
                .CaptureException(ex, $"Reserve ledger update failed: {ex.Message}");
        }
    }
}

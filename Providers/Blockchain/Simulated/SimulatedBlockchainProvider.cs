using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Base;

namespace AZOA.WebAPI.Providers.Blockchain.Simulated;

/// <summary>
/// Database-only / no-chain blockchain provider (db-only-null-provider track).
/// The <b>no-signer</b> member of the provider seam: it overrides every
/// value-moving op to return <b>deterministic, clearly-marked synthetic</b>
/// results and NEVER calls a signer or a node — there is no <c>HttpClient</c>,
/// no node URL, and no <see cref="AZOA.WebAPI.Interfaces.Signing.ITransactionSigner"/>
/// dependency anywhere in this class.
///
/// <para><b>Marker guardrail (brand/safety):</b> every synthetic address and tx
/// hash carries the reserved <see cref="SimPrefix"/> (<c>"sim:"</c>). A real
/// Algorand address is 58-char base32 and a Solana address is base58 — neither
/// alphabet contains <c>':'</c>, so a <c>sim:</c> value can never collide with a
/// settled on-chain identifier. This lets any query partition simulated rows
/// from real ones and prevents simulated data from being mistaken for settled
/// value when a tenant toggles Simulated→Live.</para>
///
/// <para><b>Determinism:</b> write ops return a tx hash that is a pure function
/// of the operation inputs (SHA-256 over a canonical concatenation), so the same
/// logical operation always yields the same hash — which is what makes the unit
/// tests assertable and what lets a caller dedupe replays.</para>
///
/// <para>Method signatures stay congruent with <see cref="BaseBlockchainProvider"/>
/// (and therefore the real providers) so a tenant can toggle Simulated↔Live
/// without changing any call site.</para>
///
/// <para><b>Balance ledger (D4 = in-memory, v1):</b> the real <c>wallet</c>
/// aggregate is balance-free by design (chain is source of truth in Live mode),
/// so this provider owns a small in-process ledger to give
/// <see cref="GetBalanceAsync"/> a coherent value after a simulated
/// mint/transfer/burn. It is deliberately non-durable (test/demo scope). A
/// durable SurrealDB <c>simulated_balance</c> table is a documented follow-up.</para>
/// </summary>
public sealed class SimulatedBlockchainProvider : BaseBlockchainProvider
{
    /// <summary>Reserved marker prefix for all simulated identifiers. Centralized
    /// so tests reference it instead of a magic string.</summary>
    public const string SimPrefix = "sim:";

    /// <summary>Prefix on every synthetic tx hash (<c>sim:tx:&lt;digest&gt;</c>).</summary>
    public const string SimTxPrefix = SimPrefix + "tx:";

    /// <summary>The marker key/value written into
    /// <see cref="BlockchainOperation.Parameters"/> so simulated rows are queryable.</summary>
    public const string SimulatedMarkerKey = "simulated";

    public override string ChainType => "Simulated";

    // (address, tokenId) → balance. tokenId "" represents the native/default unit.
    // ConcurrentDictionary keeps the ledger safe under the singleton provider's
    // concurrent request load without a lock.
    private readonly ConcurrentDictionary<(string Address, string TokenId), long> _ledger = new();

    public SimulatedBlockchainProvider(IConfiguration config, ILogger<SimulatedBlockchainProvider> logger)
        : base(config, logger)
    {
    }

    // ─── Marker helpers (deterministic, pure) ───

    /// <summary>
    /// Deterministic synthetic address: <c>sim:&lt;chain&gt;:&lt;base32-of-hash(seed)&gt;</c>.
    /// Pure function of (chain, seed). Exposed for tests/managers that need to
    /// synthesize a stable simulated address for a logical owner.
    /// </summary>
    public static string SimAddress(string chain, string seed)
    {
        var digest = Base32(Sha256(Canonical("addr", chain, seed)));
        return $"{SimPrefix}{chain.ToLowerInvariant()}:{digest}";
    }

    /// <summary>
    /// Deterministic synthetic tx hash: <c>sim:tx:&lt;base32-digest&gt;</c>. The
    /// digest is SHA-256 over a canonical concatenation of the operation inputs,
    /// so identical inputs always produce an identical hash and different inputs
    /// differ.
    /// </summary>
    public static string SimTxHash(
        string op, string walletAddress, string? tokenId, ulong amount, string? assetType)
    {
        var digest = Base32(Sha256(Canonical(
            op, walletAddress, tokenId ?? string.Empty, amount.ToString(), assetType ?? string.Empty)));
        return $"{SimTxPrefix}{digest}";
    }

    /// <summary>True for any value carrying the reserved <c>sim:</c> marker.</summary>
    public static bool IsSimulated(string? value) =>
        value is not null && value.StartsWith(SimPrefix, StringComparison.Ordinal);

    // ─── Account / Wallet ───

    /// <summary>
    /// Accepts only <c>sim:</c>-prefixed addresses as valid. Real addresses are
    /// deliberately NOT validated here (no cross-contamination): a real-looking
    /// address is reported invalid in simulated mode so it can never be silently
    /// treated as a simulated owner.
    /// </summary>
    public override Task<AZOAResult<bool>> ValidateAddressAsync(
        string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Task.FromResult(new AZOAResult<bool>
            {
                IsError = true, Result = false, Message = "Address is required"
            });

        if (!IsSimulated(address))
            return Task.FromResult(new AZOAResult<bool>
            {
                IsError = true,
                Result = false,
                Message = $"Not a simulated address (expected '{SimPrefix}' prefix); " +
                          "real addresses are not validated in simulated mode"
            });

        return Task.FromResult(Ok(true, "Valid simulated address"));
    }

    public override Task<AZOAResult<string>> GetBalanceAsync(
        string address, string? tokenId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return Task.FromResult(Error<string>("Address is required"));

        var balance = _ledger.GetValueOrDefault((address, tokenId ?? string.Empty), 0);
        return Task.FromResult(Ok(
            balance.ToString(),
            $"Simulated balance for {address}{(tokenId is null ? "" : $" / token {tokenId}")}"));
    }

    // ─── Token / Asset lifecycle (deterministic simulated successes) ───

    public override Task<AZOAResult<string>> MintAsync(
        string tokenUri, ulong amount, string assetType, string walletAddress,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(walletAddress) || amount == 0 || string.IsNullOrWhiteSpace(assetType))
            return Task.FromResult(Error<string>("Wallet address, positive amount, and asset type are required"));

        var hash = SimTxHash("mint", walletAddress, tokenUri, amount, assetType);
        // Mint credits the wallet. tokenId for a fresh mint is the synthetic hash
        // (a stable, marked identifier for the minted holding).
        Credit(walletAddress, hash, amount);
        return Task.FromResult(Ok(hash, $"Simulated mint of {amount} {assetType} to {walletAddress}"));
    }

    public override Task<AZOAResult<string>> TransferAsync(
        string tokenId, string fromAddress, string toAddress, ulong amount,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
            return Task.FromResult(Error<string>("Token ID is required"));
        if (string.IsNullOrWhiteSpace(fromAddress) || string.IsNullOrWhiteSpace(toAddress))
            return Task.FromResult(Error<string>("Sender and recipient addresses are required"));
        if (amount == 0)
            return Task.FromResult(Error<string>("Transfer amount must be positive"));

        var hash = SimTxHash("transfer", fromAddress, tokenId, amount, toAddress);
        // Move the holding deterministically; the ledger can go negative for the
        // sender in demo mode (no settlement guarantees), which is acceptable for
        // the test/demo scope and keeps the op deterministic regardless of order.
        Debit(fromAddress, tokenId, amount);
        Credit(toAddress, tokenId, amount);
        return Task.FromResult(Ok(hash, $"Simulated transfer of {amount} (token {tokenId}) {fromAddress} → {toAddress}"));
    }

    public override Task<AZOAResult<string>> BurnAsync(
        string tokenId, ulong amount, string walletAddress,
        SigningContext? signingContext = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
            return Task.FromResult(Error<string>("Token ID is required"));
        if (string.IsNullOrWhiteSpace(walletAddress))
            return Task.FromResult(Error<string>("Wallet address is required"));
        if (amount == 0)
            return Task.FromResult(Error<string>("Burn amount must be positive"));

        var hash = SimTxHash("burn", walletAddress, tokenId, amount, null);
        Debit(walletAddress, tokenId, amount);
        return Task.FromResult(Ok(hash, $"Simulated burn of {amount} (token {tokenId}) from {walletAddress}"));
    }

    // ─── Transaction status ───

    /// <summary>
    /// Any <c>sim:tx:</c>-prefixed hash reports as confirmed
    /// (<see cref="OperationStatus.Completed"/>) with NO network call. A
    /// non-simulated hash is rejected so a real hash can never be reported
    /// "confirmed" by the simulated provider.
    /// </summary>
    public override Task<AZOAResult<Dictionary<string, object>>> GetTransactionStatusAsync(
        string txHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(txHash))
            return Task.FromResult(Error<Dictionary<string, object>>("Transaction hash is required"));

        if (!txHash.StartsWith(SimTxPrefix, StringComparison.Ordinal))
            return Task.FromResult(Error<Dictionary<string, object>>(
                $"Not a simulated transaction hash (expected '{SimTxPrefix}' prefix)"));

        var status = new Dictionary<string, object>
        {
            ["txHash"] = txHash,
            ["chain"] = ChainType,
            ["confirmed"] = true,
            ["status"] = OperationStatus.Completed,
            [SimulatedMarkerKey] = true,
            ["fetchedAt"] = DateTime.UtcNow
        };
        return Task.FromResult(Ok(status, "Simulated transaction confirmed (no network call)"));
    }

    // ─── Chain info ───

    public override Task<AZOAResult<Dictionary<string, object>>> GetChainInfoAsync(
        CancellationToken ct = default)
    {
        var info = new Dictionary<string, object>
        {
            ["chain"] = ChainType,
            ["network"] = ActiveNetwork.ToString(),
            [SimulatedMarkerKey] = true,
            ["description"] = "Simulated (database-only) provider — no chain, no signer, no network I/O. " +
                              "All addresses and tx hashes carry the 'sim:' marker.",
            ["time"] = DateTime.UtcNow
        };
        return Task.FromResult(Ok(info, "Simulated chain info"));
    }

    // ─── Ledger primitives ───

    private void Credit(string address, string tokenId, ulong amount)
    {
        // checked: a simulated amount above long.MaxValue is impossible-balance —
        // throw loudly rather than silently inverting the ledger via wraparound.
        var delta = checked((long)amount);
        _ledger.AddOrUpdate((address, tokenId), delta, (_, cur) => cur + delta);
    }

    private void Debit(string address, string tokenId, ulong amount)
    {
        // checked: see Credit — an overflowing ulong must throw, not debit a credit.
        var delta = checked((long)amount);
        _ledger.AddOrUpdate((address, tokenId), -delta, (_, cur) => cur - delta);
    }

    // ─── Deterministic digest helpers ───

    private static string Canonical(params string[] parts) => string.Join("", parts);

    private static byte[] Sha256(string input) =>
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));

    // RFC 4648 base32 (no padding), lowercase. Used purely as a stable, opaque,
    // alphanumeric digest representation — independent of any chain's encoding.
    private static string Base32(byte[] data)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(alphabet[(buffer >> bitsLeft) & 0x1f]);
            }
        }
        if (bitsLeft > 0)
            sb.Append(alphabet[(buffer << (5 - bitsLeft)) & 0x1f]);
        return sb.ToString();
    }
}

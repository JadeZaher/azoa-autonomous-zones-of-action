using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services;

/// <summary>
/// Wormhole bridge adapter — integrates with the Guardian network REST API
/// to fetch and verify VAAs, and coordinates with blockchain providers to
/// initiate/redeem trustless cross-chain transfers.
///
/// Flow:
/// 1. Source chain provider publishes a Wormhole message (lock/transfer)
/// 2. Guardian network observes and signs a VAA
/// 3. This adapter fetches + verifies the VAA
/// 4. Target chain provider submits the VAA to complete the transfer
/// </summary>
public class WormholeAdapter : IWormholeAdapter
{
    private readonly HttpClient _guardianClient;
    private readonly IBlockchainProviderFactory _providerFactory;
    private readonly WormholeConfig _config;
    private readonly ILogger<WormholeAdapter> _logger;

    /// <summary>
    /// Optional real secp256k1 signature verifier. When null AND
    /// <see cref="WormholeConfig.RequireFullSignatureVerification"/> is true,
    /// <see cref="VerifyVAAAsync"/> fails closed (refuses to trust any VAA).
    /// </summary>
    private readonly IVaaSignatureVerifier? _signatureVerifier;

    /// <summary>Config-driven network for provider resolution (see Services/AGENTS.md §bridge).</summary>
    private readonly ChainNetwork _network;

    public WormholeAdapter(
        HttpClient guardianClient,
        IBlockchainProviderFactory providerFactory,
        IOptions<WormholeConfig> config,
        ILogger<WormholeAdapter> logger,
        IConfiguration appConfig,
        IVaaSignatureVerifier? signatureVerifier = null)
    {
        _guardianClient = guardianClient;
        _providerFactory = providerFactory;
        _config = config.Value;
        _logger = logger;
        _signatureVerifier = signatureVerifier;
        // Single config-driven network for provider resolution — mirrors
        // CrossChainBridgeService so lock (source) and redeem (target) resolve
        // the SAME network the bridge stamped, never a hardcoded Devnet.
        _network = Enum.TryParse<ChainNetwork>(
            appConfig.GetValue<string>("Blockchain:DefaultNetwork"), ignoreCase: true, out var net)
            ? net : ChainNetwork.Devnet;
    }

    public async Task<AZOAResult<WormholeTransferInitiation>> InitiateTransferAsync(
        string sourceChain, string targetChain,
        string tokenId, string senderAddress, string recipientAddress,
        ulong amount, CancellationToken ct = default)
    {
        if (!IsRouteSupported(sourceChain, targetChain))
            return Error<WormholeTransferInitiation>(
                $"Wormhole route {sourceChain}→{targetChain} is not supported");

        var sourceMapping = _config.ChainMappings[sourceChain];
        var targetMapping = _config.ChainMappings[targetChain];

        var sourceProvider = _providerFactory.GetProvider(sourceChain, _network);

        // Lock the asset on the source chain via the Wormhole Core Bridge.
        // The provider's LockForBridgeAsync should publish a Wormhole message
        // and return the tx hash. The sequence number is derived from the tx.
        var lockResult = await sourceProvider.LockForBridgeAsync(
            tokenId, sourceMapping.CoreBridgeAddress, amount,
            targetChain, recipientAddress, ct);

        if (lockResult.IsError)
            return Error<WormholeTransferInitiation>($"Source lock failed: {lockResult.Message}");

        // In production, parse the actual sequence from the on-chain tx logs.
        // For now, derive a deterministic sequence from the tx hash.
        var sequence = DeriveSequenceFromTxHash(lockResult.Result!);

        var initiation = new WormholeTransferInitiation
        {
            TxHash = lockResult.Result!,
            EmitterChainId = sourceMapping.WormholeChainId,
            EmitterAddress = NormalizeEmitterAddress(sourceMapping.CoreBridgeAddress),
            Sequence = sequence
        };

        _logger.LogInformation(
            "Wormhole transfer initiated: {SourceChain}→{TargetChain} seq={Sequence} tx={TxHash}",
            sourceChain, targetChain, sequence, lockResult.Result);

        return Ok(initiation);
    }

    public async Task<AZOAResult<WormholeVAA>> FetchVAAAsync(
        int emitterChainId, string emitterAddress, long sequence,
        CancellationToken ct = default)
    {
        var endpoint = $"/v1/signed_vaa/{emitterChainId}/{emitterAddress}/{sequence}";
        var deadline = DateTime.UtcNow.AddSeconds(_config.VaaTimeoutSeconds);

        _logger.LogInformation(
            "Polling for VAA: chain={ChainId} emitter={Emitter} seq={Sequence}",
            emitterChainId, emitterAddress, sequence);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var response = await _guardianClient.GetAsync(endpoint, ct);

                if (response.IsSuccessStatusCode)
                {
                    var envelope = await response.Content.ReadFromJsonAsync<GuardianVAAEnvelope>(cancellationToken: ct);
                    if (envelope?.VaaBytes is { } vaaBytes)
                    {
                        var vaa = ParseVAA(vaaBytes, emitterChainId, emitterAddress, sequence);
                        _logger.LogInformation("VAA fetched: seq={Sequence} signatures={Sigs}",
                            sequence, vaa.SignatureCount);
                        return Ok(vaa);
                    }
                }

                if ((int)response.StatusCode == 404)
                {
                    // VAA not yet available — Guardians haven't reached consensus
                    await Task.Delay(_config.VaaPollIntervalMs, ct);
                    continue;
                }

                return Error<WormholeVAA>(
                    $"Guardian API error: {response.StatusCode} for seq={sequence}");
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Guardian API request failed, retrying...");
                await Task.Delay(_config.VaaPollIntervalMs, ct);
            }
        }

        return Error<WormholeVAA>(
            $"VAA fetch timed out after {_config.VaaTimeoutSeconds}s for seq={sequence}");
    }

    public async Task<AZOAResult<bool>> VerifyVAAAsync(WormholeVAA vaa, CancellationToken ct = default)
    {
        // ─── 1. Basic presence / version ───
        if (string.IsNullOrWhiteSpace(vaa.VaaBytes))
            return Error<bool>("VAA bytes are empty");

        if (vaa.Version != 1)
            return Error<bool>($"Unsupported VAA version: {vaa.Version}");

        // ─── 2. Re-parse from the raw bytes (do NOT trust caller-set fields) ───
        // Verification must derive its facts from VaaBytes itself, otherwise a
        // caller could hand us a struct whose SignatureCount/Digest disagree
        // with the wire bytes that were (or were not) actually signed.
        WormholeVAA parsed;
        try
        {
            parsed = ParseVAA(vaa.VaaBytes, vaa.EmitterChainId, vaa.EmitterAddress, vaa.Sequence);
        }
        catch (Exception ex)
        {
            return Error<bool>($"VAA could not be parsed from bytes: {ex.Message}", ex);
        }

        if (!parsed.StructurallyParsed)
            return Error<bool>(
                "VAA is structurally invalid (truncated header/body) — refusing to trust");

        if (parsed.Version != 1)
            return Error<bool>($"Unsupported parsed VAA version: {parsed.Version}");

        // ─── 3. Signature count vs configured minimum ───
        int sigCount = parsed.Signatures.Count;
        if (sigCount != parsed.SignatureCount)
            return Error<bool>(
                $"VAA signature count mismatch: header says {parsed.SignatureCount}, parsed {sigCount}");

        if (sigCount < _config.MinGuardianSignatures)
            return Error<bool>(
                $"Insufficient Guardian signatures: {sigCount}/{_config.MinGuardianSignatures}");

        // ─── 4. Byzantine quorum (2/3 + 1) when Guardian-set size is known ───
        if (_config.ExpectedGuardianSetSize is { } setSize && setSize > 0)
        {
            int quorum = (setSize * 2 / 3) + 1;
            if (sigCount < quorum)
                return Error<bool>(
                    $"VAA below Wormhole quorum: {sigCount}/{quorum} (guardian set size {setSize})");

            foreach (var s in parsed.Signatures)
            {
                if (s.GuardianIndex < 0 || s.GuardianIndex >= setSize)
                    return Error<bool>(
                        $"Guardian index {s.GuardianIndex} out of range for set size {setSize}");
            }
        }

        // ─── 5. Strictly increasing, unique signer indices ───
        // Wormhole requires Guardian signatures ordered by ascending guardian
        // index with no duplicates. A duplicate or out-of-order index is a
        // forgery / replay-padding signal and MUST reject the VAA.
        int previousIndex = -1;
        foreach (var s in parsed.Signatures)
        {
            if (s.GuardianIndex < 0)
                return Error<bool>($"Negative Guardian signature index: {s.GuardianIndex}");

            if (s.GuardianIndex <= previousIndex)
                return Error<bool>(
                    "Guardian signature indices are not strictly increasing/unique " +
                    $"(saw {s.GuardianIndex} after {previousIndex}) — possible forgery/replay");

            if (s.R.Length != 32 || s.S.Length != 32)
                return Error<bool>(
                    $"Malformed signature for guardian {s.GuardianIndex} (r/s not 32 bytes)");

            previousIndex = s.GuardianIndex;
        }

        // ─── 6. Emitter / sequence well-formedness ───
        if (parsed.EmitterChainId <= 0)
            return Error<bool>($"Invalid emitter chain id: {parsed.EmitterChainId}");

        if (string.IsNullOrWhiteSpace(parsed.EmitterAddress) ||
            IsAllZeroHex(parsed.EmitterAddress))
            return Error<bool>("VAA emitter address is empty or all-zero");

        if (parsed.Sequence < 0)
            return Error<bool>($"Negative VAA sequence: {parsed.Sequence}");

        // ─── 7. Canonical digest must be present (keccak256(keccak256(body))) ───
        if (string.IsNullOrWhiteSpace(parsed.Digest))
            return Error<bool>("Canonical VAA body digest could not be computed");

        // ─── 8. Cryptographic per-signature verification (FAIL CLOSED) ───
        // Everything above is structural and CANNOT establish that the
        // Guardians actually signed this body. That requires secp256k1
        // ecrecover, which is not bundled in this project's dependencies.
        if (_signatureVerifier is null)
        {
            if (_config.RequireFullSignatureVerification)
            {
                return Error<bool>(
                    "secp256k1 signature verification not available — refusing to " +
                    "treat VAA as trusted. Register an IVaaSignatureVerifier or " +
                    "explicitly set Blockchain:Wormhole:RequireFullSignatureVerification=false " +
                    "(NEVER do this where real value can move).");
            }

            _logger.LogWarning(
                "VAA seq={Sequence} chain={Chain} passed STRUCTURAL checks only; " +
                "per-signature secp256k1 verification SKIPPED " +
                "(RequireFullSignatureVerification=false). NOT cryptographically trusted.",
                parsed.Sequence, parsed.EmitterChainId);

            return Ok(true, "VAA structurally valid (signatures NOT cryptographically verified)");
        }

        // A real verifier is wired — every signature must independently verify
        // against the canonical digest. Any failure rejects the whole VAA.
        byte[] digestBytes = AZOA.WebAPI.Helpers.Encoding.HexToBytes(parsed.Digest!);
        int verified = 0;
        foreach (var s in parsed.Signatures)
        {
            bool ok;
            try
            {
                ok = await _signatureVerifier.VerifySignatureAsync(
                    digestBytes, s, parsed.GuardianSetIndex, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Error<bool>(
                    $"Signature verification threw for guardian {s.GuardianIndex}: {ex.Message}", ex);
            }

            if (!ok)
                return Error<bool>(
                    $"Guardian signature {s.GuardianIndex} failed secp256k1 verification");

            verified++;
        }

        if (verified < _config.MinGuardianSignatures)
            return Error<bool>(
                $"Only {verified} signatures cryptographically verified, " +
                $"need {_config.MinGuardianSignatures}");

        _logger.LogInformation(
            "VAA verified (cryptographic): seq={Sequence} chain={Chain} sigs={Sigs}/{Required} digest={Digest}",
            parsed.Sequence, parsed.EmitterChainId, verified, _config.MinGuardianSignatures, parsed.Digest);

        return Ok(true, "VAA verification passed (signatures cryptographically verified)");
    }

    /// <summary>
    /// Replay-ledger digest for a VAA: lowercase hex of <c>SHA256(VaaBytes)</c>
    /// where <c>VaaBytes</c> is the raw (base64-decoded) VAA. This is the EXACT
    /// formula the bridge service uses as its consumed-VAA uniqueness key —
    /// both sides MUST compute it identically. Distinct from the canonical
    /// keccak256(keccak256(body)) signing digest in <see cref="WormholeVAA.Digest"/>.
    /// </summary>
    public static string ComputeVaaDigest(WormholeVAA vaa)
    {
        if (vaa is null || string.IsNullOrWhiteSpace(vaa.VaaBytes))
            throw new ArgumentException("VAA has no bytes to digest", nameof(vaa));
        return ComputeVaaDigest(vaa.VaaBytes);
    }

    /// <summary>
    /// Replay-ledger digest from a base64 VAA string:
    /// lowercase hex of <c>SHA256(base64Decode(vaaBase64))</c>.
    /// </summary>
    public static string ComputeVaaDigest(string vaaBase64)
    {
        if (string.IsNullOrWhiteSpace(vaaBase64))
            throw new ArgumentException("VAA bytes are empty", nameof(vaaBase64));

        var raw = Convert.FromBase64String(vaaBase64);
        var sha = System.Security.Cryptography.SHA256.HashData(raw);
        return AZOA.WebAPI.Helpers.Encoding.ToLowerHex(sha);
    }

    public async Task<AZOAResult<WormholeRedemptionResult>> RedeemTransferAsync(
        string targetChain, WormholeVAA vaa, string recipientAddress,
        CancellationToken ct = default)
    {
        if (!_config.ChainMappings.ContainsKey(targetChain))
            return Error<WormholeRedemptionResult>($"Unknown target chain: {targetChain}");

        // Verify the VAA before redeeming
        var verifyResult = await VerifyVAAAsync(vaa, ct);
        if (verifyResult.IsError)
            return Error<WormholeRedemptionResult>($"VAA verification failed: {verifyResult.Message}");

        var targetProvider = _providerFactory.GetProvider(targetChain, _network);

        // Submit the VAA to the target chain's Token Bridge.
        // The provider's MintWrappedAsync handles the on-chain redemption.
        var mintResult = await targetProvider.MintWrappedAsync(
            $"wormhole:{vaa.EmitterChainId}",
            $"seq:{vaa.Sequence}",
            vaa.VaaBytes,   // Pass VAA bytes as the token URI for on-chain verification
            1,              // Amount is encoded in the VAA payload
            recipientAddress,
            ct);

        var redemption = new WormholeRedemptionResult
        {
            TxHash = mintResult.Result ?? "",
            Success = !mintResult.IsError,
            ErrorMessage = mintResult.IsError ? mintResult.Message : null
        };

        if (redemption.Success)
        {
            _logger.LogInformation(
                "Wormhole redemption completed: {TargetChain} tx={TxHash} seq={Sequence}",
                targetChain, redemption.TxHash, vaa.Sequence);
        }

        return redemption.Success
            ? Ok(redemption, "Wormhole transfer redeemed on target chain")
            : Error<WormholeRedemptionResult>($"Redemption failed: {redemption.ErrorMessage}");
    }

    public int? GetWormholeChainId(string azoaChainName)
    {
        return _config.ChainMappings.TryGetValue(azoaChainName, out var mapping)
            ? mapping.WormholeChainId
            : null;
    }

    public bool IsRouteSupported(string sourceChain, string targetChain)
    {
        return _config.ChainMappings.ContainsKey(sourceChain)
            && _config.ChainMappings.ContainsKey(targetChain)
            && sourceChain != targetChain;
    }

    // ─── Internal helpers ───

    /// <summary>
    /// Fully parse a Wormhole VAA from its base64 wire bytes.
    ///
    /// Wire format (Wormhole canonical):
    ///   header:
    ///     [0]              version (1 byte)
    ///     [1..4]           guardian set index (uint32, big-endian)
    ///     [5]              signature count L (1 byte)
    ///     [6 .. 6+66*L)    L signatures, 66 bytes each:
    ///                          [0]      guardian index (1 byte)
    ///                          [1..32]  r (32 bytes)
    ///                          [33..64] s (32 bytes)
    ///                          [65]     recovery id v (1 byte)
    ///   body (the signed region; digest = keccak256(keccak256(body))):
    ///     timestamp(uint32) nonce(uint32) emitterChain(uint16)
    ///     emitterAddress(32) sequence(uint64) consistencyLevel(1) payload(rest)
    ///
    /// <see cref="WormholeVAA.StructurallyParsed"/> is set true ONLY when the
    /// header AND the full fixed body prefix fit within the bytes. A truncated
    /// VAA leaves it false and MUST be rejected by callers.
    /// </summary>
    private static WormholeVAA ParseVAA(string vaaBase64, int emitterChainId, string emitterAddress, long sequence)
    {
        var b = Convert.FromBase64String(vaaBase64);

        var vaa = new WormholeVAA
        {
            VaaBytes = vaaBase64,
            EmitterChainId = emitterChainId,
            EmitterAddress = emitterAddress,
            Sequence = sequence,
            Timestamp = DateTime.UtcNow,
            StructurallyParsed = false
        };

        // ─── Header ───
        // Need at minimum: version(1) + guardianSetIndex(4) + sigCount(1) = 6.
        if (b.Length < 6)
            return vaa;

        vaa.Version = b[0];
        vaa.GuardianSetIndex = (b[1] << 24) | (b[2] << 16) | (b[3] << 8) | b[4];
        int sigCount = b[5];
        vaa.SignatureCount = sigCount;

        const int sigSize = 66; // 1 (index) + 32 (r) + 32 (s) + 1 (v)
        int sigSectionStart = 6;
        long sigSectionEnd = (long)sigSectionStart + (long)sigCount * sigSize;

        // Body must start after the signature block AND the fixed body prefix
        // must fit. Body prefix = ts(4)+nonce(4)+emitterChain(2)+emitter(32)
        // +sequence(8)+consistency(1) = 51 bytes.
        const int bodyPrefix = 4 + 4 + 2 + 32 + 8 + 1;
        if (sigSectionEnd + bodyPrefix > b.Length)
            return vaa; // truncated — leave StructurallyParsed=false

        // ─── Signatures ───
        for (int i = 0; i < sigCount; i++)
        {
            int off = sigSectionStart + i * sigSize;
            var r = new byte[32];
            var s = new byte[32];
            System.Array.Copy(b, off + 1, r, 0, 32);
            System.Array.Copy(b, off + 33, s, 0, 32);
            vaa.Signatures.Add(new WormholeVaaSignature
            {
                GuardianIndex = b[off],
                R = r,
                S = s,
                V = b[off + 65]
            });
        }

        // ─── Body ───
        int bodyStart = (int)sigSectionEnd;
        int p = bodyStart;

        uint timestamp = ReadUInt32BE(b, p); p += 4;
        uint nonce = ReadUInt32BE(b, p); p += 4;
        int bodyEmitterChain = (b[p] << 8) | b[p + 1]; p += 2;

        var emitterBytes = new byte[32];
        System.Array.Copy(b, p, emitterBytes, 0, 32);
        p += 32;

        ulong bodySequence = ReadUInt64BE(b, p); p += 8;
        byte consistency = b[p]; p += 1;

        // Prefer the values actually inside the signed body over caller hints.
        vaa.EmitterChainId = bodyEmitterChain;
        vaa.EmitterAddress = AZOA.WebAPI.Helpers.Encoding.ToLowerHex(emitterBytes);
        vaa.Sequence = (long)bodySequence;
        vaa.Nonce = nonce;
        vaa.ConsistencyLevel = consistency;
        vaa.Timestamp = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;

        if (b.Length > p)
            vaa.Payload = Convert.ToHexString(b, p, b.Length - p).ToLowerInvariant();

        // ─── Canonical signing digest: keccak256(keccak256(body)) ───
        // The body is everything from bodyStart to end of bytes.
        var body = new byte[b.Length - bodyStart];
        System.Array.Copy(b, bodyStart, body, 0, body.Length);
        var digest = Keccak256.ComputeHash(Keccak256.ComputeHash(body));
        vaa.Digest = AZOA.WebAPI.Helpers.Encoding.ToLowerHex(digest);

        vaa.StructurallyParsed = true;
        return vaa;
    }

    private static uint ReadUInt32BE(byte[] b, int o)
        => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | (uint)b[o + 3];

    private static ulong ReadUInt64BE(byte[] b, int o)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
        return v;
    }

    private static bool IsAllZeroHex(string hex)
    {
        foreach (var c in hex)
            if (c != '0' && c != 'x' && c != 'X') return false;
        return true;
    }

    /// <summary>
    /// Canonicalize an emitter address to lowercase 64-hex (`^[0-9a-f]{64}$`) — the
    /// exact shape the consumed_vaa_ledger schema ASSERT enforces. This is the single
    /// seam that produces the emitter written into the replay ledger, so canonicalizing
    /// HERE guarantees the ASSERT never false-rejects a legit redeem over mere casing or
    /// an `0x` prefix. Strips a leading `0x`, lowercases, then left-pads to 64 hex chars
    /// (e.g. Algorand app IDs / EVM checksummed hex). See Services/AGENTS.md §bridge.
    /// </summary>
    private static string NormalizeEmitterAddress(string address)
    {
        var hex = address.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex.Substring(2);
        hex = hex.ToLowerInvariant();
        if (hex.Length < 64)
            hex = hex.PadLeft(64, '0');
        return hex;
    }

    private static long DeriveSequenceFromTxHash(string txHash)
    {
        // In production, parse the sequence from the source chain's Wormhole message logs.
        // For dev/testing, derive a deterministic sequence from the tx hash.
        var hash = txHash.GetHashCode(StringComparison.Ordinal);
        return Math.Abs((long)hash);
    }

    private AZOAResult<T> Ok<T>(T result, string message = "")
        => new() { IsError = false, Result = result, Message = message };

    private AZOAResult<T> Error<T>(string message, Exception? ex = null)
    {
        _logger.LogError(ex, "Wormhole error: {Message}", message);
        return new AZOAResult<T> { IsError = true, Message = message, Exception = ex };
    }
}

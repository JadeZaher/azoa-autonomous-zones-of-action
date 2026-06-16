// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models.Idempotency;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Managers;

/// <summary>
/// Composes the existing KYC gate, wallet provisioning, and mint/transfer
/// primitives into one idempotent, KYC-gated, tenant-callable allocation seam
/// (see <see cref="IAllocationManager"/>). Holds no payment-provider secret and
/// runs no economics — the fiat-settlement tenant decides the amount, supplies the
/// idempotency key; OASIS materialises the wallet and moves the asset exactly
/// once.
/// </summary>
public sealed class AllocationManager : IAllocationManager
{
    private const string OperationType = "fiat_allocation";

    private readonly IKycGateService _kycGate;
    private readonly IWalletManager _walletManager;
    private readonly IWalletStore _walletStore;
    private readonly INftManager _nftManager;
    private readonly IIdempotencyStore _idempotencyStore;

    public AllocationManager(
        IKycGateService kycGate,
        IWalletManager walletManager,
        IWalletStore walletStore,
        INftManager nftManager,
        IIdempotencyStore idempotencyStore)
    {
        _kycGate = kycGate ?? throw new ArgumentNullException(nameof(kycGate));
        _walletManager = walletManager ?? throw new ArgumentNullException(nameof(walletManager));
        _walletStore = walletStore ?? throw new ArgumentNullException(nameof(walletStore));
        _nftManager = nftManager ?? throw new ArgumentNullException(nameof(nftManager));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
    }

    /// <inheritdoc />
    public async Task<OASISResult<AllocationResult>> AllocateAsync(
        Guid avatarId,
        AllocationRequest request,
        Guid callerAvatarId,
        string? clientIdempotencyKey,
        string apiKeyId)
    {
        if (request is null)
            return Fail("Allocation request is required.");
        if (string.IsNullOrWhiteSpace(request.ChainType))
            return Fail("ChainType is required.");
        if (string.IsNullOrWhiteSpace(apiKeyId))
            return Fail("Caller API key context is required.");

        // ── Step 1: idempotency key ───────────────────────────────────────────
        // Client key wins; absent ⇒ deterministic content key over
        // (avatarId, asset descriptor, amount). NEVER a random per-request key.
        // The whole key is partitioned by apiKeyId so two tenants reusing the
        // same human-friendly key (e.g. "1") cannot collide.
        var idempotencyKey = BuildIdempotencyKey(apiKeyId, avatarId, request, clientIdempotencyKey);

        // ── Step 2: idempotency claim ─────────────────────────────────────────
        // TryClaim BEFORE any irreversible effect. On a lost claim we replay the
        // stored original result and perform NO second mint/transfer.
        var claim = await _idempotencyStore.TryClaimAsync(idempotencyKey, OperationType, CancellationToken.None);
        if (!claim.Won)
            return ReplayFromRecord(claim.Record, idempotencyKey);

        try
        {
            // ── Step 3: KYC gate (fail-closed) ────────────────────────────────
            // Per D3, the value-bearing allocation is gated. Provisioning may
            // precede approval, but we gate before generating the wallet too so
            // a rejected avatar produces NO side effect at all under a won claim.
            var gate = await _kycGate.RequireVerifiedAsync(avatarId);
            if (gate.IsError)
            {
                await _idempotencyStore.FailAsync(idempotencyKey, gate.Message, CancellationToken.None);
                return Fail(gate.Message);
            }

            // ── Step 4: provision-if-absent ───────────────────────────────────
            var (wallet, provisioned, walletError) = await EnsureWalletAsync(avatarId, request.ChainType);
            if (wallet is null)
            {
                await _idempotencyStore.FailAsync(idempotencyKey, walletError, CancellationToken.None);
                return Fail(walletError);
            }

            // ── Step 5: execute allocation (D4 discriminator) ─────────────────
            var opResult = request.Kind switch
            {
                AllocationKind.Mint => await MintAsync(avatarId, wallet, request),
                AllocationKind.Transfer => await TransferAsync(avatarId, wallet, request),
                _ => Operation.Invalid($"Unsupported allocation kind: {request.Kind}.")
            };

            if (opResult.IsError || opResult.Result is null)
            {
                var msg = opResult.IsError ? opResult.Message : "Allocation produced no operation.";
                await _idempotencyStore.FailAsync(idempotencyKey, msg, CancellationToken.None);
                return Fail(msg);
            }

            // ── Step 6: persist result for replay, then return ────────────────
            var result = new AllocationResult
            {
                AvatarId = avatarId,
                WalletId = wallet.Id,
                WalletAddress = wallet.Address,
                WalletProvisioned = provisioned,
                OperationId = opResult.Result.Id,
                Replayed = false,
                IdempotencyKey = idempotencyKey
            };

            await _idempotencyStore.CompleteAsync(
                idempotencyKey, SerializeForReplay(result), CancellationToken.None);

            return new OASISResult<AllocationResult> { Result = result, Message = "Allocation completed." };
        }
        catch (Exception ex)
        {
            // The claim is owned; mark it failed so a retry with the same key is
            // not stuck as a perpetual in-progress duplicate.
            await _idempotencyStore.FailAsync(idempotencyKey, ex.Message, CancellationToken.None);
            return new OASISResult<AllocationResult>().CaptureException(ex, "Allocation failed.");
        }
    }

    // ── Provision-if-absent ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the avatar's existing wallet for the chain, or generates one.
    /// Never duplicates (uniqueness is also guarded inside GenerateWalletAsync).
    /// </summary>
    private async Task<(IWallet? Wallet, bool Provisioned, string Error)> EnsureWalletAsync(
        Guid avatarId, string chainType)
    {
        var existing = await _walletStore.GetByAvatarAsync(avatarId);
        if (!existing.IsError && existing.Result is not null)
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

    // ── Allocation execution (consumes existing surface verbatim) ──────────────

    private async Task<OASISResult<IBlockchainOperation>> MintAsync(
        Guid avatarId, IWallet wallet, AllocationRequest request)
    {
        var mint = new NftMintRequest
        {
            WalletId = wallet.Id,
            Name = request.Name,
            Description = request.Description ?? string.Empty,
            ChainId = request.AssetId ?? request.ChainType,
            TokenId = request.AssetId,
            Metadata = MergeAmount(request)
        };
        return await _nftManager.MintAsync(mint, avatarId);
    }

    private async Task<OASISResult<IBlockchainOperation>> TransferAsync(
        Guid avatarId, IWallet wallet, AllocationRequest request)
    {
        if (request.AssetRecordId is null || request.AssetRecordId == Guid.Empty)
            return Operation.Invalid("Transfer allocation requires AssetRecordId.");

        var transfer = new NftTransferRequest
        {
            // IDOR: the transfer target is the authorised avatar, never a body id.
            TargetAvatarId = avatarId,
            WalletId = wallet.Id,
            Memo = request.Memo
        };
        return await _nftManager.TransferAsync(request.AssetRecordId.Value, transfer, avatarId);
    }

    /// <summary>
    /// Folds the already-decided amount into the asset metadata so it is recorded
    /// against the operation. OASIS treats the amount as opaque/authoritative.
    /// </summary>
    private static Dictionary<string, string> MergeAmount(AllocationRequest request)
    {
        var metadata = new Dictionary<string, string>(request.Metadata);
        if (!string.IsNullOrWhiteSpace(request.Amount))
            metadata["amount"] = request.Amount;
        return metadata;
    }

    // ── Idempotency helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Builds the partitioned idempotency key. Always prefixed with the API-key
    /// id so the dedup namespace is per-tenant. The tail is the client key when
    /// present, else a deterministic SHA-256 over the allocation content.
    /// </summary>
    private static string BuildIdempotencyKey(
        string apiKeyId, Guid avatarId, AllocationRequest request, string? clientIdempotencyKey)
    {
        var tail = !string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? clientIdempotencyKey.Trim()
            : DeterministicContentKey(avatarId, request);
        return $"alloc:{apiKeyId}:{tail}";
    }

    private static string DeterministicContentKey(Guid avatarId, AllocationRequest request)
    {
        var canonical = string.Join('|',
            avatarId.ToString("N"),
            request.Kind.ToString(),
            request.ChainType.ToLowerInvariant(),
            request.Amount,
            request.AssetId ?? string.Empty,
            request.AssetRecordId?.ToString("N") ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static OASISResult<AllocationResult> ReplayFromRecord(
        IdempotencyRecord record, string idempotencyKey)
    {
        switch (record.State)
        {
            case IdempotencyState.Completed when !string.IsNullOrEmpty(record.ResultPayload):
                var replayed = DeserializeForReplay(record.ResultPayload!);
                if (replayed is not null)
                {
                    replayed.Replayed = true;
                    return new OASISResult<AllocationResult>
                    {
                        Result = replayed,
                        Message = "Duplicate request: returning the result of the original allocation (not re-executed)."
                    };
                }
                return Fail("Duplicate request: original allocation result could not be replayed.");

            case IdempotencyState.Failed:
                return Fail(string.IsNullOrEmpty(record.Error)
                    ? "Original allocation failed."
                    : record.Error!);

            default:
                // InProgress (or Completed with no payload): the original effect
                // is not yet known to have settled. Do NOT re-execute; surface a
                // retryable in-progress state.
                return Fail(
                    $"Allocation for key '{idempotencyKey}' is already in progress; " +
                    "retry once the original request settles.");
        }
    }

    private static readonly JsonSerializerOptions ReplayJson = new(JsonSerializerDefaults.Web);

    private static string SerializeForReplay(AllocationResult result)
        => JsonSerializer.Serialize(result, ReplayJson);

    private static AllocationResult? DeserializeForReplay(string payload)
    {
        try { return JsonSerializer.Deserialize<AllocationResult>(payload, ReplayJson); }
        catch (JsonException) { return null; }
    }

    private static OASISResult<AllocationResult> Fail(string message)
        => new() { IsError = true, Message = message };

    /// <summary>Local helper for synthesising an error operation result.</summary>
    private static class Operation
    {
        public static OASISResult<IBlockchainOperation> Invalid(string message)
            => new() { IsError = true, Message = message };
    }
}

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Nethereum.Model;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private readonly IEthereumPreparationStore? _preparationStore;
    private sealed record EthereumCall(string Sender, string? Target, string Value, string Data,
        string Label, SigningContext? Context, string GasLimit, string PriorityFee, string MaxFee, string OwnerIntentJson = "",
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        EthereumCallOwner? Owner = null);
    private sealed record EthereumCallOwner(string Domain, string OperationId, string Stage, int Generation,
        Guid ActorUserId, Guid WalletId, Guid? TenantId, string RequiredScope, string IntentHash);
    private sealed record EthereumFeeAuthorization(BigInteger GasLimit, BigInteger PriorityFee, BigInteger MaxFee,
        BigInteger MaximumFee, BigInteger MaximumValue);

    private async Task<T> WithPreparationStoreAsync<T>(Func<IEthereumPreparationStore, Task<T>> action)
    {
        if (_preparationStore is not null) return await action(_preparationStore);
        if (_custodyScopeFactory is null)
            throw new InvalidOperationException("Durable Ethereum preparation storage is unavailable.");
        using var scope = _custodyScopeFactory.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IEthereumPreparationStore>());
    }

    private static string EthereumIdentity(params string[] parts)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts)))).ToLowerInvariant();

    private async Task<AZOAResult<EthereumPreparation>> PrepareCallAsync(string preparationKey,
        string sender, string? target, BigInteger value, byte[] data, string label, SigningContext context, CancellationToken ct, string ownerIntentJson = "",
        EthereumFeeAuthorization? feeAuthorization = null)
    {
        if (_signerFactory is null) return Error<EthereumPreparation>("Ethereum custody signer is unavailable.");
        var reserved = await ReserveCallAsync(preparationKey, sender, target, value, data, label, context, ct,
            ownerIntentJson, feeAuthorization);
        return reserved.IsError || reserved.Result is null ? reserved
            : await SignReservedCallAsync(reserved.Result, context, ct);
    }

    private async Task<AZOAResult<EthereumPreparation>> ReserveCallAsync(string preparationKey,
        string sender, string? target, BigInteger value, byte[] data, string label, SigningContext? context, CancellationToken ct,
        string ownerIntentJson, EthereumFeeAuthorization? feeAuthorization, EthereumCallOwner? owner = null)
    {
        if (string.IsNullOrWhiteSpace(preparationKey) || (_custodyScopeFactory is null && _preparationStore is null) ||
            context is null && (owner is null || owner.Domain != "swap" || owner.ActorUserId == Guid.Empty || owner.WalletId == Guid.Empty ||
                owner.Generation < 0 || string.IsNullOrWhiteSpace(owner.OperationId) || string.IsNullOrWhiteSpace(owner.Stage) ||
                owner.RequiredScope != AzoaScopes.SwapSign || owner.IntentHash is not { Length: 64 } ||
                owner.TenantId == Guid.Empty || feeAuthorization is null) || context is not null && owner is not null)
            return Error<EthereumPreparation>("Ethereum signing requires a durable preparation key and custody store.");
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError) return Error<EthereumPreparation>(chain.Message);
        var protocol = "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture);
        sender = sender.ToLowerInvariant();
        var id = EthereumIdentity(protocol, sender, preparationKey);
        var fingerprint = EthereumIdentity(sender, target?.ToLowerInvariant() ?? string.Empty,
            value.ToString(CultureInfo.InvariantCulture), EthereumHex.Data(data), JsonSerializer.Serialize(context), ownerIntentJson);
        if (owner is not null) fingerprint = EthereumIdentity(fingerprint, JsonSerializer.Serialize(owner), JsonSerializer.Serialize(feeAuthorization));
        return await WithPreparationStoreAsync(async store =>
        {
            var row = await store.GetAsync(id, ct);
            if (row is null)
            {
                var epoch = _config["AZOA:Ethereum:AllocatorActivationEpoch"];
                var checkpoint = await store.GetCheckpointAsync(EthereumIdentity(protocol, sender), ct);
                if (string.IsNullOrWhiteSpace(epoch) || checkpoint?.ActivationEpoch != epoch)
                    return Error<EthereumPreparation>("Ethereum signer requires a quiesced allocator activation checkpoint.");
                var fees = await GetFeeDataAsync(ct);
                if (fees.IsError || fees.Result is null) return Error<EthereumPreparation>(fees.Message);
                var gas = await EstimateGasAsync(sender, target, value, data, label, ct);
                if (gas.IsError) return Error<EthereumPreparation>(gas.Message);
                if (feeAuthorization is not null && (gas.Result > feeAuthorization.GasLimit
                    || fees.Result.MaxFeePerGas > feeAuthorization.MaxFee
                    || fees.Result.MaxPriorityFeePerGas > feeAuthorization.PriorityFee
                    || feeAuthorization.GasLimit <= 0 || feeAuthorization.PriorityFee < 0
                    || feeAuthorization.MaxFee < feeAuthorization.PriorityFee
                    || feeAuthorization.GasLimit * feeAuthorization.MaxFee > feeAuthorization.MaximumFee
                    || value > feeAuthorization.MaximumValue))
                    return Error<EthereumPreparation>("Current Ethereum cost exceeds the immutable authorization; obtain a new quote before signing.");
                var call = new EthereumCall(sender, target, value.ToString(CultureInfo.InvariantCulture), EthereumHex.Data(data),
                    label, context, (feeAuthorization?.GasLimit ?? gas.Result).ToString(CultureInfo.InvariantCulture),
                    (feeAuthorization?.PriorityFee ?? fees.Result.MaxPriorityFeePerGas).ToString(CultureInfo.InvariantCulture),
                    (feeAuthorization?.MaxFee ?? fees.Result.MaxFeePerGas).ToString(CultureInfo.InvariantCulture), ownerIntentJson, owner);
                EthereumPreparation Candidate(BigInteger value) => new()
                {
                    Id = id, ProtocolIdentity = protocol, Network = ActiveNetwork.ToString(), Sender = sender,
                    PreparationKey = preparationKey, Nonce = value.ToString("D78", CultureInfo.InvariantCulture),
                    CallFingerprint = fingerprint, IntentJson = JsonSerializer.Serialize(call),
                    OwnerUserId = (owner?.ActorUserId ?? (context!.Value.IsPlatform ? context.Value.GrantorUserId : context.Value.UserId)).ToString("N"),
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                };
                for (var attempt = 0; attempt < 32 && row is null; attempt++)
                {
                    var pending = await RpcCallAsync("eth_getTransactionCount", new object?[] { sender, "pending" }, ct);
                    if (pending.IsError || !pending.TryGetString(out var nonceText) || !EthereumHex.TryParseQuantity(nonceText, out var nonce))
                        return Error<EthereumPreparation>("Ethereum pending nonce is unavailable.");
                    var floor = BigInteger.Max(nonce, BigInteger.Parse(checkpoint.InitialNonce, CultureInfo.InvariantCulture));
                    // Released reservations were never signed or disclosed, so their nonces are provably unused: fill those holes first.
                    var released = await ReusableReleasedNonceAsync(store, protocol, sender, floor, ct);
                    if (released is not null)
                    {
                        row = await store.TryClaimReleasedAsync(released, Candidate(BigInteger.Parse(released.Nonce, CultureInfo.InvariantCulture)), ct);
                        continue;
                    }
                    var highest = await store.GetHighestNonceAsync(protocol, sender, ct);
                    if (highest is not null) nonce = BigInteger.Max(nonce, BigInteger.Parse(highest.Nonce, CultureInfo.InvariantCulture) + 1);
                    nonce = BigInteger.Max(nonce, floor);
                    if (nonce < 0 || nonce >= (BigInteger.One << 256)) return Error<EthereumPreparation>("Ethereum nonce is outside uint256.");
                    row = await store.TryClaimAsync(Candidate(nonce), ct);
                }
                if (row is null) return Error<EthereumPreparation>("Ethereum nonce admission is busy; retry the same preparation key.");
            }
            if (row.CallFingerprint != fingerprint || row.ProtocolIdentity != protocol || row.Sender != sender)
                return Error<EthereumPreparation>("Ethereum preparation key belongs to another immutable call.");
            if (row.State == "Canceled" || ReleasedReservation(row.State))
                return Error<EthereumPreparation>("Ethereum preparation was authoritatively canceled or released.");
            return Ok(row);
        });
    }

    /// <summary>Never-signed reservations that no longer own their nonce: released, consumed elsewhere, or a refilled key-lock tombstone.</summary>
    private static bool ReleasedReservation(string state) => state is "Released" or "ReleasedConsumedExternally" or "ReleasedRefilled";

    /// <summary>Lowest released nonce still at or above the chain floor; released nonces below it were consumed outside the allocator.</summary>
    private static async Task<EthereumPreparation?> ReusableReleasedNonceAsync(IEthereumPreparationStore store, string protocol, string sender,
        BigInteger floor, CancellationToken ct)
    {
        foreach (var released in await store.ListReleasedAsync(protocol, sender, 16, ct))
        {
            if (BigInteger.Parse(released.Nonce, CultureInfo.InvariantCulture) >= floor) return released;
            released.State = "ReleasedConsumedExternally";
            released.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateAsync(released, released.Version, ct);
        }
        return null;
    }

    /// <summary>Releases an unsigned, never-disclosed owner-domain reservation so the allocator can refill its nonce.</summary>
    private async Task<bool> ReleaseUnsignedReservationAsync(string reservationId, CancellationToken ct)
    {
        var row = await WithPreparationStoreAsync(store => store.GetAsync(reservationId, ct));
        if (row is not { State: "Reserved", SignedTransactionBase64: null, TransactionId: null }) return false;
        row.State = "Released";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        return await WithPreparationStoreAsync(store => store.UpdateAsync(row, row.Version, ct)) is { State: "Released" };
    }

    private async Task<AZOAResult<EthereumPreparation>> SignReservedCallAsync(EthereumPreparation reserved,
        SigningContext context, CancellationToken ct)
    {
        if (_signerFactory is null) return Error<EthereumPreparation>("Ethereum custody signer is unavailable.");
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError) return Error<EthereumPreparation>(chain.Message);
        return await WithPreparationStoreAsync(async store =>
        {
            var row = await store.GetAsync(reserved.Id, ct);
            if (row is null || row.CallFingerprint != reserved.CallFingerprint || row.ProtocolIdentity != "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture) ||
                row.Network != ActiveNetwork.ToString()) return Error<EthereumPreparation>("Ethereum reservation identity changed.");
            var admitted = JsonSerializer.Deserialize<EthereumCall>(row.IntentJson)
                ?? throw new InvalidOperationException("Ethereum admitted call is missing.");
            if (admitted.Owner is { } owner)
            {
                if (admitted.Context is not null || context.IsPlatform || !context.IsResolvableUserContext ||
                    context.UserId != owner.ActorUserId || context.WalletId != owner.WalletId || context.ChainType != ChainType ||
                    context.Scope != owner.RequiredScope || context.ActingTenantId != (owner.TenantId ?? Guid.Empty) ||
                    owner.TenantId.HasValue && context.GrantorUserId != owner.ActorUserId)
                    return Error<EthereumPreparation>("Current custody authority differs from the reserved domain owner.");
            }
            else if (admitted.Context != context)
                return Error<EthereumPreparation>("Ethereum custody context differs from the original reservation.");
            if (row.State is "Canceling" or "Canceled" or "Reverted")
                return Error<EthereumPreparation>("Ethereum reservation is canceling or terminal without the intended effect.");
            if (row.SignedTransactionBase64 is not null) return Ok(row);
            if (row.State != "Reserved") return Error<EthereumPreparation>("Ethereum reservation is no longer available for signing.");
            var transaction = new Transaction1559(chain.Result, BigInteger.Parse(row.Nonce, CultureInfo.InvariantCulture),
                BigInteger.Parse(admitted.PriorityFee, CultureInfo.InvariantCulture), BigInteger.Parse(admitted.MaxFee, CultureInfo.InvariantCulture),
                BigInteger.Parse(admitted.GasLimit, CultureInfo.InvariantCulture), admitted.Target,
                BigInteger.Parse(admitted.Value, CultureInfo.InvariantCulture), admitted.Data, accessList: null);
            var signed = await SignViaCustodyAsync(transaction.GetRLPEncodedRaw(), admitted.Label, row.Sender, context);
            if (signed.IsError || signed.Result is null) return Error<EthereumPreparation>(signed.Message);
            row.SignedTransactionBase64 = Convert.ToBase64String(signed.Result);
            row.TransactionId = EthereumHex.Data(Keccak.CalculateHash(signed.Result));
            row.State = "Prepared";
            row.UpdatedAt = DateTimeOffset.UtcNow;
            var winner = await store.UpdateAsync(row, row.Version, ct) ?? await store.GetAsync(row.Id, ct);
            return winner?.SignedTransactionBase64 is not null && winner.State is ("Prepared" or "SubmissionAuthorized" or "Confirmed") ? Ok(winner)
                : Error<EthereumPreparation>("Ethereum signing raced recovery; retry the same preparation identity.");
        });
    }

    private async Task SubmitPreparationAsync(EthereumPreparation preparation, CancellationToken ct)
    {
        var current = await WithPreparationStoreAsync(async store =>
        {
            var row = await store.GetAsync(preparation.Id, ct);
            if (row is { State: "Prepared" } && row.SignedTransactionBase64 == preparation.SignedTransactionBase64)
            {
                row.State = "SubmissionAuthorized";
                row.UpdatedAt = DateTimeOffset.UtcNow;
                return await store.UpdateAsync(row, row.Version, ct) ?? await store.GetAsync(row.Id, ct);
            }
            return row;
        });
        if (current is null || current.SignedTransactionBase64 != preparation.SignedTransactionBase64
            || current.TransactionId != preparation.TransactionId)
            throw new InvalidOperationException("Ethereum preparation is not the current authorized candidate.");
        if (current.State is "Canceling" or "Canceled" or "Confirmed" or "Reverted") return;
        if (current.State != "SubmissionAuthorized")
            throw new InvalidOperationException("Ethereum preparation is not admitted for submission.");
        var wire = Convert.FromBase64String(current.SignedTransactionBase64!);
        if (EthereumHex.Data(Keccak.CalculateHash(wire)) != current.TransactionId)
            throw new InvalidOperationException("Ethereum prepared transaction hash differs from its signed wire.");
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError || current.ProtocolIdentity != "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture)
            || current.Network != ActiveNetwork.ToString())
            throw new InvalidOperationException("Ethereum preparation belongs to another network.");
        var response = await RpcCallAsync("eth_sendRawTransaction", new object?[] { EthereumHex.Data(wire) }, ct);
        if (response.IsError) throw new HttpRequestException("Ethereum submission requires reconciliation: " + response.ErrorMessage);
        if (!response.TryGetString(out var hash) || !string.Equals(hash, current.TransactionId, StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("Ethereum node returned a mismatched transaction identity.");
    }

    private async Task<AZOAResult<ChainConfirmation>> ObservePreparationAsync(EthereumPreparation preparation, CancellationToken ct)
    {
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError || preparation.ProtocolIdentity != "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture)
            || preparation.Network != ActiveNetwork.ToString()) return Error<ChainConfirmation>("Ethereum preparation network mismatch.");
        if (string.IsNullOrWhiteSpace(preparation.TransactionId)) return Ok(ChainConfirmation.Unknown);
        var transaction = await RpcCallAsync("eth_getTransactionByHash", new object?[] { preparation.TransactionId }, ct);
        var receipt = await RpcCallAsync("eth_getTransactionReceipt", new object?[] { preparation.TransactionId }, ct);
        if (transaction.IsError || receipt.IsError) return Error<ChainConfirmation>("Ethereum transaction evidence is unavailable.");
        if (transaction.IsNullResult || receipt.IsNullResult) return Ok(ChainConfirmation.Unknown);
        var call = JsonSerializer.Deserialize<EthereumCall>(preparation.IntentJson)
            ?? throw new InvalidOperationException("Ethereum admitted call is missing.");
        var tx = transaction.Value;
        var mined = receipt.Value;
        if (!TryReadStringProperty(tx, "hash", out var hash) || !string.Equals(hash, preparation.TransactionId, StringComparison.OrdinalIgnoreCase)
            || !TryReadStringProperty(tx, "from", out var sender) || !EthereumAddress.AreSame(sender, preparation.Sender)
            || !TryReadQuantityProperty(tx, "nonce", out var nonce) || nonce != BigInteger.Parse(preparation.Nonce, CultureInfo.InvariantCulture)
            || !TryReadQuantityProperty(tx, "chainId", out var chainId) || chainId != chain.Result
            || !TryReadQuantityProperty(tx, "value", out var value) || value != BigInteger.Parse(call.Value, CultureInfo.InvariantCulture)
            || !TryReadStringProperty(tx, "input", out var data) || !string.Equals(data, call.Data, StringComparison.OrdinalIgnoreCase)
            || (call.Target is not null && (!TryReadStringProperty(tx, "to", out var target) || !EthereumAddress.AreSame(target, call.Target)))
            || (call.Target is null && tx.TryGetProperty("to", out var destination) && destination.ValueKind != JsonValueKind.Null)
            || !TryReadStringProperty(mined, "transactionHash", out var receiptHash) || !string.Equals(receiptHash, hash, StringComparison.OrdinalIgnoreCase)
            || !TryReadQuantityProperty(mined, "blockNumber", out var blockNumber)
            || !TryReadStringProperty(mined, "blockHash", out var blockHash)
            || !TryReadQuantityProperty(mined, "status", out var status) || (status != 0 && status != 1))
            return Error<ChainConfirmation>("Ethereum receipt does not prove the exact admitted transaction.");
        var block = await RpcCallAsync("eth_getBlockByNumber", new object?[] { EthereumHex.Quantity(blockNumber), false }, ct);
        var finalized = await RpcCallAsync("eth_getBlockByNumber", new object?[] { "finalized", false }, ct);
        if (block.IsError || finalized.IsError || block.IsNullResult || finalized.IsNullResult)
            return Ok(ChainConfirmation.Unknown);
        if (!TryReadStringProperty(block.Value, "hash", out var canonicalHash) || !string.Equals(canonicalHash, blockHash, StringComparison.OrdinalIgnoreCase)
            || !TryReadQuantityProperty(block.Value, "number", out var canonicalNumber) || canonicalNumber != blockNumber
            || !TryReadQuantityProperty(finalized.Value, "number", out var finalizedNumber) || finalizedNumber < blockNumber)
            return Ok(ChainConfirmation.Unknown);
        return Ok(status == 1 ? ChainConfirmation.Confirmed : ChainConfirmation.FailedOnChain);
    }
}

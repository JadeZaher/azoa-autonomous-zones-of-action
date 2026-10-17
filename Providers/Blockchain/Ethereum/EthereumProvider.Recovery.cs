using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Nethereum.Model;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider : IEthereumRecoveryModule
{
    private sealed record EthereumCancellation(string TransactionId, string SignedBase64, EthereumCall Call);

    /// <inheritdoc/>
    public async Task ReplayAuthorizedAsync(CancellationToken ct)
    {
        var rows = await WithPreparationStoreAsync(store => store.ListAuthorizedAsync(ActiveNetwork.ToString(), ct));
        foreach (var row in rows)
        {
            row.UpdatedAt = DateTimeOffset.UtcNow;
            var current = await WithPreparationStoreAsync(store => store.UpdateAsync(row, row.Version, ct));
            if (current is null) continue;
            var observation = await ObservePreparationAsync(current, ct);
            if (observation.IsError) continue;
            if (observation.Result == ChainConfirmation.Unknown)
                await SubmitPreparationAsync(current, ct);
            else
            {
                current.State = observation.Result == ChainConfirmation.Confirmed ? "Confirmed" : "Reverted";
                await WithPreparationStoreAsync(store => store.UpdateAsync(current, current.Version, ct));
            }
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> ActivateSignerAsync(string sender, Guid actor, CancellationToken ct)
    {
        if (!EthereumAddress.TryParse(sender, out _) || string.IsNullOrWhiteSpace(_config["AZOA:Ethereum:AllocatorActivationEpoch"]))
            return Error<bool>("A deployment activation epoch and valid Ethereum signer are required.");
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError) return Error<bool>(chain.Message);
        var protocol = "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture);
        sender = sender.ToLowerInvariant();
        var latest = await RpcCallAsync("eth_getTransactionCount", new object?[] { sender, "latest" }, ct);
        var pending = await RpcCallAsync("eth_getTransactionCount", new object?[] { sender, "pending" }, ct);
        var final = await RpcCallAsync("eth_getTransactionCount", new object?[] { sender, "finalized" }, ct);
        var block = await RpcCallAsync("eth_getBlockByNumber", new object?[] { "finalized", false }, ct);
        if (!latest.TryGetString(out var latestText) || !pending.TryGetString(out var pendingText) || !final.TryGetString(out var finalText)
            || !EthereumHex.TryParseQuantity(latestText, out var latestNonce) || !EthereumHex.TryParseQuantity(pendingText, out var pendingNonce)
            || !EthereumHex.TryParseQuantity(finalText, out var finalizedNonce) || latestNonce != pendingNonce || latestNonce != finalizedNonce
            || block.IsError || block.IsNullResult || !TryReadStringProperty(block.Value, "hash", out var blockHash))
            return Error<bool>("The signer has pending or unfinalized legacy work; activate only after authoritative settlement.");
        var epoch = _config["AZOA:Ethereum:AllocatorActivationEpoch"]!;
        var checkpoint = await WithPreparationStoreAsync(store => store.ClaimCheckpointAsync(new EthereumSignerCheckpoint
        {
            Id = EthereumIdentity(protocol, sender), ProtocolIdentity = protocol, Sender = sender, ActivationEpoch = epoch,
            InitialNonce = finalizedNonce.ToString(CultureInfo.InvariantCulture), BlockHash = blockHash,
            ActorUserId = actor.ToString("N"), CreatedAt = DateTimeOffset.UtcNow,
        }, ct));
        return checkpoint.ActivationEpoch == epoch ? Ok(true) : Error<bool>("Signer checkpoint belongs to another deployment epoch.");
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EthereumPreparationView>> ListPreparationsAsync(Guid owner, CancellationToken ct)
        => (await WithPreparationStoreAsync(store => store.ListOwnerAsync(owner.ToString("N"), 100, ct)))
            .Where(row => row.Network == ActiveNetwork.ToString()).Select(PreparationView).ToArray();

    /// <inheritdoc/>
    public async Task<AZOAResult<EthereumPreparationView>> RecoverPreparationAsync(string id, Guid actor, Guid tenant,
        bool platformOperator, bool cancel, string maximumFeePerGas, string reason, IReadOnlySet<string> signingScopes, CancellationToken ct)
    {
        var row = await WithPreparationStoreAsync(store => store.GetAsync(id, ct));
        if (row is null || row.Network != ActiveNetwork.ToString() || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            return Error<EthereumPreparationView>("Ethereum preparation or recovery reason is invalid.");
        var call = JsonSerializer.Deserialize<EthereumCall>(row.IntentJson) ?? throw new InvalidOperationException("Ethereum intent is missing.");
        SigningContext recoveryContext;
        if (call.Owner is { } owner)
        {
            if (call.Context is not null || owner.Domain != "swap" || owner.ActorUserId != actor || owner.WalletId == Guid.Empty ||
                !signingScopes.Contains(owner.RequiredScope) || tenant != Guid.Empty && owner.TenantId != tenant)
                return Error<EthereumPreparationView>("Current owner authority does not authorize this domain nonce recovery.");
            recoveryContext = SigningContext.ForUser(actor, owner.WalletId).ForChain(ChainType) with { Scope = owner.RequiredScope };
            if (tenant != Guid.Empty) recoveryContext = recoveryContext.ActingAs(tenant, owner.RequiredScope, actor);
        }
        else if (call.Context is { } previousContext)
        {
            if (previousContext.IsPlatform ? !platformOperator : previousContext.UserId != actor || previousContext.ActingTenantId != tenant ||
                previousContext.Scope is { Length: > 0 } scope && !signingScopes.Contains(scope))
                return Error<EthereumPreparationView>("Current signer authority does not authorize this recovery.");
            recoveryContext = previousContext;
        }
        else return Error<EthereumPreparationView>("Ethereum reservation has no explicit signer authority.");
        var original = await ObservePreparationAsync(row, ct);
        if (original.IsError) return Error<EthereumPreparationView>(original.Message);
        if (original.Result is ChainConfirmation.Confirmed or ChainConfirmation.FailedOnChain)
            return Ok(PreparationView(row) with { State = original.Result == ChainConfirmation.Confirmed ? "Confirmed" : "Reverted" });
        if (!cancel)
        {
            if (call.Owner is not null || !row.PreparationKey.StartsWith("legacy:", StringComparison.Ordinal))
                return Error<EthereumPreparationView>("Resume through the owning lifecycle or value operation.");
            var prepared = await PrepareCallAsync(row.PreparationKey, call.Sender, call.Target,
                BigInteger.Parse(call.Value, CultureInfo.InvariantCulture), Convert.FromHexString(call.Data[2..]), call.Label, recoveryContext, ct);
            if (prepared.IsError || prepared.Result is null) return Error<EthereumPreparationView>(prepared.Message);
            await SubmitPreparationAsync(prepared.Result, ct);
            return Ok(PreparationView(prepared.Result));
        }
        var candidates = JsonSerializer.Deserialize<List<EthereumCancellation>>(row.RecoveryCandidatesJson) ?? [];
        foreach (var candidate in candidates)
        {
            var probe = await ObservePreparationAsync(CancellationPreparation(row, candidate), ct);
            if (probe.IsError) return Error<EthereumPreparationView>(probe.Message);
            if (probe.Result is ChainConfirmation.Confirmed or ChainConfirmation.FailedOnChain)
            {
                row.State = "Canceled";
                row.UpdatedAt = DateTimeOffset.UtcNow;
                row = await WithPreparationStoreAsync(store => store.UpdateAsync(row, row.Version, ct)) ?? row;
                return Ok(PreparationView(row));
            }
        }
        if (!BigInteger.TryParse(maximumFeePerGas, NumberStyles.None, CultureInfo.InvariantCulture, out var maximum)
            || maximum <= 0 || maximum > _maxFeePerGasWeiCap)
            return Error<EthereumPreparationView>("An explicit allowed maximum gas price is required for cancellation.");
        var fees = await GetFeeDataAsync(ct);
        if (fees.IsError || fees.Result is null) return Error<EthereumPreparationView>(fees.Message);
        var previous = candidates.LastOrDefault()?.Call ?? call;
        var replacementMax = BigInteger.Max(fees.Result.MaxFeePerGas, BigInteger.Parse(previous.MaxFee, CultureInfo.InvariantCulture) * 113 / 100 + 1);
        var replacementTip = BigInteger.Max(fees.Result.MaxPriorityFeePerGas, BigInteger.Parse(previous.PriorityFee, CultureInfo.InvariantCulture) * 113 / 100 + 1);
        EthereumCancellation next;
        if (candidates.LastOrDefault() is { } existing && BigInteger.Parse(existing.Call.MaxFee, CultureInfo.InvariantCulture) >= fees.Result.MaxFeePerGas
            && BigInteger.Parse(existing.Call.MaxFee, CultureInfo.InvariantCulture) <= maximum)
            next = existing;
        else
        {
            if (replacementMax > maximum || replacementTip > maximum)
                return Error<EthereumPreparationView>("The authorized gas cap cannot replace the previous candidate.");
            var chain = await GetChainIdAsync(ct);
            if (chain.IsError) return Error<EthereumPreparationView>(chain.Message);
            var replacement = new EthereumCall(row.Sender, row.Sender, "0", "0x", "cancel reserved Ethereum nonce", recoveryContext,
                "21000", replacementTip.ToString(CultureInfo.InvariantCulture), replacementMax.ToString(CultureInfo.InvariantCulture));
            var transaction = new Transaction1559(chain.Result, BigInteger.Parse(row.Nonce, CultureInfo.InvariantCulture), replacementTip,
                replacementMax, 21000, row.Sender, BigInteger.Zero, "0x", accessList: null);
            var signed = await SignViaCustodyAsync(transaction.GetRLPEncodedRaw(), replacement.Label, row.Sender, recoveryContext);
            if (signed.IsError || signed.Result is null) return Error<EthereumPreparationView>(signed.Message);
            next = new(EthereumHex.Data(Keccak.CalculateHash(signed.Result)), Convert.ToBase64String(signed.Result), replacement);
            candidates.Add(next);
            row.RecoveryCandidatesJson = JsonSerializer.Serialize(candidates);
            row.State = "Canceling";
            row.RecoveryAuditJson = JsonSerializer.Serialize(new { previous = row.RecoveryAuditJson, actor, reason, at = DateTimeOffset.UtcNow, maximumFeePerGas });
            row.UpdatedAt = DateTimeOffset.UtcNow;
            var won = await WithPreparationStoreAsync(store => store.UpdateAsync(row, row.Version, ct));
            if (won is null) return Error<EthereumPreparationView>("Recovery changed concurrently; reload before submitting.");
            row = won;
        }
        var response = await RpcCallAsync("eth_sendRawTransaction", new object?[] { EthereumHex.Data(Convert.FromBase64String(next.SignedBase64)) }, ct);
        if (response.IsError) return Error<EthereumPreparationView>("Cancellation remains durable and requires reconciliation: " + response.ErrorMessage);
        if (!response.TryGetString(out var echoed) || !string.Equals(echoed, next.TransactionId, StringComparison.OrdinalIgnoreCase))
            return Error<EthereumPreparationView>("Cancellation node response has a mismatched identity.");
        return Ok(PreparationView(row));
    }

    private static EthereumPreparation CancellationPreparation(EthereumPreparation source, EthereumCancellation cancellation)
        => new() { Id = source.Id, ProtocolIdentity = source.ProtocolIdentity, Network = source.Network, Sender = source.Sender,
            Nonce = source.Nonce, TransactionId = cancellation.TransactionId, SignedTransactionBase64 = cancellation.SignedBase64,
            IntentJson = JsonSerializer.Serialize(cancellation.Call) };

    private async Task<AZOAResult<ChainConfirmation>> ObserveOwnedPreparationAsync(EthereumPreparation row, CancellationToken ct)
    {
        var original = await ObservePreparationAsync(row, ct);
        if (original.IsError || original.Result != ChainConfirmation.Unknown) return original;
        foreach (var cancellation in JsonSerializer.Deserialize<List<EthereumCancellation>>(row.RecoveryCandidatesJson) ?? [])
        {
            var observed = await ObservePreparationAsync(CancellationPreparation(row, cancellation), ct);
            if (observed.IsError) return observed;
            if (observed.Result is ChainConfirmation.Confirmed or ChainConfirmation.FailedOnChain)
                return Ok(ChainConfirmation.FailedOnChain);
        }
        return row.State is "Canceling" or "Canceled" ? Ok(ChainConfirmation.Pending) : original;
    }

    private static EthereumPreparationView PreparationView(EthereumPreparation row)
        => new(row.Id, row.Network, row.Sender, row.State, BigInteger.Parse(row.Nonce, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            row.TransactionId, row.PreparationKey, row.UpdatedAt);
}

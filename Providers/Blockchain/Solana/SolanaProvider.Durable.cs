using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Responses;
using Solnet.Programs;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record DurableSolanaPlan(string Genesis, string Blockhash, long ContextSlot, long LastValidBlockHeight,
        string Mint, ulong MintRent, byte Decimals, bool CreateDestination, string IntentHash,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        NftCostEvidence? NftCosts = null);

    private string ExpectedGenesis => _config[$"AZOA:Solana:Networks:{ActiveNetwork}:GenesisHash"] ?? ActiveNetwork switch
    {
        ChainNetwork.Mainnet => "5eykt4UsFv8P8NJdTREpY1vzqKqZKvdpKuc147dw2N9d",
        ChainNetwork.Testnet => "4uhcVJyU9pJkvQyS88uRDiswHXSCkY3zQawwpjk2NsNY",
        _ => "EtWTRABZaYq6iMfeYKouRu166VU2xqa1wcaWoxPkrZBG",
    };

    private async Task<string> VerifyGenesisAsync(CancellationToken ct)
    {
        _ = RpcHttpClient;
        var value = await DurableRpcAsync("getGenesisHash", [], ct);
        var genesis = value.GetString();
        if (genesis != ExpectedGenesis) throw new InvalidOperationException("Solana RPC genesis does not match the bound network.");
        return genesis;
    }

    private async Task<JsonElement> DurableRpcAsync(string method, object[] arguments, CancellationToken ct)
    {
        var response = await RpcReadAsync<JsonElement>(method, arguments, ct);
        if (response.Error is not null) throw new HttpRequestException($"Solana {method} failed ({response.Error.Code}).");
        return response.Result;
    }

    private async Task<DurableSolanaPlan> NewPlanAsync(string mint, ulong rent, byte decimals, bool createDestination,
        string intentHash, CancellationToken ct)
    {
        var genesis = await VerifyGenesisAsync(ct);
        var block = await DurableRpcAsync("getLatestBlockhash", [new { commitment = "finalized" }], ct);
        return new(genesis, block.GetProperty("value").GetProperty("blockhash").GetString()!,
            block.GetProperty("context").GetProperty("slot").GetInt64(),
            block.GetProperty("value").GetProperty("lastValidBlockHeight").GetInt64(), mint, rent, decimals, createDestination, intentHash);
    }

    private static byte[] CompileDurable(DurableSolanaPlan plan, PublicKey payer, IReadOnlyList<TransactionInstruction> instructions)
    {
        var builder = new TransactionBuilder().SetRecentBlockHash(plan.Blockhash).SetFeePayer(payer);
        foreach (var instruction in instructions) builder.AddInstruction(instruction);
        return builder.CompileMessage();
    }

    private async Task<AZOAResult<byte[]>> SignDurableAsync(byte[] messageBytes,
        IReadOnlyDictionary<string, SigningContext> contexts, CancellationToken ct)
    {
        if (_signerFactory?.GetSigner(ChainType) is not IPartialTransactionSigner partial)
            return AZOAResult<byte[]>.Failure("Solana detached-signature capability is unavailable.");
        var message = SolanaMessageEnvelope.Read(messageBytes);
        if (!SolanaPacketBudget.Fits(messageBytes.Length, message.RequiredSignatures))
            return AZOAResult<byte[]>.Failure("Solana transaction exceeds the network packet limit.");
        var required = message.RequiredKeys.ToArray();
        if (required.Length != contexts.Count || required.Any(key => !contexts.ContainsKey(key.Key)))
            return AZOAResult<byte[]>.Failure("Solana required signers differ from the authorized participants.");
        var signatures = new List<byte[]>();
        foreach (var publicKey in required)
        {
            ct.ThrowIfCancellationRequested();
            var context = contexts[publicKey.Key].ForChain(ChainType);
            Task<AZOAResult<byte[]>> Sign(byte[] bytes)
            {
                using var material = new SigningKeyMaterial(bytes);
                return Task.FromResult(partial.SignPartial(messageBytes, publicKey.Key, material));
            }
            AZOAResult<byte[]> signed;
            if (_custodyService is not null) signed = await SignWithCustodyAsync(_custodyService, "prepare durable transaction", context, Sign);
            else if (_custodyScopeFactory is not null)
            {
                using var scope = _custodyScopeFactory.CreateScope();
                signed = await SignWithCustodyAsync(scope.ServiceProvider.GetRequiredService<IKeyCustodyService>(), "prepare durable transaction", context, Sign);
            }
            else return AZOAResult<byte[]>.Failure("Durable Solana operations require consent-aware custody.");
            if (signed.IsError || signed.Result is null) return signed;
            if (!publicKey.Verify(messageBytes, signed.Result)) return AZOAResult<byte[]>.Failure("Solana participant signature failed verification.");
            signatures.Add(signed.Result);
        }
        return AZOAResult<byte[]>.Success(SolanaMessageEnvelope.Populate(messageBytes, signatures));
    }

    private static bool MatchesDurableWire(string wireBase64, string transactionId, byte[] expectedMessage, out byte[] wire)
    {
        wire = [];
        try
        {
            wire = Convert.FromBase64String(wireBase64);
            if (wire.Length > 1232) return false;
            var transaction = Transaction.Deserialize(wire);
            return transaction.VerifySignatures() && transaction.CompileMessage().AsSpan().SequenceEqual(expectedMessage)
                && transaction.Serialize().AsSpan().SequenceEqual(wire)
                && Encoders.Base58.EncodeData(transaction.Signatures[0].Signature) == transactionId;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IndexOutOfRangeException or OverflowException)
        { return false; }
    }

    private async Task SubmitDurableAsync(string wire, string transactionId, DurableSolanaPlan plan, CancellationToken ct)
    {
        if (await VerifyGenesisAsync(ct) != plan.Genesis) throw new InvalidOperationException("Solana prepared network changed.");
        var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
        if (height.GetInt64() > plan.LastValidBlockHeight) throw new InvalidOperationException("The original Solana transaction expired; reconcile its identity.");
        var result = await RpcCallAsync<string>("sendTransaction", [wire, new { encoding = "base64", skipPreflight = false,
            preflightCommitment = "finalized", maxRetries = 0, minContextSlot = plan.ContextSlot }], ct);
        if (result.Error is not null) throw new HttpRequestException($"Solana submission returned an unresolved RPC error ({result.Error.Code}); reconcile the original signature.");
        if (result.Result != transactionId) throw new InvalidOperationException("Solana returned a different transaction signature.");
    }

    private async Task<string> ObserveDurableAsync(string transactionId, string wireBase64, DurableSolanaPlan plan, CancellationToken ct)
    {
        if (await VerifyGenesisAsync(ct) != plan.Genesis) return "NeedsOperator";
        var statusResponse = await DurableRpcAsync("getSignatureStatuses", [new[] { transactionId }, new { searchTransactionHistory = true }], ct);
        var status = statusResponse.GetProperty("value")[0];
        if (status.ValueKind != JsonValueKind.Null && status.GetProperty("confirmationStatus").GetString() == "finalized")
        {
            var transaction = await DurableRpcAsync("getTransaction", [transactionId, new { encoding = "base64", commitment = "finalized", maxSupportedTransactionVersion = 0 }], ct);
            if (transaction.ValueKind == JsonValueKind.Null) return "NeedsOperator";
            var observedWire = transaction.GetProperty("transaction")[0].GetString();
            if (observedWire != wireBase64) return "NeedsOperator";
            return transaction.GetProperty("meta").GetProperty("err").ValueKind == JsonValueKind.Null ? "Confirmed" : "FailedOnChain";
        }
        var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
        return height.GetInt64() > plan.LastValidBlockHeight ? "NeedsOperator" : "Unseen";
    }

    private static string IntentDigest<T>(T intent) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(intent)))).ToLowerInvariant();
    private static bool CanonicalAmount(string amount, out ulong parsed) => ulong.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
        && parsed > 0 && parsed.ToString(CultureInfo.InvariantCulture) == amount;
    private static DurableSolanaPlan? ReadPlan(string? metadata)
    {
        try { return metadata is null ? null : JsonSerializer.Deserialize<DurableSolanaPlan>(metadata); }
        catch (JsonException) { return null; }
    }

    private async Task<JsonElement?> ReadParsedAccountAsync(string address, CancellationToken ct)
    {
        var result = await DurableRpcAsync("getAccountInfo", [address, new { encoding = "jsonParsed", commitment = "finalized" }], ct);
        var account = result.GetProperty("value");
        return account.ValueKind == JsonValueKind.Null ? null : account;
    }

    private static JsonElement TokenInfo(JsonElement account, string type)
    {
        if (account.GetProperty("owner").GetString() != TokenProgram.ProgramIdKey.Key || account.GetProperty("executable").GetBoolean())
            throw new InvalidOperationException("Solana account is not owned by the expected token program.");
        var parsed = account.GetProperty("data").GetProperty("parsed");
        if (parsed.GetProperty("type").GetString() != type) throw new InvalidOperationException("Solana token account type differs.");
        return parsed.GetProperty("info");
    }
}

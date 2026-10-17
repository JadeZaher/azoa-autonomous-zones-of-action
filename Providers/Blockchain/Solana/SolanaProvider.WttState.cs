using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider : IWttGuardianStateSource
{
    private sealed record WttSolanaDeployment(PublicKey Core, PublicKey TokenBridge, string CoreHash, string TokenHash);
    private const string WttSysvarOwner = "Sysvar1111111111111111111111111111111111111";

    /// <inheritdoc/>
    public async Task<AZOAResult<WttGuardianEvidence>> ReadWttGuardianEvidenceAsync(uint guardianSetIndex, ushort emitterChain,
        string emitterAddress, CancellationToken ct = default)
    {
        _ = RpcHttpClient;
        var deployment = ReadWttSolanaDeployment();
        if (deployment is null) return Error<WttGuardianEvidence>("An enabled, code-pinned Solana WTT deployment is required.");
        if (emitterChain is 0 or 1 || emitterAddress is not { Length: 64 }
            || emitterAddress.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return Error<WttGuardianEvidence>("A canonical foreign emitter is required.");
        var emitter = Convert.FromHexString(emitterAddress);
        var genesis = await VerifyGenesisAsync(ct);
        var indexSeed = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(indexSeed, guardianSetIndex);
        var chainSeed = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(chainSeed, emitterChain);
        var addresses = new[]
        {
            WttSolanaAccounts.Derive(deployment.Core, "Bridge"u8.ToArray()), WttSolanaAccounts.Derive(deployment.Core, "GuardianSet"u8.ToArray(), indexSeed),
            WttSolanaAccounts.Derive(deployment.TokenBridge, "config"u8.ToArray()), WttSolanaAccounts.Derive(deployment.TokenBridge, chainSeed, emitter),
            deployment.Core, SolanaNativeLoader.ProgramData(deployment.Core), deployment.TokenBridge,
            SolanaNativeLoader.ProgramData(deployment.TokenBridge), SysVars.ClockKey,
        };
        var response = await DurableRpcAsync("getMultipleAccounts", [addresses.Select(value => value.Key).ToArray(),
            new { encoding = "base64", commitment = "finalized" }], ct);
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("context", out var context)
            || context.ValueKind != JsonValueKind.Object || !context.TryGetProperty("slot", out var slotValue)
            || slotValue.ValueKind != JsonValueKind.Number || !slotValue.TryGetUInt64(out var slot) || !response.TryGetProperty("value", out var accounts)
            || accounts.ValueKind != JsonValueKind.Array || accounts.GetArrayLength() != addresses.Length)
            return Error<WttGuardianEvidence>("Finalized WTT account snapshot is malformed.");
        try
        {
            var bridge = SolanaAccountData.ReadOwned(accounts[0], deployment.Core.Key, false);
            var guardians = SolanaAccountData.ReadOwned(accounts[1], deployment.Core.Key, false);
            var token = SolanaAccountData.ReadOwned(accounts[2], deployment.TokenBridge.Key, false);
            var endpoint = SolanaAccountData.ReadOwned(accounts[3], deployment.TokenBridge.Key, false);
            SolanaProgramDataEvidence.Require(accounts[4], accounts[5], addresses[5], deployment.CoreHash);
            SolanaProgramDataEvidence.Require(accounts[6], accounts[7], addresses[7], deployment.TokenHash);
            var clock = SolanaAccountData.ReadOwned(accounts[8], WttSysvarOwner, false);
            if (bridge.Length != 24 || !WttSolanaStateCodec.TokenConfig(token, deployment.Core.KeyBytes, out _)
                || !WttSolanaStateCodec.Endpoint(endpoint, emitterChain, emitter)
                || !WttSolanaStateCodec.Clock(clock, slot, out var chainTime)
                || !WttSolanaStateCodec.GuardianSet(guardians, guardianSetIndex, chainTime, out var keys, out var expiry))
                return Error<WttGuardianEvidence>("WTT account identity, guardian layout or emitter registration differs.");
            var block = await DurableRpcAsync("getBlock", [slot, new { commitment = "finalized", transactionDetails = "none", rewards = false }], ct);
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("blockhash", out var hashValue) || hashValue.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(hashValue.GetString()))
                return Error<WttGuardianEvidence>("Finalized WTT snapshot block is unavailable.");
            return Ok(new WttGuardianEvidence("solana:" + genesis, deployment.Core.Key, deployment.TokenBridge.Key,
                guardianSetIndex, BinaryPrimitives.ReadUInt32LittleEndian(bridge), keys, expiry, chainTime,
                slot.ToString(CultureInfo.InvariantCulture) + ":" + hashValue.GetString(), DateTimeOffset.UtcNow, emitterChain, emitterAddress));
        }
        catch (InvalidDataException exception) { return Error<WttGuardianEvidence>(exception.Message); }
    }

    private WttSolanaDeployment? ReadWttSolanaDeployment()
    {
        var prefix = $"AZOA:Solana:Wtt:Networks:{ActiveNetwork}:";
        if (!bool.TryParse(_config[prefix + "Enabled"], out var enabled) || !enabled
            || !IsNativeHash(_config[prefix + "CoreProgramDataHash"]) || !IsNativeHash(_config[prefix + "TokenBridgeProgramDataHash"])) return null;
        try
        {
            var core = new PublicKey(_config[prefix + "CoreBridge"] ?? "");
            var token = new PublicKey(_config[prefix + "TokenBridge"] ?? "");
            return core.Key == token.Key ? null : new(core, token, _config[prefix + "CoreProgramDataHash"]!, _config[prefix + "TokenBridgeProgramDataHash"]!);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException) { return null; }
    }

}

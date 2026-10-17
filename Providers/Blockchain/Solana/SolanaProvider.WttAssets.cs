using System.Buffers.Binary;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Programs;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private const string WttNativeMint = "So11111111111111111111111111111111111111112";
    private sealed record WttSolanaSnapshot(WttSolanaDeployment Deployment, string Genesis, ulong Slot,
        uint ChainTime, ulong MessageFee, JsonElement[] Accounts);

    /// <inheritdoc cref="IWttSourceModule.InspectWttSourceAsync"/>
    public async Task<AZOAResult<(WttNetworkDescriptor Network, WttAssetDescriptor Asset)>> InspectWttSourceAsync(
        string tokenAddress, bool nativeAsset, CancellationToken ct = default)
    {
        try
        {
            var deployment = ReadWttSolanaDeployment() ?? throw new InvalidDataException("An enabled code-pinned Solana WTT deployment is required.");
            var mint = new PublicKey(tokenAddress);
            if (nativeAsset && mint.Key != WttNativeMint) throw new InvalidDataException("Native Solana value must use the canonical wrapped SOL mint.");
            var snapshot = await ReadWttSolanaSnapshotAsync(deployment,
                [mint, WttSolanaAccounts.Derive(deployment.TokenBridge, "meta"u8.ToArray(), mint.KeyBytes), SolanaNftMetadata.Metadata(mint)], ct);
            return Ok((WttDescriptor(snapshot), ReadWttSolanaAsset(snapshot, mint, nativeAsset)));
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or FormatException)
        { return Error<(WttNetworkDescriptor Network, WttAssetDescriptor Asset)>(exception.Message); }
    }

    /// <inheritdoc cref="IWttTargetModule.InspectWttTargetAsync"/>
    public async Task<AZOAResult<WttTargetInspection>> InspectWttTargetAsync(WttAssetDescriptor origin,
        string recipientAddress, bool unwrapNative, CancellationToken ct = default)
    {
        try
        {
            if (origin is null || origin.OriginChain == 0) throw new InvalidDataException("An original token identity is required.");
            var deployment = ReadWttSolanaDeployment() ?? throw new InvalidDataException("An enabled code-pinned Solana WTT deployment is required.");
            var originBytes = WttPayloadCodec.FixedHex(origin.OriginAddress, 32);
            var mint = origin.OriginChain == 1 ? new PublicKey(originBytes) : WttSolanaAccounts.WrappedMint(deployment.TokenBridge, origin.OriginChain, originBytes);
            if (unwrapNative && (origin.OriginChain != 1 || mint.Key != WttNativeMint))
                throw new InvalidDataException("Only original wrapped SOL can be unwrapped on Solana.");
            var recipient = new PublicKey(recipientAddress);
            var ata = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(recipient, mint);
            var snapshot = await ReadWttSolanaSnapshotAsync(deployment,
                [mint, WttSolanaAccounts.Derive(deployment.TokenBridge, "meta"u8.ToArray(), mint.KeyBytes), SolanaNftMetadata.Metadata(mint), ata], ct);
            if (snapshot.Accounts[0].ValueKind == JsonValueKind.Null)
            {
                if (origin.OriginChain == 1) throw new InvalidDataException("Original Solana mint is absent.");
                if (snapshot.Accounts[1].ValueKind != JsonValueKind.Null || snapshot.Accounts[3].ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException("Absent wrapped mint has inconsistent metadata or recipient holding.");
                return Ok(new WttTargetInspection(WttDescriptor(snapshot), null, (byte)Math.Min(origin.Decimals, (byte)8), false, false,
                    Convert.ToHexStringLower(ata.KeyBytes)));
            }
            var asset = ReadWttSolanaAsset(snapshot, mint, false);
            if (asset.OriginChain != origin.OriginChain || asset.OriginAddress != origin.OriginAddress)
                throw new InvalidDataException("Target mint origin differs from the source asset.");
            var ready = false;
            if (snapshot.Accounts[3].ValueKind != JsonValueKind.Null)
            {
                var holding = SolanaSplAssetCodec.ReadHolding(SolanaAccountData.ReadOwned(snapshot.Accounts[3], TokenProgram.ProgramIdKey.Key, false));
                if (holding.Mint != mint.Key || holding.Owner != recipient.Key || holding.Frozen)
                    throw new InvalidDataException("Recipient token account has another owner, mint or frozen state.");
                ready = true;
            }
            return Ok(new WttTargetInspection(WttDescriptor(snapshot), mint.Key, asset.Decimals, true, ready,
                Convert.ToHexStringLower(ata.KeyBytes)));
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or FormatException)
        { return Error<WttTargetInspection>(exception.Message); }
    }

    private async Task<WttSolanaSnapshot> ReadWttSolanaSnapshotAsync(WttSolanaDeployment deployment, PublicKey[] requested, CancellationToken ct)
    {
        var genesis = await VerifyGenesisAsync(ct);
        var fixedAccounts = new[] { deployment.Core, SolanaNativeLoader.ProgramData(deployment.Core), deployment.TokenBridge,
            SolanaNativeLoader.ProgramData(deployment.TokenBridge), WttSolanaAccounts.Derive(deployment.Core, "Bridge"u8.ToArray()),
            WttSolanaAccounts.Derive(deployment.TokenBridge, "config"u8.ToArray()), SysVars.ClockKey };
        var addresses = fixedAccounts.Concat(requested).Select(value => value.Key).ToArray();
        var response = await DurableRpcAsync("getMultipleAccounts", [addresses, new { encoding = "base64", commitment = "finalized" }], ct);
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("context", out var context)
            || context.ValueKind != JsonValueKind.Object || !context.TryGetProperty("slot", out var slotValue)
            || slotValue.ValueKind != JsonValueKind.Number || !slotValue.TryGetUInt64(out var slot)
            || !response.TryGetProperty("value", out var accounts) || accounts.ValueKind != JsonValueKind.Array || accounts.GetArrayLength() != addresses.Length)
            throw new InvalidDataException("Solana WTT snapshot is malformed.");
        SolanaProgramDataEvidence.Require(accounts[0], accounts[1], fixedAccounts[1], deployment.CoreHash);
        SolanaProgramDataEvidence.Require(accounts[2], accounts[3], fixedAccounts[3], deployment.TokenHash);
        var bridge = SolanaAccountData.ReadOwned(accounts[4], deployment.Core.Key, false);
        var config = SolanaAccountData.ReadOwned(accounts[5], deployment.TokenBridge.Key, false);
        if (bridge.Length != 24 || !WttSolanaStateCodec.TokenConfig(config, deployment.Core.KeyBytes, out var paused) || paused
            || !WttSolanaStateCodec.Clock(SolanaAccountData.ReadOwned(accounts[6], WttSysvarOwner, false), slot, out var time))
            throw new InvalidDataException("Solana WTT configuration or snapshot clock is inconsistent.");
        return new(deployment, genesis, slot, time, BinaryPrimitives.ReadUInt64LittleEndian(bridge.AsSpan(16)),
            accounts.EnumerateArray().Skip(fixedAccounts.Length).Select(value => value.Clone()).ToArray());
    }

    private WttNetworkDescriptor WttDescriptor(WttSolanaSnapshot snapshot) => new("Solana", ActiveNetwork,
        "solana:" + snapshot.Genesis, ActiveNetwork.ToString(), 1,
        snapshot.Deployment.Core.Key, snapshot.Deployment.TokenBridge.Key,
        CanonicalJson.Hash(new { snapshot.Deployment.CoreHash, snapshot.Deployment.TokenHash }),
        new(1, Convert.ToHexStringLower(Encoders.Base58.DecodeData(snapshot.Genesis)),
            Convert.ToHexStringLower(snapshot.Deployment.Core.KeyBytes), Convert.ToHexStringLower(snapshot.Deployment.TokenBridge.KeyBytes)),
        Convert.ToHexStringLower(WttSolanaAccounts.Derive(snapshot.Deployment.TokenBridge, "emitter"u8.ToArray()).KeyBytes));

    private static WttAssetDescriptor ReadWttSolanaAsset(WttSolanaSnapshot snapshot, PublicKey mint, bool nativeAsset)
    {
        var info = SolanaSplAssetCodec.ReadMint(SolanaAccountData.ReadOwned(snapshot.Accounts[0], TokenProgram.ProgramIdKey.Key, false));
        ushort originChain = 1; var originAddress = Convert.ToHexStringLower(mint.KeyBytes); byte? originDecimals = info.Decimals;
        var wrapped = snapshot.Accounts[1].ValueKind != JsonValueKind.Null;
        if (wrapped)
        {
            var meta = WttSolanaAssetCodec.ReadWrapped(SolanaAccountData.ReadOwned(snapshot.Accounts[1], snapshot.Deployment.TokenBridge.Key, false));
            if (nativeAsset || WttSolanaAccounts.WrappedMint(snapshot.Deployment.TokenBridge, meta.Chain, meta.Address).Key != mint.Key
                || info.Decimals != Math.Min(meta.OriginalDecimals, (byte)8)
                || info.MintAuthority != WttSolanaAccounts.Derive(snapshot.Deployment.TokenBridge, "mint_signer"u8.ToArray()).Key)
                throw new InvalidDataException("Wrapped mint authority, origin PDA or precision differs from the bridge.");
            originChain = meta.Chain; originAddress = Convert.ToHexStringLower(meta.Address); originDecimals = meta.OriginalDecimals;
        }
        string symbol = new('0', 64), name = new('0', 64);
        if (snapshot.Accounts[2].ValueKind != JsonValueKind.Null)
        {
            var metadata = SolanaNativeMetadata.Read(SolanaAccountData.ReadOwned(snapshot.Accounts[2], SolanaNftMetadata.Program.Key, false));
            if (metadata.Mint != mint.Key) throw new InvalidDataException("Token metadata belongs to another mint.");
            symbol = WttText32(metadata.Symbol); name = WttText32(metadata.Name);
        }
        return new(mint.Key, nativeAsset, info.Decimals, originChain, originAddress, originDecimals, symbol, name,
            CanonicalJson.Hash(new { Kind = wrapped ? "WttWrapped" : "LegacySpl", Program = TokenProgram.ProgramIdKey.Key,
                info.MintAuthority, info.FreezeAuthority, info.Decimals, originChain, originAddress }));
    }

    private static string WttText32(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value.TrimEnd('\0')); var result = new byte[32];
        bytes.AsSpan(0, Math.Min(bytes.Length, result.Length)).CopyTo(result); return Convert.ToHexStringLower(result);
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IWttSourceModule.InspectWttSourceAsync"/>
    public async Task<AZOAResult<(WttNetworkDescriptor Network, WttAssetDescriptor Asset)>> InspectWttSourceAsync(
        string tokenAddress, bool nativeAsset, CancellationToken ct = default)
    {
        try
        {
            var snapshot = await ReadWttAlgoSnapshotAsync(null, 0, null, ct);
            var asset = await ReadWttAlgoAssetAsync(snapshot, tokenAddress, nativeAsset, ct);
            await RequireWttAlgoRoundAsync(snapshot.Round, ct);
            return AZOAResult<(WttNetworkDescriptor Network, WttAssetDescriptor Asset)>.Success((WttAlgoDescriptor(snapshot), asset));
        }
        catch (InvalidDataException exception)
        { return AZOAResult<(WttNetworkDescriptor Network, WttAssetDescriptor Asset)>.Failure(exception.Message); }
        catch (JsonException)
        { return AZOAResult<(WttNetworkDescriptor Network, WttAssetDescriptor Asset)>.Failure("Algorand WTT asset state is malformed."); }
    }

    private WttNetworkDescriptor WttAlgoDescriptor(WttAlgoSnapshot snapshot)
    {
        var deployment = snapshot.Deployment;
        return new("Algorand", ActiveNetwork, "algorand:" + deployment.Genesis, ActiveNetwork.ToString(), 8,
            deployment.Core.ToString(CultureInfo.InvariantCulture), deployment.Token.ToString(CultureInfo.InvariantCulture),
            CanonicalJson.Hash(deployment), new(8, Convert.ToHexStringLower(Convert.FromBase64String(deployment.Genesis)),
                WttAlgoAssetAddress(deployment.Core), WttAlgoAssetAddress(deployment.Token)),
            Convert.ToHexStringLower(global::Algorand.Address.ForApplication(deployment.Token).Bytes));
    }

    /// <inheritdoc cref="IWttTargetModule.InspectWttTargetAsync"/>
    public async Task<AZOAResult<WttTargetInspection>> InspectWttTargetAsync(WttAssetDescriptor origin,
        string recipientAddress, bool unwrapNative, CancellationToken ct = default)
    {
        if (origin is null || origin.OriginChain == 0 || origin.OriginAddress is not { Length: 64 }
            || !origin.OriginAddress.All(char.IsAsciiHexDigitLower) || !global::Algorand.Address.IsValid(recipientAddress))
            return AZOAResult<WttTargetInspection>.Failure("Canonical asset origin and Algorand recipient are required.");
        var recipient = new global::Algorand.Address(recipientAddress);
        if (recipient.Bytes.All(value => value == 0))
            return AZOAResult<WttTargetInspection>.Failure("A nonzero Algorand recipient is required.");
        try
        {
            var snapshot = await ReadWttAlgoSnapshotAsync(null, 0, null, ct);
            var original = Convert.FromHexString(origin.OriginAddress);
            ulong? assetId;
            if (origin.OriginChain == 8)
            {
                if (original.AsSpan(0, 24).ContainsAnyExcept((byte)0))
                    throw new InvalidDataException("Original Algorand asset ID exceeds the uint64 protocol range.");
                assetId = BinaryPrimitives.ReadUInt64BigEndian(original.AsSpan(24));
            }
            else
            {
                if (original.All(value => value == 0)) throw new InvalidDataException("Foreign asset origin cannot be zero.");
                _ = WttAlgoEmitter(snapshot.Token, origin.OriginChain);
                var address = WttAlgorandStorage.Address(snapshot.Deployment.Token, origin.OriginChain, original);
                var account = await ReadWttAlgorandJsonAsync("/v2/accounts/" + address, ct, allowMissing: true);
                assetId = null;
                if (account.ValueKind != JsonValueKind.Undefined)
                {
                    var blob = WttAlgorandStorage.Read(account, snapshot.Deployment.Token, address, snapshot.Round);
                    if (blob is null && !WttAlgorandStorage.IsUnallocated(account, address, snapshot.Round))
                        throw new InvalidDataException("Foreign asset storage has incompatible authority or local state.");
                    if (blob is not null && (blob.Bytes.Any(value => value != 0) || blob.Metadata.Length != 0))
                    {
                        var wrapped = WttAlgorandStorage.ReadWrappedAsset(blob)
                            ?? throw new InvalidDataException("Foreign asset storage has an invalid registration.");
                        if (wrapped.Origin.TokenChain != origin.OriginChain || wrapped.Origin.TokenAddress != origin.OriginAddress)
                            throw new InvalidDataException("Foreign asset storage contains a different origin.");
                        assetId = wrapped.AssetId;
                    }
                }
            }
            if (unwrapNative && (origin.OriginChain != 8 || assetId != 0))
                throw new InvalidDataException("Only original ALGO settles as native Algorand value.");
            var decimals = (byte)Math.Min(origin.Decimals, (byte)8);
            var ready = false;
            if (assetId.HasValue)
            {
                var asset = await ReadWttAlgoAssetAsync(snapshot, assetId.Value.ToString(CultureInfo.InvariantCulture), assetId == 0, ct);
                if (asset.OriginChain != origin.OriginChain || asset.OriginAddress != origin.OriginAddress)
                    throw new InvalidDataException("Target asset does not match the original token identity.");
                decimals = asset.Decimals;
                var account = await ReadWttAlgorandJsonAsync("/v2/accounts/" + recipientAddress, ct, allowMissing: true);
                if (account.ValueKind != JsonValueKind.Undefined)
                {
                    if (WttAlgoText(account, "address") != recipientAddress || WttAlgoUnsigned(account, "round") != snapshot.Round)
                        throw new InvalidDataException("Recipient evidence differs from the finalized snapshot.");
                    ready = assetId == 0 ? WttAlgoUnsigned(account, "amount") >= WttAlgoUnsigned(account, "min-balance")
                        : WttAlgoHoldingReady(account, assetId.Value);
                }
            }
            await RequireWttAlgoRoundAsync(snapshot.Round, ct);
            return AZOAResult<WttTargetInspection>.Success(new(WttAlgoDescriptor(snapshot),
                assetId?.ToString(CultureInfo.InvariantCulture), decimals, assetId.HasValue, ready,
                Convert.ToHexStringLower(recipient.Bytes)));
        }
        catch (InvalidDataException exception) { return AZOAResult<WttTargetInspection>.Failure(exception.Message); }
        catch (JsonException) { return AZOAResult<WttTargetInspection>.Failure("Algorand WTT target state is malformed."); }
    }

    private static bool WttAlgoHoldingReady(JsonElement account, ulong asset)
    {
        if (!account.TryGetProperty("assets", out var holdings)) return false;
        if (holdings.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Recipient ASA holdings are malformed.");
        var matches = holdings.EnumerateArray().Where(item => WttAlgoUnsigned(item, "asset-id") == asset).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Recipient ASA holding is duplicated.");
        if (matches.Length == 0) return false;
        _ = WttAlgoUnsigned(matches[0], "amount");
        if (!matches[0].TryGetProperty("is-frozen", out var frozen) || frozen.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Recipient ASA freeze state is absent or malformed.");
        return !frozen.GetBoolean();
    }

    private async Task<WttAssetDescriptor> ReadWttAlgoAssetAsync(WttAlgoSnapshot snapshot, string tokenAddress,
        bool nativeAsset, CancellationToken ct)
    {
        if (nativeAsset)
        {
            if (tokenAddress is not ("" or "0")) throw new InvalidDataException("Native Algorand WTT uses asset zero.");
            var word = new byte[32]; "ALGO"u8.CopyTo(word);
            var text = Convert.ToHexStringLower(word);
            return new("0", true, 6, 8, new string('0', 64), 6, text, text,
                CanonicalJson.Hash(new { Kind = "NativeAlgo", snapshot.Deployment }));
        }
        if (!UInt64Text.TryParse(tokenAddress, out var id) || id == 0)
            throw new InvalidDataException("A canonical nonzero ASA ID is required.");
        var asset = WttAlgorandAsset.Read(await ReadWttAlgorandJsonAsync($"/v2/assets/{id}", ct), id);
        var creator = await ReadWttAlgorandJsonAsync("/v2/accounts/" + asset.Creator, ct);
        if (WttAlgoText(creator, "address") != asset.Creator || WttAlgoUnsigned(creator, "round") != snapshot.Round)
            throw new InvalidDataException("ASA creator evidence differs from the current finalized snapshot.");
        var bridge = global::Algorand.Address.ForApplication(snapshot.Deployment.Token).ToString();
        var controlled = false;
        if (creator.TryGetProperty("auth-addr", out var authority))
        {
            if (authority.ValueKind != JsonValueKind.String || !global::Algorand.Address.IsValid(authority.GetString()))
                throw new InvalidDataException("ASA creator rekey authority is malformed.");
            controlled = authority.GetString() == bridge;
        }
        if (controlled)
        {
            var blob = WttAlgorandStorage.Read(creator, snapshot.Deployment.Token, asset.Creator, snapshot.Round);
            var wrapped = blob is null ? null : WttAlgorandStorage.ReadWrappedAsset(blob);
            if (wrapped is null || wrapped.AssetId != id
                || WttAlgorandStorage.Address(snapshot.Deployment.Token, wrapped.Origin.TokenChain,
                    Convert.FromHexString(wrapped.Origin.TokenAddress)) != asset.Creator
                || WttAlgoEmitter(snapshot.Token, wrapped.EmitterChain) != wrapped.EmitterAddress
                || asset.Decimals != Math.Min(wrapped.Origin.Decimals, (byte)8)
                || asset.Total != 18446744073709550000UL || asset.DefaultFrozen
                || asset.Manager != bridge || asset.Freeze != bridge || asset.Clawback != bridge || asset.Reserve != asset.Creator)
                throw new InvalidDataException("Wrapped ASA does not match canonical bridge storage, origin and authority.");
            return new(tokenAddress, false, asset.Decimals, wrapped.Origin.TokenChain, wrapped.Origin.TokenAddress,
                wrapped.Origin.Decimals, asset.SymbolHex, asset.NameHex,
                CanonicalJson.Hash(new { Kind = "WttWrappedAsa", snapshot.Deployment, Asset = asset, wrapped.Origin.TokenChain, wrapped.Origin.TokenAddress }));
        }
        return new(tokenAddress, false, asset.Decimals, 8, WttAlgoAssetAddress(id), (byte)Math.Min(asset.Decimals, (byte)8),
            asset.SymbolHex, asset.NameHex, CanonicalJson.Hash(new { Kind = "OriginalAsa", Asset = asset }));
    }

    private static string WttAlgoAssetAddress(ulong asset)
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), asset);
        return Convert.ToHexStringLower(bytes);
    }
}

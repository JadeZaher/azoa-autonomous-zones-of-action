using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    /// <inheritdoc cref="IWttSourceModule.InspectWttSourceAsync"/>
    public async Task<AZOAResult<(WttNetworkDescriptor Network, WttAssetDescriptor Asset)>> InspectWttSourceAsync(
        string tokenAddress, bool nativeAsset, CancellationToken ct = default)
    {
        try
        {
            var snapshot = await ReadWttSnapshotAsync(ct);
            var asset = await ReadWttAssetAsync(snapshot, tokenAddress, nativeAsset, ct);
            return Ok((WttDescriptor(snapshot), asset));
        }
        catch (InvalidDataException exception)
        { return Error<(WttNetworkDescriptor Network, WttAssetDescriptor Asset)>(exception.Message); }
    }

    /// <inheritdoc cref="IWttTargetModule.InspectWttTargetAsync"/>
    public async Task<AZOAResult<WttTargetInspection>> InspectWttTargetAsync(WttAssetDescriptor origin,
        string recipientAddress, bool unwrapNative, CancellationToken ct = default)
    {
        if (origin is null || origin.OriginChain == 0 || !EthereumAddress.TryParse(recipientAddress, out var recipient)
            || recipient == "0x0000000000000000000000000000000000000000")
            return Error<WttTargetInspection>("An exact token origin and nonzero recipient are required.");
        byte[] originBytes;
        try { originBytes = Services.Wormhole.WttPayloadCodec.FixedHex(origin.OriginAddress, 32); }
        catch (FormatException) { return Error<WttTargetInspection>("Token origin must be canonical bytes32."); }
        try
        {
            var snapshot = await ReadWttSnapshotAsync(ct);
            string assetAddress;
            if (origin.OriginChain == snapshot.ProtocolChain)
            {
                if (originBytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) >= 0)
                    return Error<WttTargetInspection>("Ethereum original token address has nonzero padding.");
                assetAddress = EthereumHex.Data(originBytes[12..]);
            }
            else
            {
                assetAddress = await ReadWttAddressAsync(snapshot.Deployment.TokenBridge, "wrappedAsset(uint16,bytes32)",
                    [origin.OriginChain, originBytes], snapshot.Block, ct, allowZero: true);
                if (assetAddress == "0x0000000000000000000000000000000000000000")
                {
                    if (unwrapNative) return Error<WttTargetInspection>("Foreign wrapped assets cannot be unwrapped as native Ethereum value.");
                    return Ok(new WttTargetInspection(WttDescriptor(snapshot), null, (byte)Math.Min(origin.Decimals, (byte)8), false, true,
                        new string('0', 24) + recipientAddress[2..].ToLowerInvariant()));
                }
            }
            var asset = await ReadWttAssetAsync(snapshot, assetAddress, false, ct);
            if (asset.OriginChain != origin.OriginChain || asset.OriginAddress != origin.OriginAddress)
                return Error<WttTargetInspection>("Target token registration does not match the exact original asset.");
            if (unwrapNative)
            {
                var weth = await ReadWttAddressAsync(snapshot.Deployment.TokenBridge, "WETH()", [], snapshot.Block, ct);
                if (origin.OriginChain != snapshot.ProtocolChain || !EthereumAddress.AreSame(weth, asset.TokenAddress))
                    return Error<WttTargetInspection>("Only the registered original WETH representation can unwrap to native value.");
            }
            return Ok(new WttTargetInspection(WttDescriptor(snapshot), asset.TokenAddress, asset.Decimals, true, true,
                new string('0', 24) + recipientAddress[2..].ToLowerInvariant()));
        }
        catch (InvalidDataException exception) { return Error<WttTargetInspection>(exception.Message); }
    }

    private WttNetworkDescriptor WttDescriptor(WttEvmSnapshot snapshot) => new("Ethereum", ActiveNetwork,
        snapshot.ProtocolIdentity, ActiveNetwork.ToString(), snapshot.ProtocolChain, snapshot.Deployment.Core,
        snapshot.Deployment.TokenBridge, CanonicalJson.Hash(snapshot.Deployment), new(snapshot.ProtocolChain,
            snapshot.Deployment.Genesis[2..].ToLowerInvariant(), new string('0', 24) + snapshot.Deployment.Core[2..].ToLowerInvariant(),
            new string('0', 24) + snapshot.Deployment.TokenBridge[2..].ToLowerInvariant()),
        new string('0', 24) + snapshot.Deployment.TokenBridge[2..].ToLowerInvariant());

    private async Task<WttAssetDescriptor> ReadWttAssetAsync(WttEvmSnapshot snapshot, string requestedAddress,
        bool nativeAsset, CancellationToken ct)
    {
        var token = requestedAddress;
        if (nativeAsset)
        {
            token = await ReadWttAddressAsync(snapshot.Deployment.TokenBridge, "WETH()", [], snapshot.Block, ct);
            if (!string.IsNullOrEmpty(requestedAddress) && !EthereumAddress.AreSame(requestedAddress, token))
                throw new InvalidDataException("Native Ethereum WTT uses the bridge's actual WETH contract.");
        }
        if (!EthereumAddress.TryParse(token, out token) || token == "0x0000000000000000000000000000000000000000")
            throw new InvalidDataException("A nonzero Ethereum token address is required.");
        var decimals = await ReadWttUIntAsync(token, "decimals()", [], snapshot.Block, ct);
        if (decimals > byte.MaxValue) throw new InvalidDataException("Token decimals exceed the protocol range.");
        var wrapped = await ReadWttUIntAsync(snapshot.Deployment.TokenBridge, "isWrappedAsset(address)", [token], snapshot.Block, ct);
        if (wrapped > 1 || nativeAsset && wrapped != 0) throw new InvalidDataException("Token wrapped status is inconsistent.");
        ushort originChain = snapshot.ProtocolChain;
        string originAddress = new string('0', 24) + token[2..].ToLowerInvariant();
        byte? originDecimals = (byte)decimals;
        string behavior;
        if (wrapped == 1)
        {
            var wrappedCode = await RequireWttWrappedCodeAsync(snapshot, token, ct);
            var origin = await ReadWttUIntAsync(token, "chainId()", [], snapshot.Block, ct);
            var native = await ReadWttCallAsync(token, "nativeContract()", [], snapshot.Block, ct);
            if (origin <= 0 || origin > ushort.MaxValue || origin == snapshot.ProtocolChain
                || !EthereumHex.TryParseData(native, out var nativeBytes) || nativeBytes.Length != 32)
                throw new InvalidDataException("Wrapped token original identity is malformed.");
            originChain = (ushort)origin;
            originAddress = Convert.ToHexStringLower(nativeBytes);
            var registered = await ReadWttAddressAsync(snapshot.Deployment.TokenBridge, "wrappedAsset(uint16,bytes32)",
                [originChain, nativeBytes], snapshot.Block, ct);
            if (!EthereumAddress.AreSame(registered, token) || decimals > 8)
                throw new InvalidDataException("Wrapped token is not the bridge's exact registered representation.");
            originDecimals = null;
            behavior = CanonicalJson.Hash(new { snapshot.Deployment.TokenBridge, originChain, originAddress, wrappedCode });
        }
        else
        {
            var prefix = $"AZOA:Ethereum:Wtt:Networks:{ActiveNetwork}:StandardTokens:{token.ToLowerInvariant()}:";
            var code = _config[prefix + "RuntimeCodeHash"] ?? string.Empty;
            var kind = _config[prefix + "ContractKind"];
            if (!WttHash(code) || kind is not ("NonProxy" or "Eip1967"))
                throw new InvalidDataException("Original tokens require an explicitly reviewed standard-transfer behavior and code pin.");
            var implementation = _config[prefix + "ImplementationCodeHash"] ?? string.Empty;
            if (kind == "Eip1967")
            {
                if (!WttHash(implementation)) throw new InvalidDataException("Proxy token behavior requires an implementation code pin.");
                await RequireWttProxyAsync(token, code, implementation, snapshot.Block, ct);
            }
            else await RequireWttCodeAsync(token, code, snapshot.Block, ct);
            behavior = CanonicalJson.Hash(new { token, kind, code, implementation });
        }
        var symbol = await ReadWttCallAsync(token, "symbol()", [], snapshot.Block, ct);
        var name = await ReadWttCallAsync(token, "name()", [], snapshot.Block, ct);
        if (!WttEthereumAssetCodec.TryMetadataWord(symbol, out var symbolHex)
            || !WttEthereumAssetCodec.TryMetadataWord(name, out var nameHex))
            throw new InvalidDataException("Token metadata does not match the bridge's Solidity string ABI.");
        return new(token, nativeAsset, (byte)decimals, originChain, originAddress, originDecimals, symbolHex, nameHex, behavior);
    }

    private async Task<string> ReadWttAddressAsync(string contract, string signature, object[] values, object block,
        CancellationToken ct, bool allowZero = false)
    {
        var encoded = await ReadWttCallAsync(contract, signature, values, block, ct);
        if (!EthereumHex.TryParseData(encoded, out var bytes) || bytes.Length != 32
            || bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) >= 0 || !allowZero && bytes.All(value => value == 0))
            throw new InvalidDataException("WTT contract returned a malformed address.");
        return EthereumHex.Data(bytes[12..]).ToLowerInvariant();
    }

    private async Task<string> RequireWttWrappedCodeAsync(WttEvmSnapshot snapshot, string token, CancellationToken ct)
    {
        var prefix = $"AZOA:Ethereum:Wtt:Networks:{ActiveNetwork}:";
        var proxyHash = _config[prefix + "WrappedProxyCodeHash"] ?? string.Empty;
        var implementationHash = _config[prefix + "WrappedImplementationCodeHash"] ?? string.Empty;
        if (!WttHash(proxyHash) || !WttHash(implementationHash))
            throw new InvalidDataException("Wrapped token proxy and implementation code pins are required.");
        await RequireWttCodeAsync(token, proxyHash, snapshot.Block, ct);
        var implementation = await ReadWttBeaconImplementationAsync(token, snapshot.Deployment.TokenBridge, snapshot.Block, ct);
        await RequireWttCodeAsync(implementation, implementationHash, snapshot.Block, ct);
        return CanonicalJson.Hash(new { proxyHash, implementationHash, implementation });
    }
}

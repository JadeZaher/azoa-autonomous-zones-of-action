using System.Numerics;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private async Task<WttEvmAssetPin> ReadWttAssetPinAsync(WttEvmSnapshot snapshot, WttAssetDescriptor asset, CancellationToken ct)
    {
        var prefix = $"AZOA:Ethereum:Wtt:Networks:{ActiveNetwork}:";
        if (asset.OriginChain != snapshot.ProtocolChain)
            return new("WttBeacon", _config[prefix + "WrappedProxyCodeHash"] ?? "", _config[prefix + "WrappedImplementationCodeHash"] ?? "",
                await ReadWttAddressAsync(snapshot.Deployment.TokenBridge, "implementation()", [], snapshot.Block, ct));
        prefix += "StandardTokens:" + asset.TokenAddress.ToLowerInvariant() + ":";
        return new(_config[prefix + "ContractKind"] ?? "", _config[prefix + "RuntimeCodeHash"] ?? "",
            _config[prefix + "ImplementationCodeHash"] ?? "", null);
    }

    private static string WttAssetBehaviorIdentity(WttAssetDescriptor asset, WttEvmDeployment deployment, WttEvmAssetPin pin)
    {
        if (!WttHash(pin.RuntimeHash) || pin.Kind is not ("NonProxy" or "Eip1967" or "WttBeacon")
            || pin.Kind != "NonProxy" && !WttHash(pin.ImplementationHash))
            throw new InvalidDataException("WTT token code evidence is incomplete.");
        if (pin.Kind == "WttBeacon")
        {
            if (asset.OriginChain == 0 || !EthereumAddress.IsNonZero(pin.ImplementationAddress))
                throw new InvalidDataException("WTT beacon implementation identity is incomplete.");
            var wrappedCode = CanonicalJson.Hash(new { proxyHash = pin.RuntimeHash,
                implementationHash = pin.ImplementationHash, implementation = pin.ImplementationAddress });
            return CanonicalJson.Hash(new { deployment.TokenBridge, originChain = asset.OriginChain,
                originAddress = asset.OriginAddress, wrappedCode });
        }
        return CanonicalJson.Hash(new { token = asset.TokenAddress, kind = pin.Kind,
            code = pin.RuntimeHash, implementation = pin.ImplementationHash });
    }

    private async Task RequireWttAssetCodeAtAsync(WttAssetDescriptor asset, WttEvmDeployment deployment,
        WttEvmAssetPin pin, object block, CancellationToken ct)
    {
        if (WttAssetBehaviorIdentity(asset, deployment, pin) != asset.BehaviorIdentity)
            throw new InvalidDataException("WTT token code evidence differs from frozen behavior.");
        if (pin.Kind == "Eip1967")
        {
            await RequireWttProxyAsync(asset.TokenAddress, pin.RuntimeHash, pin.ImplementationHash, block, ct);
            return;
        }
        await RequireWttCodeAsync(asset.TokenAddress, pin.RuntimeHash, block, ct);
        if (pin.Kind != "WttBeacon") return;
        var implementation = await ReadWttBeaconImplementationAsync(asset.TokenAddress, deployment.TokenBridge, block, ct);
        if (!EthereumAddress.AreSame(implementation, pin.ImplementationAddress))
            throw new InvalidDataException("WTT wrapped implementation changed at execution.");
        await RequireWttCodeAsync(implementation, pin.ImplementationHash, block, ct);
    }

    private async Task<string> ReadWttBeaconImplementationAsync(string token, string bridge, object block, CancellationToken ct)
    {
        var slot = new BigInteger(Keccak.CalculateHash(System.Text.Encoding.UTF8.GetBytes("eip1967.proxy.beacon")), true, true) - 1;
        var storage = await RpcReadAsync("eth_getStorageAt", [token, EthereumHex.Data(slot.ToByteArray(true, true)), block], ct);
        if (storage.IsError || !storage.TryGetString(out var encoded) || !EthereumHex.TryParseData(encoded, out var bytes)
            || bytes.Length != 32 || bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) >= 0
            || !EthereumAddress.AreSame(EthereumHex.Data(bytes[12..]), bridge))
            throw new InvalidDataException("WTT wrapped beacon identity changed at execution.");
        var implementation = await ReadWttAddressAsync(bridge, "implementation()", [], block, ct);
        var declared = await ReadWttAddressAsync(bridge, "tokenImplementation()", [], block, ct);
        if (!EthereumAddress.AreSame(implementation, declared))
            throw new InvalidDataException("Token bridge beacon disagrees with its declared wrapped implementation.");
        return implementation;
    }
}

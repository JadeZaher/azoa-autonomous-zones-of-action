using System.Collections;
using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using Nethereum.ABI.Model;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider : IWttGuardianStateSource
{
    private const string WttImplementationSlot = "0x360894a13ba1a3210667c828492db98dca3e2076cc3735a920a3ca505d382bbc";
    private sealed record WttEvmDeployment(string Core, string TokenBridge, string Genesis,
        string CoreProxyHash, string CoreImplementationHash, string TokenProxyHash, string TokenImplementationHash);

    /// <inheritdoc/>
    public async Task<AZOAResult<WttGuardianEvidence>> ReadWttGuardianEvidenceAsync(uint guardianSetIndex, ushort emitterChain, string emitterAddress,
        CancellationToken ct = default)
    {
        try
        {
            var snapshot = await ReadWttSnapshotAsync(ct);
            var deployment = snapshot.Deployment;
            var block = snapshot.Block;
            var current = await ReadWttUIntAsync(deployment.Core, "getCurrentGuardianSetIndex()", [], block, ct);
            if (current > uint.MaxValue) return Error<WttGuardianEvidence>("Guardian set index exceeds the protocol range.");
            var encodedSet = await ReadWttCallAsync(deployment.Core, "getGuardianSet(uint32)", [guardianSetIndex], block, ct);
            if (!TryWttGuardianSet(encodedSet, out var addresses, out var expiry))
                return Error<WttGuardianEvidence>("Core returned a malformed or empty guardian set.");
            var emitter = await ReadWttCallAsync(deployment.TokenBridge, "bridgeContracts(uint16)", [emitterChain], block, ct);
            if (!EthereumHex.TryParseData(emitter, out var emitterBytes) || emitterBytes.Length != 32 || emitterBytes.All(value => value == 0)
                || Convert.ToHexStringLower(emitterBytes) != emitterAddress)
                return Error<WttGuardianEvidence>("The source emitter is not registered on the target token bridge.");
            return Ok(new WttGuardianEvidence(snapshot.ProtocolIdentity,
                deployment.Core, deployment.TokenBridge, guardianSetIndex, (uint)current, addresses, expiry,
                snapshot.ChainTime, snapshot.BlockHash, DateTimeOffset.UtcNow, emitterChain, Convert.ToHexStringLower(emitterBytes)));
        }
        catch (InvalidDataException exception) { return Error<WttGuardianEvidence>(exception.Message); }
    }

    private sealed record WttEvmSnapshot(WttEvmDeployment Deployment, string ProtocolIdentity,
        ushort ProtocolChain, string BlockHash, uint ChainTime, object Block);

    private async Task<WttEvmSnapshot> ReadWttSnapshotAsync(CancellationToken ct)
    {
        _ = RpcHttpClient;
        var deployment = ReadWttDeployment()
            ?? throw new InvalidDataException("An enabled, fully pinned Ethereum WTT deployment is required.");
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError) throw new InvalidDataException(chain.Message);
        ushort protocolChain = ActiveNetwork == Core.ChainNetwork.Testnet ? (ushort)10002 : (ushort)2;
        if (ActiveNetwork == Core.ChainNetwork.Testnet && chain.Result != 11155111
            || ActiveNetwork == Core.ChainNetwork.Mainnet && chain.Result != 1)
            throw new InvalidDataException("Ethereum WTT network profile does not match its supported protocol chain.");
        var genesis = await RpcReadAsync("eth_getBlockByNumber", ["0x0", false], ct);
        var finalized = await RpcReadAsync("eth_getBlockByNumber", ["finalized", false], ct);
        if (genesis.IsError || finalized.IsError || genesis.IsNullResult || finalized.IsNullResult
            || !TryReadStringProperty(genesis.Value, "hash", out var genesisHash) || !SameWttHash(genesisHash, deployment.Genesis)
            || !TryReadStringProperty(finalized.Value, "hash", out var blockHash) || !WttHash(blockHash)
            || !TryReadQuantityProperty(finalized.Value, "timestamp", out var chainTime) || chainTime < 0 || chainTime > uint.MaxValue)
            throw new InvalidDataException("Finalized Ethereum WTT genesis/block identity is unavailable.");
        var block = new { blockHash, requireCanonical = true };
        await RequireWttProxyAsync(deployment.Core, deployment.CoreProxyHash, deployment.CoreImplementationHash, block, ct);
        await RequireWttProxyAsync(deployment.TokenBridge, deployment.TokenProxyHash, deployment.TokenImplementationHash, block, ct);
        foreach (var contract in new[] { deployment.Core, deployment.TokenBridge })
            if (await ReadWttUIntAsync(contract, "chainId()", [], block, ct) != protocolChain
                || await ReadWttUIntAsync(contract, "evmChainId()", [], block, ct) != chain.Result)
                throw new InvalidDataException("WTT contracts report a different protocol or EVM chain identity.");
        var coreCall = await ReadWttCallAsync(deployment.TokenBridge, "wormhole()", [], block, ct);
        if (!EthereumAbi.TryDecodeAddress(coreCall, out var actualCore) || !EthereumAddress.AreSame(actualCore, deployment.Core))
            throw new InvalidDataException("Token bridge does not bind the configured core bridge.");
        return new(deployment, "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture) + ":" + genesisHash.ToLowerInvariant(),
            protocolChain, blockHash.ToLowerInvariant(), (uint)chainTime, block);
    }

    private WttEvmDeployment? ReadWttDeployment()
    {
        var prefix = $"AZOA:Ethereum:Wtt:Networks:{ActiveNetwork}:";
        if (!bool.TryParse(_config[prefix + "Enabled"], out var enabled) || !enabled
            || !EthereumAddress.TryParse(_config[prefix + "CoreBridge"], out var core)
            || !EthereumAddress.TryParse(_config[prefix + "TokenBridge"], out var token)) return null;
        string Read(string name) => _config[prefix + name] ?? string.Empty;
        var hashes = new[] { "GenesisHash", "CoreProxyCodeHash", "CoreImplementationCodeHash", "TokenBridgeProxyCodeHash", "TokenBridgeImplementationCodeHash" }
            .Select(Read).ToArray();
        return hashes.All(WttHash) ? new(core, token, hashes[0], hashes[1], hashes[2], hashes[3], hashes[4]) : null;
    }

    private async Task RequireWttProxyAsync(string proxy, string proxyHash, string implementationHash, object block, CancellationToken ct)
    {
        await RequireWttCodeAsync(proxy, proxyHash, block, ct);
        var slot = await RpcReadAsync("eth_getStorageAt", [proxy, WttImplementationSlot, block], ct);
        if (slot.IsError || !slot.TryGetString(out var value) || !EthereumHex.TryParseData(value, out var bytes)
            || bytes.Length != 32 || bytes[..12].Any(item => item != 0) || bytes[12..].All(item => item == 0))
            throw new InvalidDataException("WTT proxy implementation is not authoritatively readable.");
        await RequireWttCodeAsync(EthereumHex.Data(bytes[12..]), implementationHash, block, ct);
    }

    private Task RequireWttCodeAsync(string address, string expectedHash, object block, CancellationToken ct)
        => RequireCodeAtBlockAsync(address, expectedHash, block, ct);

    private Task<string> ReadWttCallAsync(string contract, string signature, object[] values, object block, CancellationToken ct)
        => ReadContractCallAtBlockAsync(contract, signature, values, block, ct);

    private Task<BigInteger> ReadWttUIntAsync(string contract, string signature, object[] values, object block, CancellationToken ct)
        => ReadUInt256AtBlockAsync(contract, signature, values, block, ct);

    internal static bool TryWttGuardianSet(string encoded, out IReadOnlyList<string> addresses, out uint expiry)
    {
        addresses = []; expiry = 0;
        if (!EthereumHex.TryParseData(encoded, out var bytes) || bytes.Length < 160 || bytes.Length > 32 * 260
            || new BigInteger(bytes.AsSpan(0, 32), true, true) != 32
            || new BigInteger(bytes.AsSpan(32, 32), true, true) != 64) return false;
        var expiration = new BigInteger(bytes.AsSpan(64, 32), true, true);
        var count = new BigInteger(bytes.AsSpan(96, 32), true, true);
        if (expiration > uint.MaxValue || count < 1 || count > 256 || bytes.Length != 128 + (int)count * 32) return false;
        for (var i = 0; i < (int)count; i++)
            if (bytes.AsSpan(128 + i * 32, 12).IndexOfAnyExcept((byte)0) >= 0) return false;
        if (!EthereumAbi.TryDecodeReturn(EthereumHex.Data(bytes[32..]), [new Parameter("address[]", 1), new Parameter("uint32", 2)], out var decoded)
            || decoded.Count != 2 || decoded[0].Result is not IEnumerable values) return false;
        var guardians = new List<string>();
        foreach (var item in values)
        {
            if (item is not string address || !EthereumAddress.TryParse(address, out var canonical)) return false;
            guardians.Add(canonical[2..].ToLowerInvariant());
        }
        if (guardians.Count != (int)count || guardians.Distinct(StringComparer.Ordinal).Count() != guardians.Count) return false;
        addresses = guardians; expiry = (uint)expiration;
        return true;
    }

    private static bool WttHash(string value) => EthereumHex.TryParseData(value, out var bytes) && bytes.Length == 32;
    private static bool SameWttHash(string left, string right) => WttHash(left) && WttHash(right) && left.Equals(right, StringComparison.OrdinalIgnoreCase);
}

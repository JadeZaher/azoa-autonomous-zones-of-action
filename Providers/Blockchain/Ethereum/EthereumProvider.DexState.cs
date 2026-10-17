using System.Globalization;
using System.Numerics;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private sealed record DexEvmDeployment(string Executor, string Router, string Factory, string Quoter, string WrappedNative,
        string PolicySigner, string PolicyKeyVersion, string GenesisHash, string ExecutorHash, string RouterHash,
        string FactoryHash, string QuoterHash, string WrappedHash, uint PoolFee);
    private sealed record DexEvmSnapshot(DexEvmDeployment Deployment, string ProtocolIdentity, BigInteger ChainId,
        string BlockHash, BigInteger BlockNumber, BigInteger ChainTime, object Block);

    private DexEvmDeployment ReadDexDeployment()
    {
        var prefix = $"AZOA:Ethereum:Dex:Networks:{ActiveNetwork}:";
        string Text(string name) => _config[prefix + name] ?? string.Empty;
        string Address(string name) => EthereumAddress.TryParse(Text(name), out var value) && EthereumAddress.IsNonZero(value)
            ? value : throw new InvalidDataException("Ethereum DEX requires an explicit nonzero " + name + ".");
        string Hash(string name)
        {
            var text = Text(name);
            if (!EthereumHex.TryParseData(text, out var bytes) || bytes.Length != 32 || bytes.All(value => value == 0))
                throw new InvalidDataException("Ethereum DEX requires an explicit " + name + " pin.");
            return EthereumHex.Data(bytes);
        }
        if (!bool.TryParse(Text("Enabled"), out var enabled) || !enabled ||
            !uint.TryParse(Text("PoolFee"), NumberStyles.None, CultureInfo.InvariantCulture, out var fee) || fee is 0 or > 0xffffff)
            throw new InvalidDataException("Ethereum DEX requires an enabled reviewed pool policy.");
        return new(Address("Executor"), Address("Router"), Address("Factory"), Address("Quoter"), Address("WrappedNative"),
            Address("PolicySigner"), Hash("PolicyKeyVersion"), Hash("GenesisHash"), Hash("ExecutorCodeHash"),
            Hash("RouterCodeHash"), Hash("FactoryCodeHash"), Hash("QuoterCodeHash"), Hash("WrappedNativeCodeHash"), fee);
    }

    private async Task<DexEvmSnapshot> ReadDexSnapshotAsync(DexEvmDeployment deployment, CancellationToken ct)
    {
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError) throw new InvalidDataException(chain.Message);
        var genesis = await RpcReadAsync("eth_getBlockByNumber", ["0x0", false], ct);
        var finalized = await RpcReadAsync("eth_getBlockByNumber", ["finalized", false], ct);
        if (genesis.IsError || finalized.IsError || genesis.IsNullResult || finalized.IsNullResult ||
            !TryReadStringProperty(genesis.Value, "hash", out var genesisHash) || !SameDexHash(genesisHash, deployment.GenesisHash) ||
            !TryReadStringProperty(finalized.Value, "hash", out var blockHash) || !IsDexHash(blockHash) ||
            !TryReadQuantityProperty(finalized.Value, "number", out var blockNumber) || blockNumber < 0 ||
            !TryReadQuantityProperty(finalized.Value, "timestamp", out var chainTime) || chainTime <= 0)
            throw new InvalidDataException("Ethereum DEX requires authoritative finalized genesis, block and timestamp.");
        var block = new { blockHash, requireCanonical = true };
        foreach (var (address, hash) in new[] { (deployment.Executor, deployment.ExecutorHash), (deployment.Router, deployment.RouterHash),
            (deployment.Factory, deployment.FactoryHash), (deployment.Quoter, deployment.QuoterHash), (deployment.WrappedNative, deployment.WrappedHash) })
            await RequireCodeAtBlockAsync(address, hash, block, ct);
        foreach (var (contract, signature, address) in new[]
        {
            (deployment.Executor, "router()", deployment.Router), (deployment.Executor, "factory()", deployment.Factory),
            (deployment.Executor, "wrappedNative()", deployment.WrappedNative), (deployment.Executor, "policySigner()", deployment.PolicySigner),
            (deployment.Router, "factory()", deployment.Factory), (deployment.Router, "WETH9()", deployment.WrappedNative),
            (deployment.Quoter, "factory()", deployment.Factory), (deployment.Quoter, "WETH9()", deployment.WrappedNative)
        })
            if (!EthereumAddress.AreSame(await ReadDexAddressAsync(contract, signature, [], block, ct), address))
                throw new InvalidDataException("Ethereum DEX deployed immutable authority differs from its reviewed policy.");
        foreach (var (signature, hash) in new[] { ("policyKeyVersion()", deployment.PolicyKeyVersion), ("routerCodeHash()", deployment.RouterHash),
            ("factoryCodeHash()", deployment.FactoryHash), ("wrappedNativeCodeHash()", deployment.WrappedHash) })
            if (!SameDexHash(await ReadContractCallAtBlockAsync(deployment.Executor, signature, [], block, ct), hash))
                throw new InvalidDataException("Ethereum DEX executor runtime pins differ from current deployment.");
        return new(deployment, "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture), chain.Result, blockHash, blockNumber, chainTime, block);
    }

    private async Task<string> ReadDexAddressAsync(string contract, string signature, object[] values, object block, CancellationToken ct)
    {
        var encoded = await ReadContractCallAtBlockAsync(contract, signature, values, block, ct);
        if (!EthereumHex.TryParseData(encoded, out var bytes) || bytes.Length != 32 || bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) >= 0 ||
            !EthereumAbi.TryDecodeAddress(encoded, out var address)) throw new InvalidDataException("DEX contract address result is not one canonical ABI word.");
        return address;
    }

    private static bool IsDexHash(string value) => EthereumHex.TryParseData(value, out var bytes) && bytes.Length == 32;
    private static bool SameDexHash(string left, string right) => IsDexHash(left) && IsDexHash(right) && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private bool CurrentDexTime(BigInteger chainTime)
    {
        var prefix = $"AZOA:Ethereum:Dex:Networks:{ActiveNetwork}:";
        if (!int.TryParse(_config[prefix + "MaximumFinalizedLagSeconds"], NumberStyles.None, CultureInfo.InvariantCulture, out var lag) || lag is < 1 or > 7200 ||
            !int.TryParse(_config[prefix + "MaximumFutureSkewSeconds"], NumberStyles.None, CultureInfo.InvariantCulture, out var future) || future is < 0 or > 120)
            return false;
        var now = new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return chainTime >= now - lag && chainTime <= now + future;
    }
}

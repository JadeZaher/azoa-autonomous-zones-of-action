using System.Numerics;
using System.Security.Cryptography;
using Nethereum.Signer;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private EthECKey? ReadDexPolicyKey(DexEvmDeployment deployment)
    {
        var version = deployment.PolicyKeyVersion[2..].ToLowerInvariant();
        var configured = _config[$"AZOA:Ethereum:Dex:PolicyKeys:{version}:PrivateKey"];
        if (!EthereumHex.TryParseData(configured, out var secret)) return null;
        try
        {
            if (secret.Length != 32) return null;
            var scalar = new BigInteger(secret, isUnsigned: true, isBigEndian: true);
            var order = new BigInteger(Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141"), true, true);
            if (scalar <= 0 || scalar >= order) return null;
            var key = new EthECKey(secret, true);
            return EthereumAddress.AreSame(key.GetPublicAddress(), deployment.PolicySigner) ? key : null;
        }
        catch (ArgumentException) { return null; }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private byte[] SignDexPolicy(DexEvmDeployment deployment, BigInteger chain, byte[] payload)
    {
        var key = ReadDexPolicyKey(deployment)
            ?? throw new InvalidDataException("The original retained DEX policy key version is unavailable.");
        return EthereumPayloadPermit.Sign(key, EthereumPayloadPermit.Digest("AZOA DEX", "1", chain, deployment.Executor, payload));
    }
}

using System.Numerics;
using System.Text;
using Nethereum.ABI;
using Nethereum.Util;
using Nethereum.Signer;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>Builds the EIP-712 Execution payload-hash digest for an explicit protocol domain.</summary>
internal static class EthereumPayloadPermit
{
    internal static void RequireSigner(byte[] digest, byte[] permit, string signer)
    {
        if (digest.Length != 32 || permit.Length != 65 || permit[64] is not (27 or 28) ||
            !EthereumSignatureRules.IsCanonical(permit.AsSpan(0, 32), permit.AsSpan(32, 32), (byte)(permit[64] - 27)) ||
            !EthereumAddress.AreSame(new MessageSigner().EcRecover(digest, EthereumHex.Data(permit)), signer))
            throw new InvalidDataException("The exact protocol digest has no canonical permit from its pinned policy signer.");
    }

    internal static byte[] Sign(EthECKey key, byte[] digest)
    {
        if (digest.Length != 32) throw new InvalidDataException("An exact EIP-712 digest is required.");
        var signature = key.SignAndCalculateV(digest);
        var wire = new byte[65];
        signature.R.CopyTo(wire, 32 - signature.R.Length);
        signature.S.CopyTo(wire, 64 - signature.S.Length);
        wire[64] = signature.V.Single();
        return wire;
    }

    internal static byte[] Digest(string name, string version, BigInteger chain, string contract, byte[] payload)
    {
        var keccak = new Sha3Keccack(); var abi = new ABIEncode();
        var domain = keccak.CalculateHash(abi.GetABIEncoded(
            new ABIValue("bytes32", keccak.CalculateHash(Encoding.UTF8.GetBytes("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"))),
            new ABIValue("bytes32", keccak.CalculateHash(Encoding.UTF8.GetBytes(name))),
            new ABIValue("bytes32", keccak.CalculateHash(Encoding.UTF8.GetBytes(version))),
            new ABIValue("uint256", chain), new ABIValue("address", contract)));
        var message = keccak.CalculateHash(abi.GetABIEncoded(
            new ABIValue("bytes32", keccak.CalculateHash(Encoding.UTF8.GetBytes("Execution(bytes32 payloadHash)"))),
            new ABIValue("bytes32", keccak.CalculateHash(payload))));
        return keccak.CalculateHash(new byte[] { 0x19, 0x01 }.Concat(domain).Concat(message).ToArray());
    }
}

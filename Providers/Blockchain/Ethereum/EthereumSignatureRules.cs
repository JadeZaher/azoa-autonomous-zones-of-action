using System.Numerics;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>Enforces nonzero secp256k1 scalars, EIP-2 low-S and one-bit recovery parity.</summary>
internal static class EthereumSignatureRules
{
    private static readonly BigInteger Order = new(Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141"), true, true);

    internal static bool IsCanonical(ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes, byte parity)
    {
        if (rBytes.Length is 0 or > 32 || sBytes.Length is 0 or > 32 || parity > 1) return false;
        var r = new BigInteger(rBytes, true, true); var s = new BigInteger(sBytes, true, true);
        return r > 0 && r < Order && s > 0 && s <= Order / 2;
    }
}

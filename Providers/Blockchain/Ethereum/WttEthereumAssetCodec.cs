using System.Numerics;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

internal static class WttEthereumAssetCodec
{
    internal static bool TryMetadataWord(string encoded, out string word)
    {
        word = string.Empty;
        if (!EthereumHex.TryParseData(encoded, out var bytes) || bytes.Length < 64 || bytes.Length > 4160
            || new BigInteger(bytes.AsSpan(0, 32), true, true) != 32) return false;
        var length = new BigInteger(bytes.AsSpan(32, 32), true, true);
        if (length > 4096 || bytes.Length != 64 + ((int)length + 31) / 32 * 32
            || bytes.AsSpan(64 + (int)length).IndexOfAnyExcept((byte)0) >= 0) return false;
        var first = new byte[32];
        bytes.AsSpan(64, Math.Min((int)length, 32)).CopyTo(first);
        word = Convert.ToHexStringLower(first);
        return true;
    }
}

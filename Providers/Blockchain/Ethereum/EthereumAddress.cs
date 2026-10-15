using Nethereum.Util;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>EIP-55-aware Ethereum address parsing and normalization.</summary>
/// <remarks>See <c>Providers/Blockchain/Ethereum/AGENTS.md</c> §addresses.</remarks>
public static class EthereumAddress
{
    /// <summary>The all-zero address; never a valid transfer counterparty on a value path.</summary>
    public const string Zero = "0x0000000000000000000000000000000000000000";

    private static readonly AddressUtil Util = new();

    /// <summary>
    /// Parses an address and returns its EIP-55 checksummed form.
    /// <para>A MIXED-case address carries a checksum and must satisfy it — a single
    /// mistyped character in a checksummed address is caught here rather than sending
    /// value to an unowned address. An all-lower or all-upper address carries no
    /// checksum information, so it is accepted and normalized.</para>
    /// </summary>
    public static bool TryParse(string? address, out string checksummed)
    {
        checksummed = string.Empty;
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var text = address.Trim();
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return false;

        var digits = text[2..];
        if (digits.Length != 40 || !EthereumHex.IsHex(digits))
            return false;

        var hasLowerLetter = digits.Any(c => c is >= 'a' and <= 'f');
        var hasUpperLetter = digits.Any(c => c is >= 'A' and <= 'F');
        if (hasLowerLetter && hasUpperLetter && !Util.IsChecksumAddress(text))
            return false;

        checksummed = Util.ConvertToChecksumAddress("0x" + digits.ToLowerInvariant());
        return true;
    }

    /// <summary>True when both parse to the same address, ignoring checksum casing.</summary>
    public static bool AreSame(string? left, string? right) =>
        TryParse(left, out var a) && TryParse(right, out var b) && string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>True when the address parses and is not the zero address.</summary>
    public static bool IsNonZero(string? address) =>
        TryParse(address, out var parsed) && !string.Equals(parsed, Zero, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="tokenId"/> denotes native ETH rather than an ERC-20
    /// contract: absent, the ticker, or the zero address (the conventional native
    /// sentinel). Anything else must parse as a contract address.
    /// </summary>
    public static bool IsNativeToken(string? tokenId)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
            return true;

        var text = tokenId.Trim();
        if (text.Equals("ETH", StringComparison.OrdinalIgnoreCase)
            || text.Equals("ether", StringComparison.OrdinalIgnoreCase)
            || text.Equals("native", StringComparison.OrdinalIgnoreCase))
            return true;

        return TryParse(text, out var parsed) && string.Equals(parsed, Zero, StringComparison.OrdinalIgnoreCase);
    }
}

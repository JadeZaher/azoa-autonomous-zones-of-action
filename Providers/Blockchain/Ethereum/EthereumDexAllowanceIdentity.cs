using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>Canonical physical-network ERC20 allowance identity.</summary>
public static class EthereumDexAllowanceIdentity
{
    public static bool IsCanonical(string protocol, string owner, string token, string executor)
    {
        var parts = protocol?.Split(':');
        return parts is { Length: 3 } && parts[0] == "eip155" &&
            UnsignedIntegerText.TryParse(parts[1], out var chain) && chain > 0 &&
            parts[2].Length == 64 && parts[2].All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            parts[2].Any(value => value != '0') && CanonicalAddress(owner) && CanonicalAddress(token) && CanonicalAddress(executor);
    }

    public static string Create(string protocol, string owner, string token, string executor)
    {
        if (!IsCanonical(protocol, owner, token, executor)) throw new ArgumentException("Allowance resource identity is not canonical.");
        return CanonicalJson.Hash(JsonSerializer.SerializeToElement(new { version = 1, protocol, owner, token, executor }));
    }

    private static bool CanonicalAddress(string value)
        => value is { Length: 42 } && value == value.ToLowerInvariant() && EthereumAddress.IsNonZero(value);
}

public sealed record EthereumDexAllowanceReleaseEvidence(int Version, string ProtocolIdentity, string Owner,
    string Token, string Executor, string AllowanceAfter, string TransactionId, string BlockHash, string BlockNumber, string Effect);

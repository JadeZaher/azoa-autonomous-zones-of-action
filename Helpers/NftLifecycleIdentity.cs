using System.Security.Cryptography;
using System.Text;

namespace AZOA.WebAPI.Helpers;

/// <summary>Deterministic application identities for NFT requests, never chain transaction or asset identifiers.</summary>
public static class NftLifecycleIdentity
{
    /// <summary>Partitions a caller's request key by operation into the durable lifecycle record key.</summary>
    public static string ForRequest(Guid actor, string operation, string key, Guid tenant = default)
        => Fingerprint($"{tenant:N}:{actor:N}:{operation}:{key}")[..32];

    /// <summary>Hashes canonical application data for immutable request and asset identity checks.</summary>
    public static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

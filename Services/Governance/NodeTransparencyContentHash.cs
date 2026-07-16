using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Services.Governance;

public static class NodeTransparencyContentHash
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Compute<T>(string domain, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"AZOA.NodeTransparency.v1\0{domain}\0"));
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

public static class NodeTransparencyMessages
{
    public const string InvalidCursor = "The transparency cursor is invalid or no longer valid.";
    public const string Unavailable = "Node transparency is temporarily unavailable.";
}

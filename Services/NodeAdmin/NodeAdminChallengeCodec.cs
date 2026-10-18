using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>The authenticated contents of a stateless challenge id; the client signs these exact strings.</summary>
public sealed record NodeAdminChallengePayload(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("p")] string Purpose,
    [property: JsonPropertyName("fp")] string Fingerprint,
    [property: JsonPropertyName("aud")] string Audience,
    [property: JsonPropertyName("n")] string Nonce,
    [property: JsonPropertyName("exp")] string ExpiresAt);

/// <summary>
/// Stateless login challenges (NEW-1): <c>challengeId = base64url(payload ‖ HMAC-SHA256(key, payload))</c>, so issuing a challenge
/// writes nothing; see <c>Services/NodeAdmin/AGENTS.md</c> §auth.
/// </summary>
public sealed class NodeAdminChallengeCodec
{
    public const int CurrentVersion = 1;
    private const int MacLength = 32;
    private const int MaxEncodedLength = 2048;
    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("azoa-node-admin-challenge-v1");
    private readonly byte[] _key;

    public NodeAdminChallengeCodec(IOptions<NodeAdminOptions> options, IConfiguration configuration)
    {
        var secret = !string.IsNullOrWhiteSpace(options.Value.ChallengeKey)
            ? options.Value.ChallengeKey
            : configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key missing.");
        // HKDF (RFC 5869) domain-separates the challenge MAC key from every other use of the input secret.
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, [], KeyInfo);
    }

    public string Encode(NodeAdminChallengePayload payload)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        return NodeAdminCrypto.Base64Url([.. body, .. HMACSHA256.HashData(_key, body)]);
    }

    /// <summary>Decodes and authenticates a challenge id (constant-time MAC check); false for anything not minted by this key.</summary>
    public bool TryDecode(string? challengeId, out NodeAdminChallengePayload payload)
    {
        payload = null!;
        if (string.IsNullOrEmpty(challengeId) || challengeId.Length > MaxEncodedLength || challengeId.Contains('=')
            || !System.Buffers.Text.Base64Url.IsValid(challengeId, out var length) || length <= MacLength)
            return false;
        var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(challengeId);
        var body = bytes.AsSpan(0, bytes.Length - MacLength);
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, body), bytes.AsSpan(bytes.Length - MacLength))
            || !string.Equals(NodeAdminCrypto.Base64Url(bytes), challengeId, StringComparison.Ordinal))
            return false;
        NodeAdminChallengePayload? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<NodeAdminChallengePayload>(body);
        }
        catch (JsonException)
        {
            return false;
        }
        if (decoded is not { Version: CurrentVersion } || decoded.Id is not { Length: 32 } || decoded.Purpose is null
            || decoded.Fingerprint is null || decoded.Audience is null || decoded.Nonce is null || decoded.ExpiresAt is null)
            return false;
        payload = decoded;
        return true;
    }
}

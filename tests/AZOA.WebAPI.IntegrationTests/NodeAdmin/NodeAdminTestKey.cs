extern alias BCCrypto2;
using System.Security.Cryptography;
using System.Text;
using Ed25519PrivateKeyParameters = BCCrypto2::Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters;
using Ed25519Signer = BCCrypto2::Org.BouncyCastle.Crypto.Signers.Ed25519Signer;

namespace AZOA.WebAPI.IntegrationTests.NodeAdmin;

/// <summary>A client-side Ed25519 key that signs exactly like the CLI: raw signature over UTF-8 bytes, base64url without padding.</summary>
public sealed class NodeAdminTestKey
{
    private readonly Ed25519PrivateKeyParameters _private;

    private NodeAdminTestKey(byte[] seed)
    {
        _private = new Ed25519PrivateKeyParameters(seed, 0);
        var raw = _private.GeneratePublicKey().GetEncoded();
        PublicKey = Base64Url(raw);
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(raw))[..16];
    }

    public string PublicKey { get; }

    public string Fingerprint { get; }

    public static NodeAdminTestKey Generate() => new(RandomNumberGenerator.GetBytes(32));

    public string Sign(string message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _private);
        var bytes = Encoding.UTF8.GetBytes(message);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return Base64Url(signer.GenerateSignature());
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

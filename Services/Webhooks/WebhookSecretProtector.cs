using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Services.Webhooks;

/// <summary>Authenticates webhook secrets against their tenant, registration and rotation revision.</summary>
public sealed class WebhookSecretProtector(WalletKeyService keys)
{
    private const string Prefix = "webhook-secret-v1:";
    private const string Purpose = "azoa:webhook:hmac:1";
    private sealed record Envelope(string Purpose, Guid TenantId, Guid RegistrationId, long Revision, string Secret);

    public string Protect(Guid tenantId, Guid registrationId, long revision, string secret)
    {
        ValidateIdentity(tenantId, registrationId, revision);
        if (string.IsNullOrWhiteSpace(secret) || secret.Length is < 32 or > 512)
            throw new ArgumentException("Webhook secrets must contain 32 to 512 characters.", nameof(secret));
        return Prefix + keys.EncryptSecretEnvelope(JsonSerializer.Serialize(new Envelope(Purpose, tenantId,
            registrationId, revision, secret)));
    }

    public string Unprotect(Guid tenantId, Guid registrationId, long revision, string ciphertext)
    {
        ValidateIdentity(tenantId, registrationId, revision);
        if (string.IsNullOrEmpty(ciphertext) || !ciphertext.StartsWith(Prefix, StringComparison.Ordinal)
            || ciphertext.Length < Prefix.Length + 56 || ciphertext.Length > 8192)
            throw new CryptographicException("Webhook secret envelope is invalid.");
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(keys.DecryptSecretEnvelope(ciphertext[Prefix.Length..]));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or CryptographicException)
        {
            throw new CryptographicException("Webhook secret envelope authentication failed.");
        }
        if (envelope is null || envelope.Purpose != Purpose || envelope.TenantId != tenantId
            || envelope.RegistrationId != registrationId || envelope.Revision != revision
            || string.IsNullOrWhiteSpace(envelope.Secret) || envelope.Secret.Length is < 32 or > 512)
            throw new CryptographicException("Webhook secret envelope identity mismatch.");
        return envelope.Secret;
    }

    public string LegacyFingerprint(string canonicalSnapshot)
        => keys.FingerprintSecretEnvelope("azoa:webhook:legacy-snapshot:1:" + canonicalSnapshot);

    public static string GenerateSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static void ValidateIdentity(Guid tenantId, Guid registrationId, long revision)
    {
        if (tenantId == Guid.Empty || registrationId == Guid.Empty || revision < 1)
            throw new ArgumentException("A webhook secret requires a tenant, registration and positive revision.");
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Services.Webhooks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AZOA.WebAPI.Tests.Webhooks;

public sealed class WebhookSecretProtectorTests
{
    private static WalletKeyService Keys(string? key = "webhook-test-only-platform-key") => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AZOA:WalletEncryptionKey"] = key }).Build());

    [Fact]
    public void Randomized_ciphertext_roundtrips_only_for_exact_identity_and_revision()
    {
        var protector = new WebhookSecretProtector(Keys());
        var tenant = Guid.NewGuid();
        var registration = Guid.NewGuid();
        var secret = WebhookSecretProtector.GenerateSecret();
        var first = protector.Protect(tenant, registration, 1, secret);
        var second = protector.Protect(tenant, registration, 1, secret);
        first.Should().NotBe(second).And.NotContain(secret);
        protector.Unprotect(tenant, registration, 1, first).Should().Be(secret);
        Action otherTenant = () => protector.Unprotect(Guid.NewGuid(), registration, 1, first);
        Action otherRegistration = () => protector.Unprotect(tenant, Guid.NewGuid(), 1, first);
        Action otherRevision = () => protector.Unprotect(tenant, registration, 2, first);
        otherTenant.Should().Throw<CryptographicException>();
        otherRegistration.Should().Throw<CryptographicException>();
        otherRevision.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Tampering_plaintext_and_other_envelope_purposes_cannot_be_decrypted()
    {
        var keys = Keys();
        var protector = new WebhookSecretProtector(keys);
        var tenant = Guid.NewGuid();
        var registration = Guid.NewGuid();
        var secret = WebhookSecretProtector.GenerateSecret();
        var cipher = protector.Protect(tenant, registration, 1, secret);
        var tampered = cipher[..^1] + (cipher[^1] == '0' ? '1' : '0');
        var wrongPurpose = "webhook-secret-v1:" + keys.EncryptSecretEnvelope(JsonSerializer.Serialize(new
            { Purpose = "another-purpose", TenantId = tenant, RegistrationId = registration, Revision = 1, Secret = secret }));
        foreach (var invalid in new[] { secret, "webhook-secret-v1:00", tampered, wrongPurpose })
        {
            Action decrypt = () => protector.Unprotect(tenant, registration, 1, invalid);
            var failure = decrypt.Should().Throw<CryptographicException>().Which;
            failure.ToString().Should().NotContain(secret);
        }
        Action wrongKey = () => new WebhookSecretProtector(Keys("different-key"))
            .Unprotect(tenant, registration, 1, cipher);
        wrongKey.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_platform_key_is_configuration_failure(string? key)
    {
        Action create = () => Keys(key);
        create.Should().Throw<InvalidOperationException>();
    }
}

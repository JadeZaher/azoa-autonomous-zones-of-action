using System.Collections.Generic;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using AZOA.WebAPI.Services.Signing;
using Xunit;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// final-hardening B5: the zeroable byte[] decrypt path. The cleartext key is
/// returned as raw bytes (never an immutable hex string) and the caller can zero it.
/// </summary>
public class WalletKeyServiceByteDecryptTests
{
    private static WalletKeyService NewKeyService(string secret = "byte-decrypt-test-key-AAAA")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = secret,
            })
            .Build();
        return new WalletKeyService(config);
    }

    [Fact]
    public void DecryptPrivateKeyBytes_returns_the_raw_key_bytes_not_the_hex()
    {
        var svc = NewKeyService();
        // 64-byte Solana-shaped secret.
        var raw = new byte[64];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i + 1);
        var hex = Convert.ToHexString(raw).ToLowerInvariant();

        var enc = svc.EncryptPrivateKey(hex);
        var recovered = svc.DecryptPrivateKeyBytes(enc);

        // Raw key bytes, NOT the 128-char hex ASCII.
        recovered.Should().Equal(raw);
        recovered.Length.Should().Be(64);
    }

    [Fact]
    public void DecryptPrivateKeyBytes_matches_the_string_path_after_hex_decode()
    {
        var svc = NewKeyService();
        var raw = RandomNumberGenerator.GetBytes(32);
        var hex = Convert.ToHexString(raw).ToLowerInvariant();
        var enc = svc.EncryptPrivateKey(hex);

        var viaString = Convert.FromHexString(svc.DecryptPrivateKey(enc));
        var viaBytes = svc.DecryptPrivateKeyBytes(enc);

        viaBytes.Should().Equal(viaString, "the byte[] path must agree with the legacy string path");
    }

    [Fact]
    public void DecryptPrivateKeyBytes_buffer_is_caller_zeroable()
    {
        var svc = NewKeyService();
        var raw = RandomNumberGenerator.GetBytes(64);
        var enc = svc.EncryptPrivateKey(Convert.ToHexString(raw).ToLowerInvariant());

        var key = svc.DecryptPrivateKeyBytes(enc);
        key.Any(b => b != 0).Should().BeTrue("key has real bytes before zeroing");

        CryptographicOperations.ZeroMemory(key);
        key.All(b => b == 0).Should().BeTrue("the returned buffer is a plain zeroable array");
    }

    [Fact]
    public void DecryptPrivateKeyBytes_fails_closed_under_the_wrong_key()
    {
        var enc = NewKeyService("KEY-A").EncryptPrivateKey("00112233445566778899aabbccddeeff");
        var wrong = NewKeyService("KEY-B");

        var act = () => wrong.DecryptPrivateKeyBytes(enc);

        act.Should().Throw<CryptographicException>("a wrong wrapping key must fail closed, never yield garbage plaintext");
    }
}

using System.Security.Cryptography;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.Options;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttStageKeyProtectorTests
{
    [Fact]
    public void RotationRetainsOriginalSeedAndRewrapAllowsOldVersionRetirement()
    {
        var binding = Binding();
        var seed = RandomNumberGenerator.GetBytes(32);
        var firstKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var nextKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var first = Protector("v1", new() { ["v1"] = firstKey });
        var envelope = first.Protect(binding, "public-key", seed);
        using var rotated = Protector("v2", new() { ["v1"] = firstKey, ["v2"] = nextKey });
        Assert.True(rotated.WithSeed(binding, "public-key", envelope, recovered => recovered.SequenceEqual(seed)));
        var rewrapped = rotated.Rewrap(binding, "public-key", envelope);
        Assert.Equal("v2", rewrapped.KeyVersion);
        using var retired = Protector("v2", new() { ["v2"] = nextKey });
        Assert.True(retired.WithSeed(binding, "public-key", rewrapped, recovered => recovered.SequenceEqual(seed)));
        Assert.Throws<InvalidOperationException>(() => retired.WithSeed(binding, "public-key", envelope, _ => true));
        CryptographicOperations.ZeroMemory(seed);
    }

    [Fact]
    public void EveryAuthorityAndGenerationFieldIsAuthenticated()
    {
        using var protector = Protector("v1", new() { ["v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
        var binding = Binding();
        var seed = RandomNumberGenerator.GetBytes(32);
        var envelope = protector.Protect(binding, "public-key", seed);
        var alternatives = new[]
        {
            binding with { ActorUserId = Guid.NewGuid() }, binding with { TenantId = Guid.NewGuid() },
            binding with { WalletId = Guid.NewGuid() }, binding with { OperationId = "other" },
            binding with { ProtocolIdentity = "other-genesis" }, binding with { StageRole = "message" },
            binding with { Generation = 1 }, binding with { SigningScope = AzoaScopes.WttSourceSign },
        };
        foreach (var changed in alternatives)
            Assert.Throws<AuthenticationTagMismatchException>(() => protector.WithSeed(changed, "public-key", envelope, _ => true));
        Assert.Throws<AuthenticationTagMismatchException>(() => protector.WithSeed(binding, "other-key", envelope, _ => true));
        CryptographicOperations.ZeroMemory(seed);
    }

    [Fact]
    public void PlaintextCallbackBufferIsZeroedOnSuccessAndException()
    {
        using var protector = Protector("v1", new() { ["v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
        var binding = Binding();
        var seed = RandomNumberGenerator.GetBytes(32);
        var envelope = protector.Protect(binding, "public-key", seed);
        byte[]? captured = null;
        protector.WithSeed(binding, "public-key", envelope, bytes => { captured = bytes; return true; });
        Assert.All(captured!, value => Assert.Equal(0, value));
        Assert.Throws<InvalidOperationException>(() => protector.WithSeed<bool>(binding, "public-key", envelope,
            bytes => { captured = bytes; throw new InvalidOperationException("callback failed"); }));
        Assert.All(captured!, value => Assert.Equal(0, value));
        CryptographicOperations.ZeroMemory(seed);
    }

    private static WttStageKeyBinding Binding() => new("solana:local-genesis", Guid.NewGuid(), null,
        Guid.NewGuid(), "operation", "signature-set", 0, AzoaScopes.WttTargetSign);
    private static WttStageKeyProtector Protector(string active, Dictionary<string, string> keys)
        => new(Options.Create(new WttStageKeyOptions { ActiveKeyId = active, Keys = keys }));
}

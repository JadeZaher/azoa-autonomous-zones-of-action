using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Wormhole;
using AZOA.WebAPI.Tests.TestSupport;
using Microsoft.Extensions.Options;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

/// <summary>Keyring rotation as an operator runs it: degrade while a retained version is missing, rewrap, then retire.</summary>
public sealed class WttStageKeyReadinessTests
{
    [Fact]
    public async Task RotationDegradesWhileARetainedVersionIsMissingAndRewrapLetsTheOldKeyRetireWithTheSameSeed()
    {
        string oldKey = Key(), newKey = Key();
        var store = new InMemoryWttStageKeyStore();
        var binding = new WttStageKeyBinding("solana:local-genesis", Guid.NewGuid(), null, Guid.NewGuid(), "operation", "Target:Verify:payer", 0,
            AzoaScopes.WttTargetSign);
        var seed = RandomNumberGenerator.GetBytes(32);
        using (var original = Protector("v1", new() { ["v1"] = oldKey }))
            await store.ClaimAsync(Candidate(binding, original, seed));
        var state = new WttStageKeyReadinessState();

        using var retiredTooEarly = Protector("v2", new() { ["v2"] = newKey });
        var degraded = await new WttStageKeyReadiness(store, retiredTooEarly, state).CheckAsync();
        Assert.Equal(("Degraded", false), (degraded.Status, state.Ready));
        Assert.NotNull(degraded.UnreadableEnvelopeId);

        using var rotated = Protector("v2", new() { ["v1"] = oldKey, ["v2"] = newKey });
        var readiness = new WttStageKeyReadiness(store, rotated, state);
        Assert.Equal((true, true), ((await readiness.CheckAsync()).Ready, (await readiness.CheckAsync()).RewrapPending));
        var pass = await readiness.RewrapAsync();
        Assert.Equal((1, 0, false), (pass.Rewrapped, pass.Conflicted, pass.RewrapPending));

        Assert.True((await new WttStageKeyReadiness(store, retiredTooEarly, state).CheckAsync()).Ready);
        var row = (await store.FindNotOnVersionAsync("none", 10)).Single();
        Assert.Equal("v2", row.KeyVersion);
        Assert.True(retiredTooEarly.WithSeed(binding, row.PublicKey, JsonSerializer.Deserialize<WttProtectedStageKey>(row.EnvelopeJson)!,
            recovered => recovered.SequenceEqual(seed)));
        CryptographicOperations.ZeroMemory(seed);
    }

    [Fact]
    public async Task AnAbsentKeyringDegradesInsteadOfFailingStartupAndRefusesEveryProtectedUse()
    {
        using var unconfigured = new WttStageKeyProtector(Options.Create(new WttStageKeyOptions()));
        var report = await new WttStageKeyReadiness(new InMemoryWttStageKeyStore(), unconfigured).CheckAsync();
        Assert.Equal(("Degraded", false), (report.Status, report.Configured));
        var binding = new WttStageKeyBinding("solana:local-genesis", Guid.NewGuid(), null, Guid.NewGuid(), "operation", "Source:Transfer:payer", 0,
            AzoaScopes.WttSourceSign);
        Assert.Throws<InvalidOperationException>(() => unconfigured.Protect(binding, "public-key", new byte[32]));
    }

    private static WttStageKeyEnvelope Candidate(WttStageKeyBinding binding, WttStageKeyProtector protector, byte[] seed)
    {
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding));
        var envelope = protector.Protect(binding, "public-key", seed);
        return new() { Id = CanonicalJson.HashCanonical(json), BindingJson = json, PublicKey = "public-key", KeyVersion = envelope.KeyVersion,
            EnvelopeJson = JsonSerializer.Serialize(envelope), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
    }

    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static WttStageKeyProtector Protector(string active, Dictionary<string, string> keys)
        => new(Options.Create(new WttStageKeyOptions { ActiveKeyId = active, Keys = keys }));
}

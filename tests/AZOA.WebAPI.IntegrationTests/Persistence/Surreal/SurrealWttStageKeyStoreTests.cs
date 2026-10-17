using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.Options;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealWttStageKeyStoreTests : IAsyncLifetime
{
    private readonly string space = $"test{Guid.NewGuid():N}";
    private ISurrealConnection connection = null!;
    private SurrealWttStageKeyStore store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(space, "wtt_stage_key");
        connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = space, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        store = new(new DefaultSurrealExecutor(connection));
    }

    public async Task DisposeAsync()
    {
        connection?.Dispose();
        await SurrealTestSchema.DropAsync(space);
    }

    [Fact]
    public async Task OverlapAndLostClaimResponseKeepOneProtectedSeedBeforeSigning()
    {
        var binding = Binding();
        using var protector = Protector("v1", new() { ["v1"] = Key() });
        var first = Candidate(binding, protector, "first-public-key");
        var second = Candidate(binding, protector, "second-public-key");
        var claims = await Task.WhenAll(store.ClaimAsync(first), store.ClaimAsync(second));
        Assert.Equal(claims[0].PublicKey, claims[1].PublicKey);
        Assert.Equal(claims[0].EnvelopeJson, claims[1].EnvelopeJson);
        var recovered = await store.GetAsync(first.Id);
        Assert.Equal(claims[0].EnvelopeJson, recovered!.EnvelopeJson);
        var replay = await store.ClaimAsync(Candidate(binding, protector, "rotated-wallet-candidate"));
        Assert.Equal(recovered.PublicKey, replay.PublicKey);
        Assert.Equal(recovered.EnvelopeJson, replay.EnvelopeJson);
    }

    [Fact]
    public async Task RewrapUsesExactEnvelopeCasAndReadinessPreventsPrematureRetirement()
    {
        var binding = Binding();
        string oldKey = Key(), newKey = Key();
        using var original = Protector("v1", new() { ["v1"] = oldKey });
        var saved = await store.ClaimAsync(Candidate(binding, original, "public-key"));
        using var rotated = Protector("v2", new() { ["v1"] = oldKey, ["v2"] = newKey });
        using var retired = Protector("v2", new() { ["v2"] = newKey });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WttStageKeyReadiness(store, retired).EnsureReadyAsync());
        var rewrapped = rotated.Rewrap(binding, saved.PublicKey, JsonSerializer.Deserialize<WttProtectedStageKey>(saved.EnvelopeJson)!);
        Assert.True(await store.TryRewrapAsync(saved, rewrapped.KeyVersion, JsonSerializer.Serialize(rewrapped)));
        Assert.False(await store.TryRewrapAsync(saved, "v1", saved.EnvelopeJson));
        await new WttStageKeyReadiness(store, retired).EnsureReadyAsync();
        var current = (await store.GetAsync(saved.Id))!;
        Assert.Equal(saved.PublicKey, current.PublicKey);
        Assert.Equal(saved.BindingJson, current.BindingJson);
        Assert.Equal(1, current.Version);
    }

    private static WttStageKeyEnvelope Candidate(WttStageKeyBinding binding, WttStageKeyProtector protector, string publicKey)
    {
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding));
        var seed = RandomNumberGenerator.GetBytes(32);
        try
        {
            var envelope = protector.Protect(binding, publicKey, seed);
            return new() { Id = CanonicalJson.HashCanonical(json), BindingJson = json, PublicKey = publicKey,
                KeyVersion = envelope.KeyVersion, EnvelopeJson = JsonSerializer.Serialize(envelope),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    private static WttStageKeyBinding Binding() => new("solana:local-genesis", Guid.NewGuid(), null,
        Guid.NewGuid(), Guid.NewGuid().ToString("N"), "message", 0, AzoaScopes.WttSourceSign);
    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static WttStageKeyProtector Protector(string active, Dictionary<string, string> keys)
        => new(Options.Create(new WttStageKeyOptions { ActiveKeyId = active, Keys = keys }));
}

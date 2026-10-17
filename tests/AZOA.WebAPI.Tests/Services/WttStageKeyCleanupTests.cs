using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttStageKeyCleanupTests
{
    [Theory]
    [InlineData("owner", false)]
    [InlineData("actor", true)]
    [InlineData("wallet", true)]
    [InlineData("tenant", true)]
    [InlineData("scope", true)]
    [InlineData("platform", true)]
    [InlineData("chain", true)]
    [InlineData("message-key", true)]
    [InlineData("signature-set", true)]
    public async Task Cleanup_uses_existing_bound_key_only_under_fresh_direct_original_wallet_custody(string mutation, bool rejected)
    {
        var binding = new WttStageKeyBinding("solana:local-genesis", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "original-operation", "Source:Transfer:payer", 3, AzoaScopes.WttSourceSign);
        if (mutation == "message-key") binding = binding with { StageRole = "Source:Transfer:message" };
        if (mutation == "signature-set") binding = binding with { StageRole = "Target:Verification:proof:signature-set", SigningScope = AzoaScopes.WttTargetSign };
        var context = SigningContext.ForUser(binding.ActorUserId, binding.WalletId).ForChain("Solana") with { Scope = AzoaScopes.WttCleanupSign };
        context = mutation switch
        {
            "actor" => context with { UserId = Guid.NewGuid() }, "wallet" => context with { WalletId = Guid.NewGuid() },
            "tenant" => context.ActingAs(binding.TenantId!.Value, AzoaScopes.WttCleanupSign),
            "scope" => context with { Scope = AzoaScopes.WttSourceSign }, "platform" => SigningContext.PlatformFor("Solana"),
            "chain" => context.ForChain("Algorand"), _ => context,
        };
        using var protector = new WttStageKeyProtector(Options.Create(new WttStageKeyOptions
            { ActiveKeyId = "fixture", Keys = new() { ["fixture"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) } }));
        var seed = RandomNumberGenerator.GetBytes(32);
        string Public(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        var publicKey = Public(seed); var encrypted = protector.Protect(binding, publicKey, seed);
        CryptographicOperations.ZeroMemory(seed);
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding));
        var row = new WttStageKeyEnvelope { Id = CanonicalJson.HashCanonical(json), BindingJson = json, PublicKey = publicKey,
            KeyVersion = encrypted.KeyVersion, EnvelopeJson = JsonSerializer.Serialize(encrypted) };
        var store = new Mock<IWttStageKeyStore>(MockBehavior.Strict);
        store.Setup(value => value.GetAsync(row.Id, It.IsAny<CancellationToken>())).ReturnsAsync(row);
        var custody = new Mock<IKeyCustodyService>(MockBehavior.Strict);
        custody.Setup(value => value.WithSigningKeyAsync(context, It.IsAny<Func<byte[], Task<bool>>>()))
            .Returns(async (SigningContext _, Func<byte[], Task<bool>> use) => AZOAResult<bool>.Success(await use([1])));
        var vault = new WttStageKeyVault(store.Object, protector, custody.Object);
        var result = await vault.UseCleanupAsync(binding, context, Public, bytes => Public(bytes) == publicKey);
        Assert.Equal(rejected, result.IsError);
        if (!rejected) Assert.True(result.Result);
        custody.Verify(value => value.WithSigningKeyAsync(It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<bool>>>()),
            rejected ? Times.Never() : Times.Once());
        store.Verify(value => value.ClaimAsync(It.IsAny<WttStageKeyEnvelope>(), It.IsAny<CancellationToken>()), Times.Never());
    }
}

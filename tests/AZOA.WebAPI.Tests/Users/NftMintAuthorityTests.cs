using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Users;

public sealed class NftMintAuthorityTests
{
    [Fact]
    public async Task CredentialContinuationRetainsOriginalPlatformSignerAndRefusesConfigurationRotation()
    {
        var harness = new NftLifecycleHarness();
        var actor = Guid.NewGuid(); var recipient = Guid.NewGuid();
        harness.AddWallet(actor); harness.AddWallet(recipient);
        var request = NftLifecycleHarness.MintRequest(false);
        request.IsSoulbound = true;
        var issued = await harness.Service.MintAsync(actor, recipient, request, "credential",
            credential: NftLifecycleHarness.Credential(actor));
        issued.IsError.Should().BeFalse();
        var saved = (await harness.Store.GetAsync(issued.Result!.OperationId))!;
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(saved.RequestJson)!;
        command.PlatformAuthorityAddress.Should().Be("platform");
        var stage = JsonSerializer.Deserialize<AZOA.WebAPI.Persistence.SurrealDb.Models.NftLifecycle>(JsonSerializer.Serialize(saved))!;
        stage.Stage = NftTransactionKinds.Deliver;
        NftLifecycleIntentFactory.Create(stage, command, ChainNetwork.Devnet, "platform", Guid.Empty)!
            .SignerAddress.Should().Be("platform");
        NftLifecycleIntentFactory.Create(stage, command, ChainNetwork.Devnet, "rotated-platform", Guid.Empty).Should().BeNull();
        NftLifecycleIntentFactory.Create(stage, command with { PlatformAuthorityAddress = null },
            ChainNetwork.Devnet, "platform", Guid.Empty).Should().BeNull();
        (await harness.Store.GetAsync(saved.Id))!.RequestJson.Should().Be(saved.RequestJson);
    }

    [Fact]
    public async Task MissingOrDifferentCredentialCannotAdmitDirectMint()
    {
        var harness = new NftLifecycleHarness();
        var actor = Guid.NewGuid();
        harness.AddWallet(actor);
        var missing = await harness.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "missing");
        var wrong = await harness.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "wrong",
            credential: NftLifecycleHarness.Credential(Guid.NewGuid()));
        missing.IsError.Should().BeTrue();
        wrong.IsError.Should().BeTrue();
        harness.Prepared.Should().BeEmpty();
        harness.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task AdmissionRetainsExactCredentialAndOwnedWalletWithoutReplacingThemOnReplay()
    {
        var harness = new NftLifecycleHarness();
        var actor = Guid.NewGuid();
        var wallet = harness.AddWallet(actor);
        var credential = NftLifecycleHarness.Credential(actor);
        var draft = NftLifecycleHarness.MintRequest();
        var result = await harness.Service.MintAsync(actor, actor, draft, "original", credential: credential);
        result.IsError.Should().BeFalse();
        var row = (await harness.Store.GetAsync(result.Result!.OperationId))!;
        var original = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        original.ActorAuthority!.Credential.Should().BeEquivalentTo(credential);
        original.ActorAuthority.Wallet.Id.Should().Be(wallet.Id);
        original.ActorAuthority.Wallet.Address.Should().Be(wallet.Address);
        var count = harness.Prepared.Count;
        var replay = await harness.Service.MintAsync(actor, actor, draft, "original",
            credential: NftLifecycleHarness.Credential(actor) with { IssuedAt = DateTimeOffset.UtcNow });
        replay.Result!.OperationId.Should().Be(result.Result.OperationId);
        (await harness.Store.GetAsync(row.Id))!.RequestJson.Should().Be(row.RequestJson);
        harness.Prepared.Should().HaveCount(count);
    }
}

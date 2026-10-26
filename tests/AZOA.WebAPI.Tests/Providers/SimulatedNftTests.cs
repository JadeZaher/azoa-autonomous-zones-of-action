using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Simulated;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AZOA.WebAPI.Tests.Providers;

/// <summary>Simulated NFT lifecycle through the factory-resolved module seam (prepare → observe → submit → ownership).</summary>
public sealed class SimulatedNftTests
{
    private static readonly string Alice = SimulatedBlockchainProvider.SimAddress("Simulated", "nft-alice");
    private static readonly string Bob = SimulatedBlockchainProvider.SimAddress("Simulated", "nft-bob");

    private static INftTransactionModule Module()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Blockchain:Mode"] = "Simulated" })
            .Build();
        BlockchainProviderRegistration[] registrations =
            [new("Simulated", () => new SimulatedBlockchainProvider(config, NullLogger<SimulatedBlockchainProvider>.Instance))];
        new BlockchainProviderFactory(registrations, config).GetProvider("Simulated", ChainNetwork.Devnet)
            .TryGetModule<INftTransactionModule>(out var module).Should().BeTrue();
        return module!;
    }

    /// <summary>A stage intent that satisfies the shared authorization contract; the signer is the acting participant.</summary>
    private static NftTransactionIntent Intent(string kind, string operation, string signer, string recipient, string? assetId,
        string lifecycleId, bool soulbound = false, string fee = "0")
    {
        var actor = Guid.NewGuid();
        var context = SigningContext.ForUser(actor, Guid.NewGuid()).ForChain("Simulated");
        var quote = new NftFeeQuote("Simulated", ChainNetwork.Devnet, operation, 1, SimulatedBlockchainProvider.SimCurrency, 0, fee, "", actor);
        var final = kind is NftTransactionKinds.Transfer or NftTransactionKinds.Burn || kind == NftTransactionKinds.Create && !soulbound;
        var grant = new NftOperationAuthorization(lifecycleId, operation, kind, 0, actor, signer, context,
            soulbound ? actor : null, soulbound ? signer : null, soulbound ? context : null, final, quote);
        return new NftTransactionIntent(kind, "Simulated", ChainNetwork.Devnet, signer, recipient, assetId, "Fixture", "ipfs://fixture",
            soulbound, context, PreparationKey: $"{lifecycleId}:{kind}:0", Authorization: grant,
            Fee: final && fee != "0" ? new NftFeeIntent(quote, signer, context) : null,
            ContractAddress: kind == NftTransactionKinds.Create ? "" : SimulatedBlockchainProvider.SimNftContract);
    }

    private static async Task<NftTransactionObservation> SettleAsync(INftTransactionModule module, NftTransactionIntent intent)
    {
        var prepared = await module.PrepareNftTransactionAsync(intent);
        prepared.IsError.Should().BeFalse(prepared.Message);
        (await module.ObserveNftTransactionAsync(prepared.Result!)).Result!.Status.Should().Be("Unseen");
        // Round-trips through JSON the way NftLifecycleService persists PreparedTransactionJson.
        var persisted = JsonSerializer.Deserialize<PreparedNftTransaction>(JsonSerializer.Serialize(prepared.Result))!;
        await module.SubmitNftTransactionAsync(persisted);
        await module.SubmitNftTransactionAsync(persisted);
        return (await module.ObserveNftTransactionAsync(persisted)).Result!;
    }

    [Fact]
    public async Task MintTransferBurnMovesOwnershipOnceAndBurnEndsIt()
    {
        var module = Module();
        var mint = Guid.NewGuid().ToString("N");
        var created = await SettleAsync(module, Intent(NftTransactionKinds.Create, "Mint", Alice, Alice, null, mint));
        created.Status.Should().Be("Confirmed");
        created.AssetId.Should().Be(SimulatedBlockchainProvider.SimNftAssetId(mint));
        ulong.Parse(created.AssetId!, CultureInfo.InvariantCulture).Should().BePositive();
        created.OwnerAddress.Should().Be(Alice);
        created.ContractAddress.Should().Be(SimulatedBlockchainProvider.SimNftContract);

        var asset = created.AssetId!;
        var protocol = created.ProtocolIdentity;
        (await module.ReadNftOwnershipAsync(asset, Alice, protocol, contractAddress: created.ContractAddress)).Result!.OwnerAddress.Should().Be(Alice);

        var moved = await SettleAsync(module, Intent(NftTransactionKinds.Transfer, "Transfer", Alice, Bob, asset, Guid.NewGuid().ToString("N")));
        moved.Status.Should().Be("Confirmed");
        (await module.ReadNftOwnershipAsync(asset, Bob, protocol, contractAddress: SimulatedBlockchainProvider.SimNftContract))
            .Result.Should().Be(new NftOwnershipObservation(true, Bob, false));

        var burned = await SettleAsync(module, Intent(NftTransactionKinds.Burn, "Burn", Bob, Bob, asset, Guid.NewGuid().ToString("N")));
        burned.Status.Should().Be("Confirmed");
        (await module.ReadNftOwnershipAsync(asset, Bob, protocol, contractAddress: SimulatedBlockchainProvider.SimNftContract))
            .Result!.Exists.Should().BeFalse();
    }

    [Fact]
    public async Task StageSignedByANonOwnerFailsOnChainAndLeavesOwnershipUntouched()
    {
        var module = Module();
        var created = await SettleAsync(module, Intent(NftTransactionKinds.Create, "Mint", Alice, Alice, null, Guid.NewGuid().ToString("N")));

        var stolen = await SettleAsync(module, Intent(NftTransactionKinds.Transfer, "Transfer", Bob, Bob, created.AssetId, Guid.NewGuid().ToString("N")));

        stolen.Status.Should().Be("FailedOnChain");
        (await module.ReadNftOwnershipAsync(created.AssetId!, Alice, created.ProtocolIdentity, contractAddress: created.ContractAddress))
            .Result!.OwnerAddress.Should().Be(Alice);
    }

    public static TheoryData<string, NftTransactionIntent> Refused => new()
    {
        { "soulbound credential", Intent(NftTransactionKinds.Create, "Mint", Alice, Bob, null, Guid.NewGuid().ToString("N"), soulbound: true) },
        { "non-zero native fee", Intent(NftTransactionKinds.Create, "Mint", Alice, Alice, null, Guid.NewGuid().ToString("N"), fee: "5") },
        { "real-chain address", Intent(NftTransactionKinds.Create, "Mint", Alice, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", null, Guid.NewGuid().ToString("N")) },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task UnsupportedIntentIsRefusedBeforeAnyCandidateExists(string reason, NftTransactionIntent intent)
    {
        var prepared = await Module().PrepareNftTransactionAsync(intent);

        prepared.IsError.Should().BeTrue(reason);
    }

    [Fact]
    public async Task TamperedCandidateIsRefusedOnSubmitAndObserve()
    {
        var module = Module();
        var prepared = (await module.PrepareNftTransactionAsync(Intent(NftTransactionKinds.Create, "Mint", Alice, Alice, null, Guid.NewGuid().ToString("N")))).Result!;
        var tampered = prepared with { Intent = prepared.Intent with { RecipientAddress = Bob } };

        (await module.ObserveNftTransactionAsync(tampered)).IsError.Should().BeTrue();
        await FluentActions.Awaiting(() => module.SubmitNftTransactionAsync(tampered)).Should().ThrowAsync<InvalidOperationException>();
        (await module.ObserveNftTransactionAsync(prepared)).Result!.Status.Should().Be("Unseen");
    }
}

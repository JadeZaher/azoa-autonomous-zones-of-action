using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Helpers;

public sealed class NftProtocolCostContractTests
{
    [Fact]
    public void DraftExcludesOnlyItsOwnAcceptanceAndBindsRecipientMetadataAndPlatformFee()
    {
        var actor = Guid.NewGuid();
        var request = new UserNFTMintModel { Name = "original", MetadataURI = "ipfs://original" };
        var hash = NftProtocolCostContract.DraftHash("operation", actor, actor, request);
        request.ProtocolCostAuthorization = new("receipt", "hash", new("1", "2", "3"));
        NftProtocolCostContract.DraftHash("operation", actor, actor, request).Should().Be(hash);
        NftProtocolCostContract.DraftHash("operation", actor, Guid.NewGuid(), request).Should().NotBe(hash);
        NftProtocolCostContract.DraftHash("different", actor, actor, request).Should().NotBe(hash);
        request.Name = "changed";
        NftProtocolCostContract.DraftHash("operation", actor, actor, request).Should().NotBe(hash);
        request.Name = "original";
        request.FeeAuthorization = new(1, "7", "fee-quote");
        NftProtocolCostContract.DraftHash("operation", actor, actor, request).Should().NotBe(hash);
    }

    [Fact]
    public void AcceptedReceiptCannotBeReboundOrRepriced()
    {
        var quote = Quote();
        var accepted = new NftProtocolCostAuthorization(quote.QuoteId, NftProtocolCostContract.Hash(quote), quote.Limits);
        NftProtocolCostContract.IsAuthorized(quote, accepted).Should().BeTrue();
        NftProtocolCostQuote[] mutations =
        [
            quote with { QuoteId = Guid.NewGuid().ToString("N") },
            quote with { ActorUserId = Guid.NewGuid() }, quote with { ActingTenantId = Guid.NewGuid() },
            quote with { PayerWalletId = Guid.NewGuid() }, quote with { PayerAddress = "other" },
            quote with { ChainType = "other" }, quote with { Network = ChainNetwork.Mainnet },
            quote with { PhysicalNetworkIdentity = "other-genesis" }, quote with { LifecycleId = "other" },
            quote with { DraftHash = new string('b', 64) }, quote with { Stage = "Accept" },
            quote with { PreparationGeneration = 1 }, quote with { TokenStandard = "credential" },
            quote with { PolicyEvidenceJson = "{\"revision\":2}" },
            quote with { Limits = quote.Limits with { ProtocolLevy = "11" } },
            quote with { CreatedAt = quote.CreatedAt.AddTicks(1) }, quote with { ExpiresAt = quote.ExpiresAt.AddTicks(1) },
        ];
        foreach (var mutation in mutations)
            NftProtocolCostContract.IsAuthorized(mutation, accepted).Should().BeFalse();
        NftProtocolCostContract.IsAuthorized(quote, accepted with { Limits = new("999", "999", "999") }).Should().BeFalse();
    }

    [Theory]
    [InlineData("11", "0", "0")]
    [InlineData("0", "11", "0")]
    [InlineData("0", "0", "11")]
    [InlineData("01", "0", "0")]
    [InlineData("-1", "0", "0")]
    [InlineData("1e1", "0", "0")]
    public void UnusedCategoriesCannotSubsidizeOverspendOrNoncanonicalAmounts(string fee, string rent, string levy)
        => NftProtocolCostContract.Covers(new("10", "10", "10"), new(fee, rent, levy)).Should().BeFalse();

    [Fact]
    public void AmountsDoNotNarrowToSignedMachineIntegers()
        => NftProtocolCostContract.Covers(new("18446744073709551616", "0", "0"),
            new("18446744073709551615", "0", "0")).Should().BeTrue();

    [Fact]
    public void EvidenceMustBeCanonicalNonemptyObjectWithoutDuplicateKeys()
    {
        foreach (var evidence in new[] { "{}", "[]", "{\"revision\": 1}", "{\"revision\":1,\"revision\":2}" })
            NftProtocolCostContract.IsValid(Quote() with { PolicyEvidenceJson = evidence }).Should().BeFalse();
    }

    [Fact]
    public void HistoricalReceiptIdentitySurvivesExpiryWithoutAuthorizingFreshAdmission()
    {
        var quote = Quote();
        quote.ExpiresAt.Should().BeBefore(DateTimeOffset.UtcNow);
        NftProtocolCostContract.IsAuthorized(quote,
            new(quote.QuoteId, NftProtocolCostContract.Hash(quote), quote.Limits)).Should().BeTrue();
    }

    private static NftProtocolCostQuote Quote() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.Empty,
        Guid.NewGuid(), "payer", "Solana", ChainNetwork.Devnet, "genesis", "lifecycle", new string('a', 64),
        "Create", 0, "NonFungible", "{\"revision\":1}", new("10", "10", "10"),
        DateTimeOffset.Parse("2025-01-01T00:00:00Z"), DateTimeOffset.Parse("2025-01-01T00:05:00Z"));
}

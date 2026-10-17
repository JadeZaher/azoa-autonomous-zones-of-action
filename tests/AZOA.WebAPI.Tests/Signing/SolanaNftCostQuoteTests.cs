using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Wallet;
using static AZOA.WebAPI.Tests.TestSupport.SolanaProtocolCostFixture;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class SolanaNftCostQuoteTests
{
    private const string Prefix = "AZOA:Solana:Nft:Networks:Devnet:";
    private const string Catalogue = "AZOA:Solana:Nft:ReviewedMetadataExecutables";

    [Theory]
    [InlineData(false, "9646560", "MetaplexNonFungible")]
    [InlineData(true, "8616480", "SPLFrozenCredential")]
    public async Task QuoteReadsActualPinnedEconomicsWithoutCustody(bool credential, string rent, string standard)
    {
        using var fixture = new SolanaDurableFixture();
        ConfigureMetadata(fixture, Prefix, Catalogue);
        var intent = fixture.Mint(credential);
        var quote = await fixture.Provider.QuoteNftProtocolCostsAsync(intent);
        quote.IsError.Should().BeFalse(quote.Message);
        quote.Result!.Limits.RetainedRent.Should().Be(rent);
        quote.Result.Limits.ProtocolLevy.Should().Be("10000000");
        quote.Result.Limits.NetworkFee.Should().Be("5000");
        quote.Result.TokenStandard.Should().Be(standard);
        quote.Result.PhysicalNetworkIdentity.Should().Be("solana:" + SolanaDurableFixture.Genesis);
        using var evidence = JsonDocument.Parse(quote.Result.PolicyEvidenceJson);
        evidence.RootElement.GetProperty("Accounts").GetProperty("MasterEditionRent").GetUInt64()
            .Should().Be(credential ? 0UL : 1030080UL);
        fixture.Consents.Should().BeEmpty(); fixture.Sends.Should().Be(0);
    }

    [Theory]
    [InlineData("program")]
    [InlineData("guard")]
    [InlineData("policy")]
    public async Task UnprovenPolicyCannotProduceQuote(string changed)
    {
        using var fixture = new SolanaDurableFixture();
        ConfigureMetadata(fixture, Prefix, Catalogue);
        if (changed == "program") PutProgram(fixture, SolanaNftMetadata.Program, [9, 9, 9], false);
        if (changed == "guard") PutProgram(fixture, new PublicKey(fixture.Configuration[Prefix + "CostGuardProgram"]!), [1, 2, 3], true);
        if (changed == "policy") fixture.Configuration[Catalogue + ":0:SourceRevision"] = "unreviewed";
        (await fixture.Provider.QuoteNftProtocolCostsAsync(fixture.Mint())).IsError.Should().BeTrue();
        fixture.Consents.Should().BeEmpty(); fixture.Sends.Should().Be(0);
    }

    [Fact]
    public async Task UpdatedRentChangesIndependentCostsWhileKeepingProtocolEvidenceExplicit()
    {
        using var fixture = new SolanaDurableFixture();
        ConfigureMetadata(fixture, Prefix, Catalogue);
        fixture.Accounts[SysVars.RentKey.Key] = BinaryAccount("Sysvar1111111111111111111111111111111111111", Rent(3));
        var quote = await fixture.Provider.QuoteNftProtocolCostsAsync(fixture.Mint());
        quote.IsError.Should().BeFalse(quote.Message);
        quote.Result!.Limits.RetainedRent.Should().Be("14469840");
        quote.Result.Limits.ProtocolLevy.Should().Be("14997280");
        fixture.Consents.Should().BeEmpty(); fixture.Sends.Should().Be(0);
    }
}

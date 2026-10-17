using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Solnet.Programs;
using static AZOA.WebAPI.Tests.TestSupport.SolanaProtocolCostFixture;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class SolanaNftCostPreparationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("payer")]
    [InlineData("generation")]
    [InlineData("network")]
    [InlineData("rent-limit")]
    [InlineData("levy-limit")]
    [InlineData("fee-limit")]
    [InlineData("rent-policy")]
    [InlineData("executable")]
    public async Task ChangedAuthorityOrUncoveredCostsRefuseBeforeCustody(string changed)
    {
        using var fixture = new SolanaDurableFixture();
        var intent = await fixture.QuotedMintAsync();
        var quote = intent.ProtocolCostQuote!;
        if (changed == "missing") intent = intent with { ProtocolCostQuote = null };
        if (changed == "payer") intent = intent with { ProtocolCostQuote = quote with { PayerWalletId = Guid.NewGuid() } };
        if (changed == "generation") intent = intent with { ProtocolCostQuote = quote with { PreparationGeneration = 1 } };
        if (changed == "network") intent = intent with { ProtocolCostQuote = quote with { PhysicalNetworkIdentity = "solana:other" } };
        if (changed == "rent-limit") intent = intent with { ProtocolCostQuote = quote with { Limits = quote.Limits with { RetainedRent = "0" } } };
        if (changed == "levy-limit") intent = intent with { ProtocolCostQuote = quote with { Limits = quote.Limits with { ProtocolLevy = "0" } } };
        if (changed == "fee-limit") fixture.MessageFee = 5001;
        if (changed == "rent-policy") fixture.Accounts[SysVars.RentKey.Key] = BinaryAccount("Sysvar1111111111111111111111111111111111111", Rent(3));
        if (changed == "executable") PutProgram(fixture, SolanaNftMetadata.Program, [9, 9, 9], false);
        (await fixture.Provider.PrepareNftTransactionAsync(intent)).IsError.Should().BeTrue();
        fixture.Consents.Should().BeEmpty(); fixture.Sends.Should().Be(0);
    }

    [Fact]
    public async Task AdmittedQuoteExpiryDoesNotReplaceItsPolicyOrPreventExactPreparation()
    {
        using var fixture = new SolanaDurableFixture();
        var intent = await fixture.QuotedMintAsync();
        var now = DateTimeOffset.UtcNow;
        intent = intent with { ProtocolCostQuote = intent.ProtocolCostQuote! with { CreatedAt = now.AddHours(-2), ExpiresAt = now.AddHours(-1) } };
        var prepared = await fixture.Provider.PrepareNftTransactionAsync(intent);
        prepared.IsError.Should().BeFalse(prepared.Message);
        prepared.Result!.Intent.Should().Be(intent);
        fixture.Consents.Should().ContainSingle(); fixture.Sends.Should().Be(0);
    }

    [Fact]
    public async Task ExactPreparedWireCannotBeSentAboveItsAcceptedFeeLimit()
    {
        using var fixture = new SolanaDurableFixture();
        var intent = await fixture.QuotedMintAsync();
        var prepared = await fixture.Provider.PrepareNftTransactionAsync(intent);
        prepared.IsError.Should().BeFalse(prepared.Message);
        fixture.MessageFee = 5001;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.SubmitNftTransactionAsync(prepared.Result!));
        fixture.Sends.Should().Be(0);
        fixture.Consents.Should().ContainSingle();
    }
}

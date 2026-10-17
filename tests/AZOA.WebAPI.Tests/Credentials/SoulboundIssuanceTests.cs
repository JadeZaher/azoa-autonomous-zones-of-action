using FluentAssertions;

namespace AZOA.WebAPI.Tests.Credentials;

public sealed class SoulboundIssuanceTests
{
    [Fact]
    public async Task Issuance_RecordsDistinctIssuerAndHolderOnlyAfterAcceptanceAndConfirmation()
    {
        var rail = new CredentialRailHarness();
        var issued = await rail.IssueAsync("778899");
        issued.Outcome!.IsError.Should().BeFalse(issued.Outcome.Message);
        issued.Record!.HasVerifiedIssuance.Should().BeTrue();
        issued.Record.IssuerUserId.Should().Be(rail.IssuerA);
        issued.Record.HolderUserId.Should().Be(rail.RecipientB);
        issued.Record.HolderAddress.Should().Be(rail.RecipientAddress);
        issued.Record.IssuerUserId.Should().NotBe(issued.Record.HolderUserId);
        rail.MintedRows.Should().ContainSingle();
        rail.Rail.Prepared.Should().Contain(intent => intent.Kind == "Accept" && intent.SignerAddress == rail.RecipientAddress);
        rail.Rail.Prepared.Should().Contain(intent => intent.Kind == "Deliver");
    }

    [Fact]
    public async Task RecipientWithoutWallet_CannotAcquireAnInventedOwnershipRow()
    {
        var rail = new CredentialRailHarness(); rail.HasNoWallet(rail.RecipientB);
        var issued = await rail.IssueAsync("778899");
        issued.Outcome!.IsError.Should().BeTrue();
        rail.MintedRows.Should().BeEmpty(); rail.Rail.Prepared.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportingAnExistingTokenCannotForgeIssuerProvenance()
    {
        var rail = new CredentialRailHarness();
        var request = CredentialRailHarness.SoulboundModelFor("778899"); request.TokenId = "778899";
        var result = await rail.Rail.Service.MintAsync(rail.IssuerA, rail.RecipientB, request, "forged-import");
        result.IsError.Should().BeTrue(); rail.Rail.Prepared.Should().BeEmpty(); rail.Rail.Store.Ownership.Should().BeEmpty();
    }

    [Fact]
    public async Task PendingCreationDoesNotReportCredentialOwnership()
    {
        var rail = new CredentialRailHarness(); rail.Rail.Confirm = false;
        var result = await rail.Rail.Service.MintAsync(rail.IssuerA, rail.RecipientB, CredentialRailHarness.SoulboundModelFor("778899"), "pending");
        result.Result!.State.Should().NotBe("Completed");
        rail.Rail.Store.Ownership.Should().BeEmpty();
    }
}

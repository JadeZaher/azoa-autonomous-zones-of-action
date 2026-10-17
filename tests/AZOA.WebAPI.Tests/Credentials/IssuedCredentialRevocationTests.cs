// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Credentials;
public sealed class IssuedCredentialRevocationTests
{
    private const string TokenId = "778899";
    private const string OtherTokenId = "112233";

    private readonly CredentialRailHarness _rail = new();
    [Fact]
    public async Task TheHolder_CannotRevokeACredentialIssuedToThem()
    {
        var credential = await IssuedToRecipient(TokenId);

        var result = await _rail.RevokeAsync(TokenId, callerUserId: _rail.RecipientB);

        result.IsError.Should().BeTrue("the holder is the party a credential is taken FROM");
        result.Code.Should().Be(AzoaErrorCodes.Forbidden);
        _rail.VerifyNoClawback();
        var authority = await _rail.Rail.Store.GetAuthorityAsync(credential.Id.ToString("N"));
        authority!.HolderUserId.Should().Be(credential.HolderUserId.ToString("N"));
        authority.Revoked.Should().BeFalse();
        _rail.Rail.Store.Ownership[credential.Id.ToString("N")].IsActive.Should().BeTrue();
    }
    [Fact]
    public async Task TheIssuer_CanRevokeACredentialItIssued()
    {
        var credential = await IssuedToRecipient(TokenId);
        credential.HolderAddress.Should().NotBeNullOrWhiteSpace(
            "AC3: without current_owner the manager refuses before authorization is ever reached");

        var result = await _rail.RevokeAsync(TokenId, callerUserId: _rail.IssuerA);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Status.Should().Be(OperationStatus.Completed);
        result.Result.IssuerUserId.Should().Be(_rail.IssuerA);
        result.Result.HolderUserId.Should().Be(_rail.RecipientB);
        result.Result.HolderAddress.Should().Be(_rail.RecipientAddress,
            "the clawback takes the asset from the holder's own account");
        result.Result.RecipientAddress.Should().Be(_rail.IssuerAddress,
            "a revoked credential returns to the issuer's wallet");

        var authority = await _rail.Rail.Store.GetAuthorityAsync(credential.Id.ToString("N"));
        authority!.HolderUserId.Should().Be(_rail.IssuerA.ToString("N"));
        authority.Revoked.Should().BeTrue();
        var ownership = _rail.Rail.Store.Ownership[credential.Id.ToString("N")];
        ownership.UserId.Should().Be(_rail.IssuerA.ToString("N"));
        ownership.CurrentOwner.Should().Be(_rail.IssuerAddress);
        ownership.IsActive.Should().BeFalse();
    }
    [Fact]
    public async Task SelfIssuedCredential_IsRefused_SoTheVacuousCaseCannotSilentlyReturn()
    {
        var issued = await _rail.IssueAsync(OtherTokenId, _rail.IssuerA);
        issued.Outcome!.IsError.Should().BeFalse(issued.Outcome.Message);
        var authority = await _rail.Rail.Store.GetAuthorityAsync(issued.Outcome.Result!.Id.ToString("N"));
        authority!.IssuerUserId.Should().BeEmpty();
        authority.HolderUserId.Should().Be(_rail.IssuerA.ToString("N"));

        var result = await _rail.RevokeAsync(OtherTokenId, callerUserId: _rail.IssuerA);

        result.IsError.Should().BeTrue(
            "issuer == holder makes the D19 authorization check vacuous; a credential in that "
            + "shape must be refused, not clawed back on the strength of a check that decided nothing");
        _rail.VerifyNoClawback();
        var unchanged = await _rail.Rail.Store.GetAuthorityAsync(authority.NftId);
        unchanged!.Version.Should().Be(authority.Version);
        unchanged.Revoked.Should().BeFalse();
    }
    [Fact]
    public async Task ReusedIdempotencyKey_OnADifferentCredential_DoesNotReplayTheFirstResult()
    {
        await IssuedToRecipient(TokenId);
        var other = await IssuedToRecipient(OtherTokenId);

        var first = await _rail.RevokeAsync(TokenId, _rail.IssuerA, idempotencyKey: "reused-key");
        first.IsError.Should().BeFalse(first.Message);
        first.Result!.AssetId.Should().Be(TokenId);

        var second = await _rail.RevokeAsync(OtherTokenId, _rail.IssuerA, idempotencyKey: "reused-key");

        second.Result?.AssetId.Should().NotBe(TokenId,
            "AC7: the first credential's cached result must never be served as the second's");
        second.Result?.Replayed.Should().NotBe(true,
            "a different credential is a different operation, not a duplicate of the first");
        second.IsError.Should().BeTrue("one request key cannot authorize a second asset");
        second.Code.Should().Be(AzoaErrorCodes.Conflict);
        var otherAuthority = await _rail.Rail.Store.GetAuthorityAsync(other.Id.ToString("N"));
        otherAuthority!.HolderUserId.Should().Be(_rail.RecipientB.ToString("N"));
        otherAuthority.Revoked.Should().BeFalse();
        _rail.Rail.Store.Ownership[other.Id.ToString("N")].IsActive.Should().BeTrue();
    }
    private async Task<CredentialOwnershipRecord> IssuedToRecipient(string tokenId)
    {
        var issued = await _rail.IssueAsync(tokenId);

        issued.SeamExists.Should().BeTrue(CredentialTrackSeams.MissingSeamMessage);
        issued.Outcome!.IsError.Should().BeFalse(issued.Outcome.Message);
        issued.Record.Should().NotBeNull("the mint must write an ownership row");

        issued.Record!.IssuerUserId.Should().Be(_rail.IssuerA,
            CredentialTrackSeams.MissingIssuerMessage);
        issued.Record.HolderUserId.Should().Be(_rail.RecipientB);
        issued.Record.IssuerUserId.Should().NotBe(issued.Record.HolderUserId,
            "an authorization test over a one-party credential decides nothing");

        return issued.Record;
    }
}

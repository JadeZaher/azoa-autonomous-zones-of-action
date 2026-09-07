// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Credentials;

/// <summary>
/// D19 authorization proven against a credential the PRODUCTION MINT PATH produced, not one
/// stubbed into existence.
///
/// <para><b>Why that distinction is the whole track.</b>
/// <c>CredentialRevokeAuthorizationTests</c> already proves "the holder cannot revoke, the
/// issuer can" — against a hand-written <c>CredentialOwnershipRecord</c> whose issuer and
/// holder differ and whose <c>HolderAddress</c> is set. Nothing in production can produce
/// that record. Every test below therefore states its premise as an assertion
/// (<c>IssuerUserId != HolderUserId</c>, <c>HolderAddress</c> present) so it fails loudly
/// rather than passing over a one-party credential.</para>
/// </summary>
public sealed class IssuedCredentialRevocationTests
{
    private const string TokenId = "778899";
    private const string OtherTokenId = "112233";

    private readonly CredentialRailHarness _rail = new();

    /// <summary>AC2, holder direction. B holds the credential; B may not take it back from themselves.</summary>
    [Fact]
    public async Task TheHolder_CannotRevokeACredentialIssuedToThem()
    {
        var credential = await IssuedToRecipient(TokenId);

        var result = await _rail.RevokeAsync(TokenId, callerUserId: _rail.RecipientB);

        result.IsError.Should().BeTrue("the holder is the party a credential is taken FROM");
        result.Code.Should().Be(AzoaErrorCodes.Forbidden);
        _rail.VerifyNoClawback();
        _rail.Credentials.CurrentHolderOf(TokenId).Should().Be(credential.HolderUserId,
            "a refused revocation moves nothing");
    }

    /// <summary>
    /// AC2, issuer direction, and AC3 with it: this is the direction that proves the revoke
    /// precondition is REACHABLE. Today it dies at "has no recorded holder address" because
    /// no mint writes <c>current_owner</c>, so "the issuer can revoke" has never once been
    /// true of a real credential.
    /// </summary>
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

        // AC5, in-line path: chain truth has moved, so AZOA's read surface must follow.
        _rail.Credentials.CurrentHolderOf(TokenId).Should().Be(_rail.IssuerA,
            "after a confirmed revocation no AZOA read surface may still report the credential as held by B");
    }

    /// <summary>
    /// AC1's teeth. A credential whose issuer IS its holder is the vacuous case: authorizing
    /// "the issuer" then means authorizing the holder to revoke their own credential, which is
    /// the exact inversion D19 exists to forbid. The manager must refuse it outright rather
    /// than perform a revocation that proves nothing.
    ///
    /// <para>This is the control that stops the vacuous case from silently returning. It is
    /// deliberately independent of the mint seam, so it keeps holding even if a later change
    /// re-introduces a self-issuing path.</para>
    /// </summary>
    [Fact]
    public async Task SelfIssuedCredential_IsRefused_SoTheVacuousCaseCannotSilentlyReturn()
    {
        _rail.Credentials.Seed(new CredentialOwnershipRecord
        {
            Id = Guid.NewGuid(),
            HolderUserId = _rail.IssuerA,
            HolderAddress = _rail.RecipientAddress,
            IssuerUserId = _rail.IssuerA,
            IsSoulbound = true,
            ChainType = CredentialRailHarness.Chain,
            TokenId = OtherTokenId,
        });

        var result = await _rail.RevokeAsync(OtherTokenId, callerUserId: _rail.IssuerA);

        result.IsError.Should().BeTrue(
            "issuer == holder makes the D19 authorization check vacuous; a credential in that "
            + "shape must be refused, not clawed back on the strength of a check that decided nothing");
        _rail.VerifyNoClawback();
        _rail.Credentials.OwnershipMoves.Should().Be(0);
    }

    /// <summary>
    /// AC7. A reused <c>Idempotency-Key</c> must never hand back some OTHER credential's
    /// outcome: reporting credential 112233 revoked when 778899 was the one that moved is a
    /// wrong answer, not a duplicate.
    /// </summary>
    [Fact]
    public async Task ReusedIdempotencyKey_OnADifferentCredential_DoesNotReplayTheFirstResult()
    {
        await IssuedToRecipient(TokenId);
        await IssuedToRecipient(OtherTokenId);

        var first = await _rail.RevokeAsync(TokenId, _rail.IssuerA, idempotencyKey: "reused-key");
        first.IsError.Should().BeFalse(first.Message);
        first.Result!.AssetId.Should().Be(TokenId);

        var second = await _rail.RevokeAsync(OtherTokenId, _rail.IssuerA, idempotencyKey: "reused-key");

        second.Result?.AssetId.Should().NotBe(TokenId,
            "AC7: the first credential's cached result must never be served as the second's");
        second.Result?.Replayed.Should().NotBe(true,
            "a different credential is a different operation, not a duplicate of the first");
        second.IsError.Should().BeFalse(second.Message);
        second.Result!.AssetId.Should().Be(OtherTokenId);
    }

    // ── Arrangement ───────────────────────────────────────────────────────────

    /// <summary>
    /// Issues one credential through the production mint seam and asserts the premise every
    /// test above depends on: the recorded issuer and holder are DIFFERENT parties.
    /// </summary>
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

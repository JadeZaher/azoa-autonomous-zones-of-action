// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Tests.Allocation;

/// <summary>
/// The tenant boundary. A non-crypto sponsor must never see an account address, an asset
/// identifier, a rail name, a fee unit, or the words "opt-in" / "token" — so every message
/// this mapper can emit is a fixed constant, and none of them leaks.
/// </summary>
public class ConsumerSafeErrorsTests
{
    /// <summary>Real refusal text from the rail seams this mapper stands in front of.</summary>
    public static TheoryData<string, string> RailFailures() => new()
    {
        {
            "The release recipient RECIPIENTADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA has "
            + "not opted into ASA 77712345. Algorand rejects a transfer to an account that has not "
            + "opted in, and the account must sign its own opt-in.",
            ConsumerSafeErrorCodes.RecipientNotReady
        },
        {
            "Partition 'contributor' would be oversubscribed: 1000 base units requested, 40 available "
            + "(declared 1000, issued 960, held 0, returned 0).",
            ConsumerSafeErrorCodes.InsufficientFunds
        },
        {
            "Cannot transfer ASA 77712345: the bridge vault holding is frozen, so the release would "
            + "be rejected on chain.",
            ConsumerSafeErrorCodes.NotPermitted
        },
        {
            "Cannot prepare a recipient: no transaction signer is configured for Algorand.",
            ConsumerSafeErrorCodes.RailUnavailable
        },
        {
            "PENDING_CONFIRMATION: Algorand transfer (tx ABCDEF) submitted but not yet confirmed; "
            + "reconcile against chain truth.",
            ConsumerSafeErrorCodes.SettlementPending
        },
        {
            "Algod returned 429 Too Many Requests while fetching suggested params.",
            ConsumerSafeErrorCodes.RailBusy
        },
    };

    [Theory]
    [MemberData(nameof(RailFailures))]
    public void Map_ClassifiesRealRailFailures(string railMessage, string expectedCode)
    {
        var mapped = ConsumerSafeErrors.Map(railMessage);

        mapped.Code.Should().Be(expectedCode);
        ConsumerSafeErrors.LeaksRailDetail(mapped.Message).Should().BeFalse(
            "the mapped message is what a sponsor reads");
    }

    [Theory]
    [MemberData(nameof(RailFailures))]
    public void Map_NeverEchoesTheRailText(string railMessage, string expectedCode)
    {
        _ = expectedCode;
        var mapped = ConsumerSafeErrors.Map(railMessage);

        mapped.Message.Should().NotContain("ASA");
        mapped.Message.Should().NotContain("Algorand");
        mapped.Message.Should().NotContain("Algod");
        mapped.Message.Should().NotContain("RECIPIENTADDRESS");
        mapped.Message.Should().NotContain("tx ");
    }

    [Fact]
    public void Map_UnrecognisedFailure_FallsBackToATemporaryUnavailableThatStillSaysNothingMoved()
    {
        var mapped = ConsumerSafeErrors.Map(" something nobody has a pattern for ");

        mapped.Code.Should().Be(ConsumerSafeErrorCodes.TemporarilyUnavailable);
        mapped.Message.Should().Contain("Nothing was moved");
        ConsumerSafeErrors.LeaksRailDetail(mapped.Message).Should().BeFalse();
    }

    [Fact]
    public void Map_NullOrEmpty_IsStillASafeAnswerRatherThanAnException()
    {
        ConsumerSafeErrors.Map(null).Code.Should().Be(ConsumerSafeErrorCodes.TemporarilyUnavailable);
        ConsumerSafeErrors.Map(string.Empty).Code.Should().Be(ConsumerSafeErrorCodes.TemporarilyUnavailable);
    }

    [Theory]
    [InlineData("The recipient has not opted into ASA 77712345.")]
    [InlineData("RECIPIENTADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA is not opted in")]
    [InlineData("Set AZOA:Algorand:PlatformMnemonic to enable signing.")]
    [InlineData("The token balance is 0 on chain.")]
    [InlineData("Confirmed in round 12345 with signature 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void LeaksRailDetail_CatchesTheVocabularyASponsorMustNeverSee(string text)
        => ConsumerSafeErrors.LeaksRailDetail(text).Should().BeTrue();

    [Theory]
    [InlineData("There are not enough available funds to complete this request.")]
    [InlineData("This request could not be completed right now. Nothing was moved; try again shortly.")]
    [InlineData("An Idempotency-Key is required to issue units.")]
    [InlineData("Allocation amount must be a positive integer base-unit amount.")]
    [InlineData(null)]
    [InlineData("")]
    public void LeaksRailDetail_PassesPlainLanguageThrough(string? text)
        => ConsumerSafeErrors.LeaksRailDetail(text).Should().BeFalse();

    [Fact]
    public void Failure_CarriesBothTheSafeMessageAndTheStableCode()
    {
        var failure = ConsumerSafeErrors.Failure<string>(
            "The recipient has not opted into ASA 77712345.");

        failure.IsError.Should().BeTrue();
        failure.Code.Should().Be(ConsumerSafeErrorCodes.RecipientNotReady);
        ConsumerSafeErrors.LeaksRailDetail(failure.Message).Should().BeFalse();
    }

    [Fact]
    public void Failure_WithAPinnedCode_KeepsThatCodeAndStillScrubsTheMessage()
    {
        var failure = ConsumerSafeErrors.Failure<string>(
            "no transaction signer is configured for Algorand",
            ConsumerSafeErrorCodes.RailUnavailable);

        failure.Code.Should().Be(ConsumerSafeErrorCodes.RailUnavailable);
        failure.Message.Should().NotContain("Algorand");
    }
}

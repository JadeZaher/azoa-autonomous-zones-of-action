// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Tests.Allocation;

/// <summary>
/// OI-30. One un-scrubbed error is enough: the sentence a member actually reads is whichever
/// refusal fires first, so it is not sufficient for the allocation seam to scrub the exits
/// somebody remembered. These tests do two things a behaviour test alone cannot.
/// <para>
/// They COUNT the exits by scanning the source, and pin the number. A refusal added later
/// either leaves through the scrubber - bumping the count and failing the pin until it is
/// deliberately updated - or builds its own failure result and fails the structural test.
/// Neither can leak quietly.
/// </para>
/// <para>
/// They then prove the sentence itself at the seam the ledger named: node governance, whose
/// refusal reads "Node governance disallows allocation:IssueUnits on chain 'Algorand'.
/// Allowed chains: ..." - three terms the programme's own rail-vocabulary guard flags.
/// </para>
/// </summary>
public class ConsumerSafeExitCoverageTests
{
    private const string AllocationManagerPath = "Managers/AllocationManager.cs";

    /// <summary>
    /// How many failure returns <c>AllocationManager</c> has. Bump this ONLY together with a
    /// new <c>ToConsumerSafeFailure</c> call - never to make a red test green.
    /// </summary>
    private const int ScrubbedExitCount = 20;

    /// <summary>The one failure-result factory, plus its two calls inside the scrubber.</summary>
    private const int RefusedFactoryMentions = 3;

    // -- The count: every exit, not the ones somebody remembered ---------------

    [Fact]
    public void EveryFailureExitLeavesThroughTheScrubberAndTheCountIsPinned()
    {
        var lines = ReadAllocationManagerLines();
        var scrubberDeclaration = IndexOfScrubberDeclaration(lines);

        var exits = lines
            .Take(scrubberDeclaration)
            .Count(line => line.Contains("ToConsumerSafeFailure(", StringComparison.Ordinal));

        exits.Should().Be(
            ScrubbedExitCount,
            "every failure return in AllocationManager must leave through ToConsumerSafeFailure, "
            + "so the count is pinned - a new refusal that raises it fails here until somebody "
            + "confirms it is scrubbed, and one that lowers it means an exit stopped being scrubbed");
    }

    [Fact]
    public void NoAllocationFailureIsBuiltOutsideTheScrubber()
    {
        var source = ReadAllocationManagerSource();

        source.Should().NotContain(
            "AZOAResult<AllocationResult>.Failure(",
            "a failure built by the public factory bypasses the scrubber entirely");
        source.Should().NotContain(
            "AZOAResult<AllocationResult>.FailureWithCode(",
            "a failure built by the public factory bypasses the scrubber entirely");
        source.Should().NotContain(
            "new AZOAResult<AllocationResult> { IsError",
            "a failure built inline bypasses the scrubber entirely");
    }

    [Fact]
    public void TheOnlyFailureFactoryIsReachedOnlyFromTheScrubber()
    {
        var lines = ReadAllocationManagerLines();
        var scrubberDeclaration = IndexOfScrubberDeclaration(lines);

        var mentionedAt = lines
            .Select((line, index) => new { Line = line, Index = index })
            .Where(entry => entry.Line.Contains("Refused(", StringComparison.Ordinal))
            .Select(entry => entry.Index)
            .ToArray();

        mentionedAt.Should().HaveCount(
            RefusedFactoryMentions,
            "Refused is declared once and called twice, both times inside ToConsumerSafeFailure");
        mentionedAt.Should().OnlyContain(
            index => index >= scrubberDeclaration,
            "a call to the failure factory ahead of the scrubber would be an exit that skipped it");
    }

    // -- The sentence: the governance refusal OI-30 named ----------------------

    [Fact]
    public async Task NodeGovernanceRefusal_ReachesTheMemberWithNoRailDetail()
    {
        var harness = new AllocationTestHarness();
        var manager = BuildWithGovernance(harness, RestrictedToSolana());

        var result = await manager.AllocateAsync(
            harness.UserId,
            AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId,
            "governance-refusal",
            AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        ConsumerSafeErrors.LeaksRailDetail(result.Message).Should().BeFalse(
            "the guard's own wording names the action, the chain and the allowlist");
        result.Message.Should().NotContain("Algorand").And.NotContain("Solana");
        result.Message.Should().NotContain("chain");
        result.Code.Should().Be(
            ConsumerSafeErrorCodes.NotPermitted,
            "a governance refusal is policy - it is permitted-or-not, never 'try again shortly'");

        // Scrubbing the sentence must not have moved the refusal later in the sequence: it
        // still lands before the KYC gate, before any broadcast, and before the claim.
        harness.ValueAccess.Verify(
            access => access.RequireValueAccessAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.BlockchainOps.Verify(
            ops => ops.ExecuteAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<AZOARequest?>()),
            Times.Never);
        var claimed = await harness.Idempotency.GetAsync(
            $"alloc:{AllocationTestHarness.ApiKeyGuid}:governance-refusal", CancellationToken.None);
        claimed.Should().BeNull("a governance refusal precedes the idempotency claim");
    }

    [Fact]
    public async Task NodeGovernanceGuard_KeepsItsDetailForTheOperatorLog()
    {
        var denial = await RestrictedToSolana().EnsureAllowedAsync(
            AllocationTestHarness.ChainType, "Riverbank cleanup", "allocation:IssueUnits");

        denial.IsError.Should().BeTrue();
        denial.Message.Should().Contain("Algorand", "an operator reading the log needs the detail");
        ConsumerSafeErrors.LeaksRailDetail(denial.Message).Should().BeTrue(
            "this is exactly the sentence that must never cross the API boundary unscrubbed");
        denial.Code.Should().Be(ConsumerSafeErrorCodes.NotPermitted);
    }

    [Fact]
    public async Task TheGuardsCodeAndTheScrubbedProseSayTheSameThing()
    {
        var denial = await RestrictedToSolana().EnsureAllowedAsync(
            AllocationTestHarness.ChainType, "Riverbank cleanup", "allocation:IssueUnits");

        var redacted = ConsumerSafeErrors.Redact(denial.Message);

        redacted.HasValue.Should().BeTrue("the guard's wording carries rail detail");
        var scrubbed = redacted!.Value;

        scrubbed.Code.Should().Be(
            denial.Code,
            "a code that says NOT_PERMITTED beside prose that says 'try again shortly' is a "
            + "contradiction the member has to resolve");
        ConsumerSafeErrors.LeaksRailDetail(scrubbed.Message).Should().BeFalse();
    }

    // -- Rail text injected at exits that used to pass it straight through -----

    [Fact]
    public async Task AWalletStoreFailure_DoesNotCarryTheProvidersWordsOut()
    {
        var harness = new AllocationTestHarness();
        harness.WalletStore
            .Setup(store => store.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IWallet>>.Failure(
                "Algod could not read the Algorand account "
                + AllocationTestHarness.RecipientAddress + "; the wallet lookup failed."));
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId,
            AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId,
            "wallet-lookup-leak",
            AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        ConsumerSafeErrors.LeaksRailDetail(result.Message).Should().BeFalse();
        result.Message.Should().NotContain(AllocationTestHarness.RecipientAddress);
    }

    [Fact]
    public async Task AFeeQuoteFailure_DoesNotCarryTheProvidersWordsOut()
    {
        var harness = new AllocationTestHarness();
        harness.NodeFees
            .Setup(fees => fees.QuoteAsync(
                It.IsAny<NodeFeeOperation>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<NodeFeeQuoteResponse>.Failure(
                "The Algorand fee schedule could not be priced: the token has no configured unit."));
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId,
            AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId,
            "fee-quote-leak",
            AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        ConsumerSafeErrors.LeaksRailDetail(result.Message).Should().BeFalse();
        result.Message.Should().NotContain("Algorand").And.NotContain("token");
    }

    [Fact]
    public async Task TheAmountRefusal_DoesNotEchoWhateverTheCallerTyped()
    {
        var harness = new AllocationTestHarness();
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId,
            AllocationTestHarness.IssueUnitsRequest(amount: "algorand"),
            harness.CallerUserId,
            "amount-echo-leak",
            AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        ConsumerSafeErrors.LeaksRailDetail(result.Message).Should().BeFalse(
            "the parse refusal quotes the caller's own string back at it");
    }

    // -- The scrub must not eat the prefix the HTTP status is read off ---------

    [Fact]
    public async Task ARoutingPrefixSurvivesTheScrubSoTheStatusStays403()
    {
        var harness = new AllocationTestHarness();
        harness.ValueAccess
            .Setup(access => access.RequireValueAccessAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Failure(
                KycAuthorizationError.Forbidden
                + "Verification is required before units may be issued on Algorand."));
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId,
            AllocationTestHarness.IssueUnitsRequest(),
            harness.CallerUserId,
            "kyc-prefix",
            AllocationTestHarness.ApiKeyId,
            drawAuthority: ReserveDrawAuthority.NodeOperator);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(
            KycAuthorizationError.Forbidden,
            "AllocationController reads the 403 off this prefix; replacing the message wholesale "
            + "would silently downgrade the refusal to a 400");
        ConsumerSafeErrors.LeaksRailDetail(result.Message).Should().BeFalse();
    }

    // -- Wiring ---------------------------------------------------------------

    private static NodeGovernanceGuard RestrictedToSolana()
        => new(Options.Create(new NodeGovernanceOptions { AllowedChains = new[] { "Solana" } }));

    /// <summary>The harness's own wiring, with a real governance guard in place of its null.</summary>
    private static AllocationManager BuildWithGovernance(
        AllocationTestHarness harness, INodeGovernanceGuard guard)
        => new(
            harness.ValueAccess.Object,
            harness.WalletManager.Object,
            harness.WalletStore.Object,
            harness.HolonStore.Object,
            harness.Nft.Object,
            harness.BlockchainOps.Object,
            harness.Idempotency,
            harness.NodeFees.Object,
            nodeGovernance: guard,
            users: null,
            reserve: harness.Reserve.Object,
            recipientPreparer: harness.Preparer.Object,
            feeSettlement: harness.FeeSettlement.Object,
            treasury: harness.Treasury.Object,
            providers: harness.Providers.Object,
            logger: null);

    private static int IndexOfScrubberDeclaration(string[] lines)
    {
        var index = Array.FindIndex(
            lines,
            line => line.Contains(
                "private AZOAResult<AllocationResult> ToConsumerSafeFailure(string? message",
                StringComparison.Ordinal));

        index.Should().BeGreaterThan(
            0, "the scrubber must exist and be declared after the exits that call it");
        return index;
    }

    private static string[] ReadAllocationManagerLines()
        => File.ReadAllLines(AllocationManagerFile());

    private static string ReadAllocationManagerSource()
        => File.ReadAllText(AllocationManagerFile());

    private static string AllocationManagerFile()
        => Path.Combine(
            ResolveRepoRoot(),
            AllocationManagerPath.Replace('/', Path.DirectorySeparatorChar));

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AZOA.WebAPI.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the AZOA repository root.");
    }
}

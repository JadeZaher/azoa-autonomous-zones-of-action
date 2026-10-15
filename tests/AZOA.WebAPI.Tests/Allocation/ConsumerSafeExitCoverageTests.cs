// SPDX-License-Identifier: UNLICENSED

using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
/// The structural half used to rest on three LITERAL substring scans and a line count, which a
/// refusal written with its initializer members in the other order, or assembled over two
/// statements, satisfied while reaching the wire unscrubbed - and two scrubber calls on one
/// line counted as one. The scans are now regexes over offsets: the pinned count counts CALLS,
/// and the load-bearing rule is that an error flag is only ever WRITTEN at or after the
/// scrubber, which no ordering or splitting evades.
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
        var source = ReadAllocationManagerSource();
        var scrubberAt = OffsetOfScrubberDeclaration(source);

        // CALLS, not lines. Two calls written on one line used to count as one, so a refusal
        // could be added beside an existing one without moving the pin.
        var exits = CountBefore(source, ScrubberCall, scrubberAt);

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
        var scrubberAt = OffsetOfScrubberDeclaration(source);

        // The public failure factories, whitespace-tolerant: a generic name may legally carry
        // spaces inside its angle brackets and around the dot, which a literal scan misses.
        FailureFactoryCall.Matches(source).Should().BeEmpty(
            "a failure built by a public factory bypasses the scrubber entirely");

        // The real rule, and the one a reordered or split construction cannot slip past: an
        // error flag is only ever WRITTEN at or after the scrubber. This covers a member-
        // reordered object initializer, a target-typed new(), and a refusal assembled over two
        // statements - none of which the three literal substring scans this replaces saw.
        var writesAhead = ErrorFlagAssignment.Matches(source)
            .Where(match => match.Index < scrubberAt)
            .Select(match => LineOf(source, match.Index))
            .ToArray();

        writesAhead.Should().BeEmpty(
            "a failure whose IsError is set ahead of the scrubber is an exit that skipped it");
    }

    [Fact]
    public void TheOnlyFailureFactoryIsReachedOnlyFromTheScrubber()
    {
        var source = ReadAllocationManagerSource();
        var scrubberAt = OffsetOfScrubberDeclaration(source);

        var mentions = RefusedFactoryCall.Matches(source).ToArray();

        mentions.Should().HaveCount(
            RefusedFactoryMentions,
            "Refused is declared once and called twice, both times inside ToConsumerSafeFailure");
        mentions.Should().OnlyContain(
            match => match.Index >= scrubberAt,
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
    public async Task NodeGovernanceGuard_ScrubsAtTheCounterAndLogsTheDetail()
    {
        var log = new RecordingLogger();
        var denial = await RestrictedToSolana(log).EnsureAllowedAsync(
            AllocationTestHarness.ChainType, "Riverbank cleanup", "allocation:IssueUnits");

        denial.IsRefused.Should().BeTrue();
        ConsumerSafeErrors.LeaksRailDetail(denial.ConsumerMessage).Should().BeFalse(
            "the guard's own wording names the action, the chain and the allowlist, and the "
            + "decision is the only thing a caller can reach");
        denial.ConsumerMessage.Should().NotContain("Algorand").And.NotContain("Solana");
        denial.Code.Should().Be(ConsumerSafeErrorCodes.NotPermitted);

        // The detail is not lost - it goes where an operator reads it, and nowhere else.
        log.Entries.Should().ContainSingle().Which.Should().Contain("Algorand");
    }

    [Fact]
    public void NodeGovernanceDecision_HasNoMemberThatCouldCarryRailDetail()
    {
        // The structural half of the counter-side fix: a door cannot leak what the type it is
        // handed does not hold. If a future edit adds an OperatorDetail/RawMessage member back,
        // this fails before any door can start returning it.
        // INSTANCE members only: what one decision VALUE can hold is what a door can read out
        // of it. The static factories are the constructors, not carriers.
        var readable = typeof(NodeGovernanceDecision)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        readable.Should().BeEquivalentTo(
            new[] { "Code", "ConsumerMessage", "IsAllowed", "IsRefused" },
            "every door in the codebase reads this type, so what it can hold is what can leak");
    }

    [Theory]
    [InlineData("Algorand", "an allowlisted rail name")]
    [InlineData("Solana", "an allowlisted rail name")]
    [InlineData("chain", "the dimension the guard refuses on")]
    public async Task EveryGuardEntryPoint_RefusesWithoutNamingTheRail(string forbidden, string why)
    {
        var guard = RestrictedToSolana();

        var decisions = new[]
        {
            await guard.EnsureAllowedAsync(
                AllocationTestHarness.ChainType, "Riverbank cleanup", "allocation:IssueUnits"),
            await guard.EnsureChainAllowedAsync(AllocationTestHarness.ChainType, "nft:mint"),
        };

        foreach (var decision in decisions)
        {
            decision.IsRefused.Should().BeTrue();
            decision.ConsumerMessage.Should().NotContainEquivalentOf(forbidden, because: why);
            ConsumerSafeErrors.LeaksRailDetail(decision.ConsumerMessage).Should().BeFalse();
        }
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

    private static NodeGovernanceGuard RestrictedToSolana(
        ILogger<NodeGovernanceGuard>? logger = null)
        => new(
            Options.Create(new NodeGovernanceOptions { AllowedChains = new[] { "Solana" } }),
            store: null,
            logger: logger);

    /// <summary>Captures the operator wording the guard writes instead of returning.</summary>
    private sealed class RecordingLogger : ILogger<NodeGovernanceGuard>
    {
        public List<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }

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

    /// <summary>A call to the scrubber, however the call is spaced.</summary>
    private static readonly Regex ScrubberCall = new(
        @"\bToConsumerSafeFailure\s*\(", RegexOptions.Compiled);

    /// <summary>A call to the one failure factory, or its declaration.</summary>
    private static readonly Regex RefusedFactoryCall = new(
        @"\bRefused\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// A public failure factory on the allocation result, tolerant of the whitespace C#
    /// allows inside a generic name and around a member access.
    /// </summary>
    private static readonly Regex FailureFactoryCall = new(
        @"AZOAResult\s*<\s*AllocationResult\s*>\s*\.\s*Failure(WithCode)?\s*\(",
        RegexOptions.Compiled);

    /// <summary>Any WRITE of an error flag, never a read (which is followed by a second =).</summary>
    private static readonly Regex ErrorFlagAssignment = new(
        @"\bIsError\s*=(?!=)", RegexOptions.Compiled);

    private static int CountBefore(string source, Regex pattern, int offset)
        => pattern.Matches(source).Count(match => match.Index < offset);

    private static int LineOf(string source, int offset)
        => source.Take(offset).Count(character => character == '\n') + 1;

    private static int OffsetOfScrubberDeclaration(string source)
    {
        var offset = source.IndexOf(
            "private AZOAResult<AllocationResult> ToConsumerSafeFailure(string? message",
            StringComparison.Ordinal);

        offset.Should().BeGreaterThan(
            0, "the scrubber must exist and be declared after the exits that call it");
        return offset;
    }

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

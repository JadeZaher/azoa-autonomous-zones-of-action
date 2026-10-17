using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Nethereum.Model;
using Nethereum.Util;

namespace AZOA.WebAPI.Tests.Managers.Dex;

/// <summary>Ethereum DEX submission, finalized settlement, nonce-winner recovery and allowance cleanup through the provider module.</summary>
public sealed partial class EthereumDexQuoteTests
{
    private const string Executed = "SwapExecuted(bytes32,bytes32,address,address,address,address,address,uint256,uint256,uint256,address,uint256)";

    [Theory]
    [InlineData("exact")]
    [InlineData("extra-transfer")]
    [InlineData("below-minimum")]
    public async Task Finalized_swap_settles_only_with_the_complete_executor_pool_and_token_grammar(string mutation)
    {
        using var fixture = new Fixture { PreparationEnabled = true, CurrentAllowance = 1010 };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        var swap = await PlanAndSignAsync(fixture, intent, "Swap");
        var chain = new ScriptedChain(fixture) { Head = 20 };
        var output = mutation == "below-minimum" ? 1980 : 1995;
        var logs = SwapLogs(fixture, intent, swap.Id, chain.BlockHash(15), output).ToList();
        if (mutation == "extra-transfer") logs.Add(Log(Address(6), swap.Id, chain.BlockHash(15), Topic("Transfer(address,address,uint256)"),
            [Word(Address(1)), Word(Address(30))], Uint(1)));
        chain.Include(swap.Wire, fixture.OwnerKey.GetPublicAddress(), 15, true, logs);
        fixture.CurrentAllowance = 0;

        var observed = await fixture.Provider.ObserveSwapStageAsync(intent, swap.Prepared);

        observed.IsError.Should().BeFalse(observed.Message);
        if (mutation != "exact")
        {
            observed.Result!.Verdict.Should().Be("NeedsOperator");
            observed.Result.ActualOutput.Should().BeNull("an unproven receipt grammar never reports economic output");
            return;
        }
        var settlement = observed.Result!;
        settlement.Verdict.Should().Be("Confirmed");
        (settlement.ActualInput, settlement.ActualOutput, settlement.PlatformFeePaid, settlement.NetworkFeePaid)
            .Should().Be(("1010", "1995", "10", "21000000000000"));
        SwapObservationContract.IsValid(intent, swap.Prepared, settlement).Should().BeTrue();
        using var evidence = JsonDocument.Parse(settlement.EvidenceJson!);
        evidence.RootElement.GetProperty("AllowanceRelease").GetProperty("TransactionId").GetString().Should().Be(swap.Id);
        (await fixture.Provider.ReleaseRetainedResourcesAsync(intent, "swap-stage", swap.Prepared, settlement)).Should().BeTrue();
        fixture.AllowanceStore.Released.Should().ContainSingle(release => release.Stage == "swap-stage" && release.Operation == intent.OperationId);
        fixture.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Mined_revert_is_failed_on_chain_with_its_actual_fee_and_keeps_the_allowance_resource()
    {
        using var fixture = new Fixture { PreparationEnabled = true, CurrentAllowance = 1010 };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        var swap = await PlanAndSignAsync(fixture, intent, "Swap");
        var chain = new ScriptedChain(fixture) { Head = 20 };
        chain.Include(swap.Wire, fixture.OwnerKey.GetPublicAddress(), 15, false, []);

        var observed = (await fixture.Provider.ObserveSwapStageAsync(intent, swap.Prepared)).Result!;

        observed.Verdict.Should().Be("FailedOnChain");
        (observed.ActualInput, observed.ActualOutput, observed.PlatformFeePaid, observed.NetworkFeePaid).Should().Be(("0", "0", "0", "21000000000000"));
        (await fixture.Provider.ReleaseRetainedResourcesAsync(intent, "swap-stage", swap.Prepared, observed)).Should().BeFalse();
        fixture.AllowanceStore.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_signature_over_identical_bytes_settles_as_the_finalized_winner_while_the_original_wire_is_retained()
    {
        using var fixture = new Fixture { PreparationEnabled = true, CurrentAllowance = 1010 };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        var original = await PlanAndSignAsync(fixture, intent, "Swap");
        var alternate = Signature(fixture, intent, original.Prepared.Plan, alternate: true);
        alternate.Id.Should().NotBe(original.Id);
        var chain = new ScriptedChain(fixture) { Head = 20, Nonce = PlanNonce(original.Prepared.Plan), ConsumedAt = 15 };
        chain.Include(alternate.Wire, fixture.OwnerKey.GetPublicAddress(), 15, true, SwapLogs(fixture, intent, alternate.Id, chain.BlockHash(15), 1995));
        fixture.CurrentAllowance = 0;

        var observed = (await fixture.Provider.ObserveSwapStageAsync(intent, original.Prepared)).Result!;

        observed.Verdict.Should().Be("Confirmed");
        observed.TransactionIds.Should().Equal(alternate.Id);
        observed.FinalizedCandidate!.OriginalPreparedHash.Should().Be(CanonicalJson.Hash(original.Prepared));
        observed.FinalizedCandidate.Candidate.TransactionIds.Should().Equal(alternate.Id);
        SwapObservationContract.IsValid(intent, original.Prepared, observed).Should().BeTrue();
        original.Prepared.TransactionIds.Should().Equal(new[] { original.Id }, "the immutable original candidate is never rewritten");
    }

    [Fact]
    public async Task A_different_finalized_nonce_winner_proves_no_effect_for_exactly_its_own_plan()
    {
        using var fixture = new Fixture { PreparationEnabled = true, CurrentAllowance = 1010 };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        var plan = (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0)).Result!;
        var nonce = PlanNonce(plan);
        var chain = new ScriptedChain(fixture) { Head = 20, Nonce = nonce, ConsumedAt = 15 };
        var cancellation = SelfCancellation(fixture, nonce);
        chain.Include(cancellation, fixture.OwnerKey.GetPublicAddress(), 15, true, []);

        fixture.Provider.GetExposedSwapTransactionIds(intent, plan).Result.Should().BeEmpty("type-2 identities depend on the actual signature");
        var exposed = (await fixture.Provider.ObserveExposedSwapStageAsync(intent, plan)).Result!;

        exposed.RecoveredCandidate.Should().BeNull();
        exposed.Observation.Verdict.Should().Be("ExpiredAbsent");
        exposed.Observation.NetworkFeePaid.Should().Be("0", "the original plan spent nothing; the winner's cost is not this stage's");
        fixture.Provider.ValidateExposedSwapNoEffect(intent, plan, exposed.Observation).Should().BeTrue();
        var otherPlan = plan with { Generation = 1, PreparationKey = SwapOperationIdentity.Preparation(intent.OperationId, "Swap", 1, plan.IntentHash) };
        fixture.Provider.ValidateExposedSwapNoEffect(intent, otherPlan, exposed.Observation).Should().BeFalse();
        fixture.Provider.ValidateExposedSwapNoEffect(intent, plan, exposed.Observation with { NetworkFeePaid = "1" }).Should().BeFalse();
    }

    [Fact]
    public async Task A_same_call_winner_in_a_wallet_chosen_envelope_needs_an_operator_and_is_never_a_no_effect_proof()
    {
        using var fixture = new Fixture { PreparationEnabled = true, CurrentAllowance = 1010 };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        var original = await PlanAndSignAsync(fixture, intent, "Swap");
        var plan = original.Prepared.Plan;
        var repriced = SameCallRepriced(fixture, plan);
        var winnerId = EthereumHex.Data(new Sha3Keccack().CalculateHash(repriced));
        var chain = new ScriptedChain(fixture) { Head = 20, Nonce = PlanNonce(plan), ConsumedAt = 15 };
        chain.Include(repriced, fixture.OwnerKey.GetPublicAddress(), 15, true, SwapLogs(fixture, intent, winnerId, chain.BlockHash(15), 1995));
        fixture.CurrentAllowance = 0;

        var exposed = (await fixture.Provider.ObserveExposedSwapStageAsync(intent, plan)).Result!;
        var custody = (await fixture.Provider.ObserveSwapStageAsync(intent, original.Prepared)).Result!;

        exposed.RecoveredCandidate.Should().BeNull("a different envelope is not a signature over the consented exact wire");
        foreach (var observed in new[] { exposed.Observation, custody })
        {
            observed.Verdict.Should().Be("NeedsOperator", "the planned call itself may have executed under the reserved nonce");
            using var evidence = JsonDocument.Parse(observed.EvidenceJson!);
            evidence.RootElement.GetProperty("Format").GetString().Should().Be("ethereum-nonce-winner-same-call/1");
            evidence.RootElement.GetProperty("WinnerTransactionId").GetString().Should().Be(winnerId);
            evidence.RootElement.GetProperty("EffectProven").GetBoolean().Should().BeTrue();
            evidence.RootElement.GetProperty("ActualOutput").GetString().Should().Be("1995");
            fixture.Provider.ValidateExposedSwapNoEffect(intent, plan, observed with { Verdict = "ExpiredAbsent" }).Should().BeFalse();
        }
    }

    [Fact]
    public async Task An_operator_settles_a_same_call_winner_only_from_re_proven_finality_and_the_lineage_then_completes()
    {
        using var fixture = new Fixture { PreparationEnabled = true, CurrentAllowance = 1010 };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        var plan = (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0)).Result!;
        var repriced = SameCallRepriced(fixture, plan);
        var winnerId = EthereumHex.Data(new Sha3Keccack().CalculateHash(repriced));
        var chain = new ScriptedChain(fixture) { Head = 20, Nonce = PlanNonce(plan), ConsumedAt = 15 };
        chain.Include(repriced, fixture.OwnerKey.GetPublicAddress(), 15, true, SwapLogs(fixture, intent, winnerId, chain.BlockHash(15), 1995));
        fixture.CurrentAllowance = 0;
        var review = (await fixture.Provider.ObserveExposedSwapStageAsync(intent, plan)).Result!.Observation;
        var audit = new SwapOperatorSettlement(1, Guid.NewGuid(), "Wallet re-priced the disclosed swap", DateTimeOffset.UtcNow,
            CanonicalJson.HashCanonical(CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(review))));

        var proved = await fixture.Provider.ProveOperatorSettlementAsync(intent, plan, review, audit);

        proved.IsError.Should().BeFalse(proved.Message);
        var settled = proved.Result!;
        settled.Verdict.Should().Be("Confirmed");
        settled.TransactionIds.Should().Equal(winnerId);
        (settled.ActualInput, settled.ActualOutput, settled.LockedBalance).Should().Be((intent.Terms.GrossInput, "1995", "0"));
        using (var evidence = JsonDocument.Parse(settled.EvidenceJson!))
        {
            evidence.RootElement.GetProperty("AllowanceRelease").GetProperty("TransactionId").GetString().Should().Be(winnerId,
                "zero allowance after the winner is read from chain state, so the lane can release through the existing evidence path");
            evidence.RootElement.GetProperty("OperatorSettlement").GetProperty("ReplacedObservationHash").GetString()
                .Should().Be(audit.ReplacedObservationHash);
        }
        fixture.Provider.ValidateOperatorSettlement(intent, plan, settled).Should().BeTrue();
        fixture.Provider.ValidateOperatorSettlement(intent, plan, settled with { ActualOutput = "1996" }).Should().BeFalse();
        var next = await fixture.Provider.GetNextSwapStageAsync(intent, [new SwapStageHistory(null, settled, ExposedPlan: plan)]);
        next.IsError.Should().BeFalse(next.Message);
        next.Result!.Complete.Should().BeTrue("the operator-settled winner is the exact consented settlement");
        var differentCall = review with
        {
            EvidenceJson = review.EvidenceJson!.Replace("ethereum-nonce-winner-same-call/1", "ethereum-finalized-nonce-winner/1"),
        };
        (await fixture.Provider.ProveOperatorSettlementAsync(intent, plan, differentCall, audit)).IsError.Should().BeTrue(
            "only a recorded same-call review can be settled");
    }

    [Fact]
    public async Task Pruned_nonce_history_stays_pending_and_resumes_only_from_its_own_persisted_checkpoint()
    {
        using var fixture = new Fixture { PreparationEnabled = true, CurrentAllowance = 1010 };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        var swap = await PlanAndSignAsync(fixture, intent, "Swap");
        var alternate = Signature(fixture, intent, swap.Prepared.Plan, alternate: true);
        var chain = new ScriptedChain(fixture) { Head = 20, Nonce = PlanNonce(swap.Prepared.Plan), ConsumedAt = 15, PrunedBelow = 14 };
        chain.Include(alternate.Wire, fixture.OwnerKey.GetPublicAddress(), 15, true, SwapLogs(fixture, intent, alternate.Id, chain.BlockHash(15), 1995));
        fixture.CurrentAllowance = 0;

        var first = (await fixture.Provider.ObserveSwapStageAsync(intent, swap.Prepared)).Result!;
        first.Verdict.Should().Be("Pending");
        first.EvidenceJson.Should().Contain("ethereum-dex-nonce-search/1");
        first.ActualOutput.Should().BeNull();

        chain.PrunedBelow = 0;
        var foreign = first with { EvidenceJson = first.EvidenceJson!.Replace(first.EvidenceJson[first.EvidenceJson.IndexOf("\"Binding\":\"", StringComparison.Ordinal)..][11..75], new string('0', 64)) };
        (await fixture.Provider.ObserveSwapStageAsync(intent, swap.Prepared, previous: foreign)).Result!.Verdict.Should().Be("Confirmed",
            "a checkpoint bound to another plan is ignored and the search restarts from the original quote block");
        var resumed = (await fixture.Provider.ObserveSwapStageAsync(intent, swap.Prepared, previous: first)).Result!;
        resumed.Verdict.Should().Be("Confirmed");
        resumed.TransactionIds.Should().Equal(alternate.Id);
    }

    [Fact]
    public async Task Exposed_plan_adopts_the_actual_browser_signature_found_at_finality()
    {
        using var fixture = new Fixture { PreparationEnabled = true, CurrentAllowance = 1010 };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        var plan = (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0)).Result!;
        var browser = Signature(fixture, intent, plan, alternate: true);
        var chain = new ScriptedChain(fixture) { Head = 20, Nonce = PlanNonce(plan), ConsumedAt = 15 };
        chain.Include(browser.Wire, fixture.OwnerKey.GetPublicAddress(), 15, true, SwapLogs(fixture, intent, browser.Id, chain.BlockHash(15), 1995));
        fixture.CurrentAllowance = 0;

        var exposed = (await fixture.Provider.ObserveExposedSwapStageAsync(intent, plan)).Result!;

        exposed.RecoveredCandidate!.SignedPayloadJson.Should().Be(browser.Prepared.SignedPayloadJson);
        exposed.Observation.Verdict.Should().Be("Confirmed");
        exposed.Observation.TransactionIds.Should().Equal(browser.Id);
        SwapObservationContract.IsValid(intent, exposed.RecoveredCandidate, exposed.Observation).Should().BeTrue();
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("already-known")]
    [InlineData("canceling")]
    [InlineData("nonce-consumed")]
    [InlineData("deadline-elapsed")]
    public async Task Submission_sends_only_the_exact_saved_wire_while_the_nonce_and_deadline_are_live(string condition)
    {
        using var fixture = new Fixture { PreparationEnabled = true, CurrentAllowance = 1010 };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        var swap = await PlanAndSignAsync(fixture, intent, "Swap");
        var chain = new ScriptedChain(fixture) { Head = 20, Nonce = PlanNonce(swap.Prepared.Plan),
            ConsumedAt = condition == "nonce-consumed" ? new BigInteger(18) : null, KnownTransaction = condition == "already-known" };
        if (condition == "canceling")
        {
            var row = fixture.Preparations.Snapshot.Single();
            row.State = "Canceling";
            await fixture.Preparations.UpdateAsync(row, row.Version);
        }
        if (condition == "deadline-elapsed") fixture.HeadTime += 4000;

        var first = await fixture.Provider.SubmitSwapStageAsync(intent, swap.Prepared);
        var replay = await fixture.Provider.SubmitSwapStageAsync(intent, swap.Prepared);

        first.IsError.Should().BeFalse(first.Message);
        replay.Result.Should().Equal(swap.Id);
        var expected = condition is "fresh" or "already-known" ? 2 : 0;
        fixture.Sent.Should().HaveCount(expected);
        fixture.Sent.Where(hex => hex != EthereumHex.Data(swap.Wire)).Should().BeEmpty("only the exact saved wire is ever sent");
    }

    [Fact]
    public async Task Stage_lineage_follows_consented_allowance_prerequisites_and_discloses_retained_allowance()
    {
        using var fixture = new Fixture { PreparationEnabled = true };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        (await fixture.Provider.GetNextSwapStageAsync(intent, [])).Result!.Stage.Should().Be("ResetAllowance");
        fixture.CurrentAllowance = 1010;
        (await fixture.Provider.GetNextSwapStageAsync(intent, [])).Result!.Stage.Should().Be("Swap", "a satisfied live allowance needs no prerequisite");
        fixture.CurrentAllowance = 5;
        var reset = await PlanAndSignAsync(fixture, intent, "ResetAllowance");
        fixture.CurrentAllowance = 0;
        var approve = await PlanAndSignAsync(fixture, intent, "ApproveExact");
        fixture.CurrentAllowance = 1010;
        var swap = await PlanAndSignAsync(fixture, intent, "Swap");
        var resetDone = new SwapStageHistory(reset.Prepared, Allowance(reset));
        var approveDone = new SwapStageHistory(approve.Prepared, Allowance(approve));

        (await fixture.Provider.GetNextSwapStageAsync(intent, [resetDone])).Result!.Stage.Should().Be("ApproveExact");
        var afterApproval = (await fixture.Provider.GetNextSwapStageAsync(intent, [resetDone, approveDone])).Result!;
        afterApproval.Stage.Should().Be("Swap");
        afterApproval.RetainedEffects.Should().ContainSingle(effect => effect.Kind == "ApproveExact" && effect.Amount == "1010");
        (await fixture.Provider.GetNextSwapStageAsync(intent, [resetDone, new(swap.Prepared, Settled(swap, "0"))])).IsError
            .Should().BeTrue("the exact approval cannot be skipped once the reset started the lineage");
        var complete = (await fixture.Provider.GetNextSwapStageAsync(intent, [resetDone, approveDone, new(swap.Prepared, Settled(swap, "0"))])).Result!;
        complete.Complete.Should().BeTrue();
        complete.RetainedEffects.Should().BeEmpty();
        var retained = (await fixture.Provider.GetNextSwapStageAsync(intent, [resetDone, approveDone, new(swap.Prepared, Settled(swap, "7"))])).Result!;
        retained.RetainedEffects.Should().ContainSingle(effect => effect.Kind == "RetainedAllowance" && effect.Amount == "7");

        using var unconsented = new Fixture { PreparationEnabled = true, CurrentAllowance = 1010 };
        var direct = Intent((await unconsented.Provider.QuoteSwapAsync(unconsented.Terms)).Result!);
        direct.Terms.Prerequisites.Should().BeEmpty();
        unconsented.CurrentAllowance = 0;
        (await unconsented.Provider.GetNextSwapStageAsync(direct, [])).IsError.Should().BeTrue("an approval the owner never consented to is refused");
    }

    [Fact]
    public async Task Allowance_cleanup_is_a_separately_consented_zero_approval_at_a_new_nonce_and_releases_on_proof()
    {
        using var fixture = new Fixture { PreparationEnabled = true };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        var reset = await PlanAndSignAsync(fixture, intent, "ResetAllowance");
        fixture.CurrentAllowance = 0;
        var approve = await PlanAndSignAsync(fixture, intent, "ApproveExact");
        fixture.CurrentAllowance = 1010;
        var swap = await PlanAndSignAsync(fixture, intent, "Swap");
        var history = new List<SwapStageHistory> { new(reset.Prepared, Allowance(reset)), new(approve.Prepared, Allowance(approve)),
            new(swap.Prepared, new("FailedOnChain", swap.Prepared.TransactionIds, "eip155:31337", "0", "0", "0", "21000000000000", "0",
                "{\"Receipt\":{\"Succeeded\":false}}")) };
        var consent = new SwapCleanupConsent(1, intent.OperationId, intent.QuoteHash, new string('a', 64), intent.Terms.ActorUserId, null,
            intent.Terms.Owner, intent.Terms.InputAsset.Id, SwapCleanupContract.RevokeCurrentAllowance, "1000000000000000", false, true);

        (await fixture.Provider.PlanSwapCleanupAsync(intent, history, consent with
        {
            Semantics = SwapCleanupContract.CloseCurrentZeroHolding, AcceptCurrentZeroHoldingReplacement = true, AcceptCurrentAllowanceRevocation = false,
        }, "RevokeAllowance", 0)).IsError.Should().BeTrue("closing a holding is not consent to revoke an allowance");
        (await fixture.Provider.PlanSwapCleanupAsync(intent, history, consent with { AssetId = intent.Terms.OutputAsset.Id }, "RevokeAllowance", 0))
            .IsError.Should().BeTrue();
        (await fixture.Provider.PlanSwapCleanupAsync(intent, history, consent with { MaximumNetworkFee = "1" }, "RevokeAllowance", 0))
            .IsError.Should().BeTrue("the cleanup gas ceiling must fit its separately accepted cap");
        var planned = await fixture.Provider.PlanSwapCleanupAsync(intent, history, consent, "RevokeAllowance", 0);
        planned.IsError.Should().BeFalse(planned.Message);
        var plan = planned.Result!;
        (plan.Purpose, plan.Stage, plan.CleanupConsent).Should().Be(("Cleanup", "RevokeAllowance", consent));
        var unsigned = Transaction1559Encoder.Current.Decode(Convert.FromBase64String(JsonSerializer.Deserialize<string>(plan.UnsignedPayloadJson)!));
        EthereumAddress.AreSame(Hex(unsigned.ReceiverAddress), Address(6)).Should().BeTrue();
        Hex(unsigned.Data).Should().BeEquivalentTo(EthereumAbi.EncodeCall("approve(address,uint256)", Address(1), BigInteger.Zero));
        (unsigned.Nonce!.Value > PlanNonce(swap.Prepared.Plan)).Should().BeTrue("cleanup reserves a fresh nonce");
        (await fixture.Provider.PlanSwapCleanupAsync(intent, history, consent, "RevokeAllowance", 0)).Result.Should().BeEquivalentTo(plan);

        var cleanup = Signature(fixture, intent, plan, alternate: false);
        var chain = new ScriptedChain(fixture) { Head = 30 };
        chain.Include(cleanup.Wire, fixture.OwnerKey.GetPublicAddress(), 25, true,
            [Log(Address(6), cleanup.Id, chain.BlockHash(25), Topic("Approval(address,address,uint256)"),
                [Word(fixture.OwnerKey.GetPublicAddress()), Word(Address(1))], Uint(0))]);
        fixture.CurrentAllowance = 0;
        var revoked = (await fixture.Provider.ObserveSwapStageAsync(intent, cleanup.Prepared)).Result!;
        revoked.Verdict.Should().Be("Confirmed");
        (revoked.ActualInput, revoked.ActualOutput, revoked.PlatformFeePaid).Should().Be(("0", "0", "0"));
        revoked.EvidenceJson.Should().Contain("\"Effect\":\"RevokeAllowance\"");
        (await fixture.Provider.ReleaseRetainedResourcesAsync(intent, "cleanup-stage", cleanup.Prepared, revoked)).Should().BeTrue();
        (await fixture.Provider.PlanSwapCleanupAsync(intent, history, consent, "RevokeAllowance", 1)).IsError
            .Should().BeTrue("a released allowance resource is no longer owned by the operation");
    }

    [Fact]
    public async Task Generic_allocator_recovery_never_replays_a_swap_owned_reservation()
    {
        using var fixture = new Fixture { PreparationEnabled = true, CurrentAllowance = 1010 };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        var swap = await PlanAndSignAsync(fixture, intent, "Swap");
        var row = fixture.Preparations.Snapshot.Single();
        row.State = "SubmissionAuthorized"; row.SignedTransactionBase64 = Convert.ToBase64String(swap.Wire); row.TransactionId = swap.Id;
        await fixture.Preparations.UpdateAsync(row, row.Version);
        _ = new ScriptedChain(fixture) { Head = 20 };

        await ((IEthereumRecoveryModule)fixture.Provider).ReplayAuthorizedAsync(CancellationToken.None);

        fixture.Sent.Should().BeEmpty("only the swap journal's live authorization and withdrawal checks may send");
    }

    [Fact]
    public async Task A_released_unsigned_swap_reservation_refills_its_nonce_while_a_signed_reservation_is_never_released()
    {
        using var fixture = new Fixture { PreparationEnabled = true, CurrentAllowance = 1010 };
        var intent = Intent((await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!);
        var abandoned = (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0)).Result!;
        var nonce = PlanNonce(abandoned);

        (await fixture.Provider.ReleaseUnsentStageAsync(intent, abandoned)).Should().BeTrue();
        (await fixture.Provider.ReleaseUnsentStageAsync(intent, abandoned)).Should().BeFalse("a released reservation is never released twice");
        (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0)).IsError.Should().BeTrue("the released plan cannot be resurrected");
        var successor = (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 1)).Result!;

        PlanNonce(successor).Should().Be(nonce, "a released, never-signed, never-exposed nonce is refilled instead of leaving a gap");
        var rows = fixture.Preparations.Snapshot;
        rows.Should().HaveCount(2, "the released row becomes a key-locking tombstone instead of being deleted");
        var tombstone = rows.Single(row => row.State == "ReleasedRefilled");
        tombstone.NonceSlot.Should().Be(tombstone.Id);
        (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0)).IsError.Should().BeTrue("the refilled key can never reserve a fresh nonce");
        fixture.Preparations.Snapshot.Should().HaveCount(2);
        var held = rows.Single(row => row.State == "Reserved");
        held.SignedTransactionBase64 = Convert.ToBase64String(Signature(fixture, intent, successor, alternate: false).Wire);
        await fixture.Preparations.UpdateAsync(held, held.Version);
        (await fixture.Provider.ReleaseUnsentStageAsync(intent, successor)).Should().BeFalse("a signed candidate may already sit in a wallet or mempool");
        fixture.Preparations.Snapshot.Single(row => row.Id == held.Id).State.Should().Be("Reserved");
    }

    private sealed record SignedStage(PreparedSwapStage Prepared, byte[] Wire, string Id);

    private static async Task<SignedStage> PlanAndSignAsync(Fixture fixture, SwapIntent intent, string stage)
    {
        var planned = await fixture.Provider.PlanSwapStageAsync(intent, stage, 0);
        planned.IsError.Should().BeFalse(planned.Message);
        return Signature(fixture, intent, planned.Result!, alternate: false);
    }

    private static SignedStage Signature(Fixture fixture, SwapIntent intent, SwapStagePlan plan, bool alternate)
    {
        var unsigned = Convert.FromBase64String(JsonSerializer.Deserialize<string>(plan.UnsignedPayloadJson)!);
        byte[] wire;
        if (alternate) wire = EthereumAlternateSignature.Sign(unsigned, fixture.OwnerKey);
        else
        {
            using var key = new SigningKeyMaterial(fixture.OwnerKey.GetPrivateKeyAsBytes());
            wire = new EthereumTransactionSigner().Sign(unsigned, key).Result!;
        }
        var prepared = fixture.Provider.ValidateSignedSwapStage(intent, plan, JsonSerializer.Serialize(Convert.ToBase64String(wire)));
        prepared.IsError.Should().BeFalse(prepared.Message);
        return new(prepared.Result!, wire, prepared.Result!.TransactionIds[0]);
    }

    private static byte[] SelfCancellation(Fixture fixture, BigInteger nonce)
    {
        var unsigned = new Transaction1559(31337, nonce, 2_000_000_000, 4_000_000_000, 21000, fixture.OwnerKey.GetPublicAddress(), 0, "0x", null).GetRLPEncodedRaw();
        using var key = new SigningKeyMaterial(fixture.OwnerKey.GetPrivateKeyAsBytes());
        return new EthereumTransactionSigner().Sign(unsigned, key).Result!;
    }

    private static byte[] SameCallRepriced(Fixture fixture, SwapStagePlan plan)
    {
        var planned = Transaction1559Encoder.Current.Decode(Convert.FromBase64String(JsonSerializer.Deserialize<string>(plan.UnsignedPayloadJson)!));
        var unsigned = new Transaction1559(31337, planned.Nonce!.Value, planned.MaxPriorityFeePerGas!.Value + 1, planned.MaxFeePerGas!.Value * 2,
            planned.GasLimit!.Value + 5000, planned.ReceiverAddress, planned.Amount!.Value, planned.Data, null).GetRLPEncodedRaw();
        using var key = new SigningKeyMaterial(fixture.OwnerKey.GetPrivateKeyAsBytes());
        return new EthereumTransactionSigner().Sign(unsigned, key).Result!;
    }

    private static BigInteger PlanNonce(SwapStagePlan plan)
    {
        using var saved = JsonDocument.Parse(plan.RecoveryMetadata);
        return BigInteger.Parse(saved.RootElement.GetProperty("Nonce").GetString()!, CultureInfo.InvariantCulture);
    }

    private static SwapStageObservation Allowance(SignedStage stage)
        => new("Confirmed", stage.Prepared.TransactionIds, "eip155:31337", "0", "0", "0", "21000000000000", "0", "{\"AllowanceAfter\":\"0\"}");

    private static SwapStageObservation Settled(SignedStage stage, string allowanceAfter)
        => new("Confirmed", stage.Prepared.TransactionIds, "eip155:31337", "1010", "1995", "10", "21000000000000", "0",
            "{\"AllowanceAfter\":\"" + allowanceAfter + "\"}");

    private static IEnumerable<object> SwapLogs(Fixture fixture, SwapIntent intent, string id, string block, int output)
    {
        var owner = fixture.OwnerKey.GetPublicAddress();
        var transfer = Topic("Transfer(address,address,uint256)");
        yield return Log(Address(1), id, block, Topic(Executed),
            ["0x" + EthereumDexExecutionCodec.OperationIdentity(intent.OperationId), "0x" + intent.QuoteHash, Word(owner)],
            Data(Word(owner), Word(Address(10)), Word(Address(6)), Word(Address(7)), Uint(1010), Uint(10), Uint(output), Word(Address(9)), Uint(1)));
        yield return Log(Address(10), id, block, Topic("Swap(address,address,int256,int256,uint160,uint128,int24)"),
            [Word(Address(2)), Word(Address(1))], Data(Int(1000), Int(-output), Uint(BigInteger.One << 96), Uint(1_000_000), Int(-5)));
        yield return Log(Address(6), id, block, transfer, [Word(owner), Word(Address(1))], Uint(1010));
        yield return Log(Address(6), id, block, transfer, [Word(Address(1)), Word(Address(9))], Uint(10));
        yield return Log(Address(6), id, block, Topic("Approval(address,address,uint256)"), [Word(Address(1)), Word(Address(2))], Uint(1000));
        yield return Log(Address(6), id, block, transfer, [Word(Address(1)), Word(Address(10))], Uint(1000));
        yield return Log(Address(7), id, block, transfer, [Word(Address(10)), Word(Address(1))], Uint(output));
        yield return Log(Address(7), id, block, transfer, [Word(Address(1)), Word(owner)], Uint(output));
    }

    private static object Log(string address, string id, string block, string topic, string[] topics, string data)
        => new { address, topics = new[] { topic }.Concat(topics).ToArray(), data, transactionHash = id, blockHash = block, removed = false };

    private static string Topic(string signature) => "0x" + new Sha3Keccack().CalculateHash(signature);
    private static string Word(string address) => "0x" + new string('0', 24) + address[2..].ToLowerInvariant();
    private static string Uint(BigInteger value) => "0x" + Convert.ToHexStringLower(Pad(value.ToByteArray(true, true), 0));
    private static string Int(BigInteger value) => "0x" + Convert.ToHexStringLower(Pad(value.ToByteArray(false, true), value < 0 ? (byte)0xff : (byte)0));
    private static string Data(params string[] words) => "0x" + string.Concat(words.Select(word => word[2..]));
    private static byte[] Pad(byte[] bytes, byte fill) => Enumerable.Repeat(fill, 32 - bytes.Length).Concat(bytes).ToArray();
    private static string Hex(string? value) => value is null ? "0x" : value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value : "0x" + value;

    /// <summary>Finalized chain double: included transactions by block, sender nonce history and pruned historical state.</summary>
    private sealed class ScriptedChain
    {
        private readonly Fixture _fixture;
        private readonly Dictionary<string, (object Transaction, object Receipt, BigInteger Block)> _included = new(StringComparer.OrdinalIgnoreCase);
        internal BigInteger Nonce;
        internal BigInteger? ConsumedAt;
        internal BigInteger PrunedBelow;
        internal bool KnownTransaction;

        internal ScriptedChain(Fixture fixture)
        {
            _fixture = fixture;
            fixture.Chain = Respond;
        }

        internal BigInteger Head { get => _fixture.Head; init => _fixture.Head = value; }
        internal string BlockHash(BigInteger number) => _fixture.BlockHash(number);

        internal void Include(byte[] wire, string from, BigInteger block, bool succeeded, IEnumerable<object> logs)
        {
            var transaction = Transaction1559Encoder.Current.Decode(wire);
            var id = EthereumHex.Data(new Sha3Keccack().CalculateHash(wire));
            var parity = EthereumHex.Quantity(new BigInteger(transaction.Signature.V, true, true));
            var rpc = new Dictionary<string, object>
            {
                ["type"] = "0x2", ["chainId"] = "0x7a69", ["nonce"] = EthereumHex.Quantity(transaction.Nonce!.Value),
                ["maxPriorityFeePerGas"] = EthereumHex.Quantity(transaction.MaxPriorityFeePerGas!.Value),
                ["maxFeePerGas"] = EthereumHex.Quantity(transaction.MaxFeePerGas!.Value), ["gas"] = EthereumHex.Quantity(transaction.GasLimit!.Value),
                ["to"] = Hex(transaction.ReceiverAddress), ["value"] = EthereumHex.Quantity(transaction.Amount!.Value),
                ["input"] = Hex(transaction.Data), ["from"] = from, ["accessList"] = Array.Empty<object>(),
                ["r"] = EthereumHex.Quantity(new BigInteger(transaction.Signature.R, true, true)),
                ["s"] = EthereumHex.Quantity(new BigInteger(transaction.Signature.S, true, true)), ["v"] = parity, ["yParity"] = parity,
                ["hash"] = id, ["blockHash"] = BlockHash(block), ["blockNumber"] = EthereumHex.Quantity(block), ["transactionIndex"] = "0x0",
            };
            var receipt = new { transactionHash = id, blockHash = BlockHash(block), blockNumber = EthereumHex.Quantity(block),
                status = succeeded ? "0x1" : "0x0", gasUsed = "0x5208", effectiveGasPrice = "0x3b9aca00", transactionIndex = "0x0", logs = logs.ToArray() };
            _included[id] = (rpc, receipt, block);
            Nonce = transaction.Nonce!.Value;
            ConsumedAt ??= block;
        }

        private object? Respond(string method, JsonElement args)
        {
            switch (method)
            {
                case "eth_getTransactionByHash":
                    return _included.TryGetValue(args[0].GetString()!, out var found) ? found.Transaction : Fixture.Null;
                case "eth_getTransactionReceipt":
                    return _included.TryGetValue(args[0].GetString()!, out var mined) ? mined.Receipt : Fixture.Null;
                case "eth_getBlockByHash":
                {
                    var number = Number(args[0].GetString()!);
                    return new { hash = BlockHash(number), number = EthereumHex.Quantity(number), transactions = Bodies(number).Select(item => item.Key).ToArray() };
                }
                case "eth_getBlockByNumber" when args[0].GetString() is not ("0x0" or "finalized" or "latest"):
                {
                    EthereumHex.TryParseQuantity(args[0].GetString(), out var number);
                    return args[1].GetBoolean()
                        ? new { number = EthereumHex.Quantity(number), hash = BlockHash(number), transactions = Bodies(number).Select(item => item.Value.Transaction).ToArray() }
                        : new { number = EthereumHex.Quantity(number), hash = BlockHash(number), parentHash = BlockHash(number - 1),
                            timestamp = EthereumHex.Quantity(_fixture.HeadTime) };
                }
                case "eth_getTransactionCount" when args[1].ValueKind == JsonValueKind.Object:
                    return EthereumHex.Quantity(Count(Head));
                case "eth_getTransactionCount" when args[1].GetString() is not ("pending" or "latest"):
                {
                    EthereumHex.TryParseQuantity(args[1].GetString(), out var number);
                    return number < PrunedBelow ? new RpcFailure("missing trie node") : EthereumHex.Quantity(Count(number));
                }
                case "eth_sendRawTransaction":
                    return KnownTransaction ? new RpcFailure("already known")
                        : EthereumHex.Data(new Sha3Keccack().CalculateHash(Convert.FromHexString(args[0].GetString()![2..])));
                default:
                    return null;
            }
        }

        private BigInteger Count(BigInteger block) => ConsumedAt is { } consumed && block >= consumed ? Nonce + 1 : Nonce;

        private IEnumerable<KeyValuePair<string, (object Transaction, object Receipt, BigInteger Block)>> Bodies(BigInteger block)
            => _included.Where(item => item.Value.Block == block);

        private BigInteger Number(string hash)
            => hash == _fixture.QuoteBlock ? 10 : BigInteger.Parse("0" + hash[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}

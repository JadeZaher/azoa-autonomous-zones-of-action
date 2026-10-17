using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

// Round-4 interleaving gate: partial extension of the swap rig; see Round4Gates/Interleaving/AGENTS.md.
public sealed partial class SurrealSwapAdmissionTests
{
    private const int InterleavingRounds = 3;

    [Fact]
    public async Task InterleavedAllowanceClaimNeverCrossesTheOldOperationsFirstSendOrCancellation()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "ethereum_dex_allowance");
        var allowances = new SurrealEthereumDexAllowanceStore(_executor);
        for (var round = 0; round < InterleavingRounds; round++)
        {
            var executor = "0x" + new string((char)('c' + round), 40);
            var (old, stage, oldAuthority, _) = await PreparedStageAsync("allowance-old-" + round);
            var stageId = SurrealRecordGuid.BareId(stage.Id);
            (await allowances.ClaimAsync(Allowance(old, executor))).Should().NotBeNull("the old operation holds the lane first");
            var (quote, terms, newAuthority) = await SeedAsync();
            var next = Operation(quote, terms, "allowance-new-" + round);
            (await _store.AdmitAsync(next, newAuthority)).Should().NotBeNull();

            var authorize = _store.AuthorizeStageAsync(old.Id, 2, stageId, 1, oldAuthority);
            var cancel = _store.CancelUnsentStageAsync(old.Id, 2, stageId, 1, Consent(oldAuthority));
            var claim = allowances.ClaimAsync(Allowance(next, executor));
            await Task.WhenAll(authorize, cancel, claim);
            var authorizeOutcome = await authorize;
            var cancelOutcome = await cancel;
            var claimOutcome = await claim;

            new[] { authorizeOutcome, cancelOutcome }.Where(result => result is not null)
                .Should().ContainSingle("first send and unsent cancellation share one stage version");
            var oldStage = (await _store.GetStageAsync(stageId))!;
            var oldOperation = (await _store.GetAsync(old.Id))!;
            var allowanceId = EthereumDexAllowanceIdentity.Create(AllowanceProtocol, AllowanceOwner, AllowanceToken, executor);
            if (oldStage.AuthorizedAt is not null)
            {
                oldStage.State.Should().Be("SubmissionAuthorized");
                oldOperation.State.Should().Be("Pending");
                claimOutcome.Should().BeNull("an operation that may broadcast keeps its allowance lane");
                (await allowances.ClaimAsync(Allowance(next, executor))).Should().BeNull("the loser re-reads the authorized winner");
                (await allowances.GetAsync(allowanceId))!.OperationId.Should().Be(old.Id);
            }
            else
            {
                oldStage.State.Should().Be("Cancelled");
                oldOperation.State.Should().Be("Cancelled");
                (await _store.AuthorizeStageAsync(old.Id, 3, stageId, 2, oldAuthority)).Should().BeNull("a cancelled stage never sends");
                var settled = await allowances.ClaimAsync(Allowance(next, executor));
                settled.Should().NotBeNull("a never-sent cancelled holder hands the lane over");
                if (claimOutcome is not null) claimOutcome.OperationId.Should().Be(next.Id);
                var row = (await allowances.GetAsync(allowanceId))!;
                row.OperationId.Should().Be(next.Id);
                row.State.Should().Be("Held");
            }
        }
    }

    [Fact]
    public async Task InterleavedFirstSendAndSubmissionWithdrawalOfAnExposedStageHaveOneWinner()
    {
        for (var round = 0; round < InterleavingRounds; round++)
        {
            var (quote, terms, authority) = await SeedAsync();
            var operation = Operation(quote, terms, "exposure-withdrawal-" + round);
            (await _store.AdmitAsync(operation, authority)).Should().NotBeNull();
            var plan = StagePlan(operation, terms);
            var stage = (await _store.ClaimInitialStageAsync(operation.Id, 0, plan, authority))!;
            var stageId = SurrealRecordGuid.BareId(stage.Id);
            stage = (await _store.ExposeStageAsync(operation.Id, 1, stageId, stage.Version, new string('f', 64), ["tx"], authority))!;
            stage = (await _store.SaveExposedCandidateAsync(operation.Id, 2, stageId, stage.Version,
                new PreparedSwapStage(plan, ["tx"], "{\"exposedSignatureFixture\":true}")))!;

            var authorize = _store.AuthorizeStageAsync(operation.Id, 3, stageId, stage.Version, authority);
            var withdraw = _store.WithdrawSubmissionAsync(operation.Id, 3, stageId, stage.Version, Consent(authority));
            await Task.WhenAll(authorize, withdraw);
            var authorizeOutcome = await authorize;
            var withdrawOutcome = await withdraw;

            new[] { authorizeOutcome, withdrawOutcome }.Where(result => result is not null)
                .Should().ContainSingle("both transitions compare-and-swap the same operation and stage versions");
            var saved = (await _store.GetStageAsync(stageId))!;
            (await _store.GetAsync(operation.Id))!.Version.Should().Be(4);
            saved.ExternalExposedAt.Should().Be(stage.ExternalExposedAt, "exposure is permanent history in both outcomes");
            if (saved.AuthorizedAt is not null)
            {
                saved.State.Should().Be("SubmissionAuthorized");
                saved.ServerSubmissionWithdrawnAt.Should().BeNull();
                var later = (await _store.WithdrawSubmissionAsync(operation.Id, 4, stageId, saved.Version, Consent(authority)))!;
                later.ServerSubmissionWithdrawnAt.Should().NotBeNull("withdrawal after authorization stops server replay only");
                later.AuthorizedAt.Should().Be(saved.AuthorizedAt);
            }
            else
            {
                saved.State.Should().Be("ExternalExposed");
                saved.ServerSubmissionWithdrawnAt.Should().NotBeNull();
                (await _store.AuthorizeStageAsync(operation.Id, 4, stageId, saved.Version, authority))
                    .Should().BeNull("a withdrawn submission is never authorized for server send");
                (await _store.GetStageAsync(stageId))!.AuthorizedAt.Should().BeNull();
            }
        }
    }

    private const string AllowanceProtocol = "eip155:31337:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AllowanceOwner = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AllowanceToken = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static EthereumDexAllowance Allowance(SwapOperationRecord operation, string executor) => new()
    {
        Id = EthereumDexAllowanceIdentity.Create(AllowanceProtocol, AllowanceOwner, AllowanceToken, executor),
        ProtocolIdentity = AllowanceProtocol, OwnerAddress = AllowanceOwner, TokenAddress = AllowanceToken,
        ExecutorAddress = executor, OperationId = operation.Id, IntentHash = operation.IntentHash,
    };
}

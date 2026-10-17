using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    [Theory]
    [InlineData(false, "None")]
    [InlineData(true, "None")]
    [InlineData(false, "Replace")]
    [InlineData(false, "WithdrawRace")]
    [InlineData(false, "Chain")]
    [InlineData(false, "TenantedRun")]
    public async Task WorkflowOpeningConsentCommitsLiabilityAndMandateTogetherAndCancelledSourceCannotRelease(bool cancelBeforeConsent, string recovery)
    {
        var open = Open(); var direct = Authority(); var now = DateTimeOffset.UtcNow;
        open = open with { Definition = open.Definition! with { AutoRelease = true, ReleaseNotBefore = now.AddMinutes(-1) } };
        var runId = Guid.NewGuid(); var nodeId = Guid.NewGuid(); var executionId = Guid.NewGuid();
        var frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { runId, nodeId, Actor = _actor, Intent = open }));
        var witness = new WorkflowSigningWitness(1, direct.Direct!.Credential,
            new(runId, nodeId, _actor, null, CanonicalJson.HashCanonical(frozen)), [AzoaScopes.ActionExecute, AzoaScopes.TransferSign], []);
        var authority = new HoldCommandAuthority(1, Workflow: witness);
        await Save(new ActionRun { Id = runId.ToString("N"), ActionId = "action:" + Guid.NewGuid().ToString("N"),
            UserId = "user:" + _actor.ToString("N"), Status = ActionRun.ActionRunStatusKind.Running, StartedAt = now });
        await Save(new ActionNodeExecution { Id = executionId.ToString("N"), RunId = "action_run:" + runId.ToString("N"),
            NodeId = "action_node:" + nodeId.ToString("N"), State = ActionNodeExecution.ActionNodeState.Running,
            FrozenInput = frozen, SigningAuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(witness)) });
        var recipient = new SigningWalletWitness(Guid.NewGuid(), _actor, "recipient", "Algorand");
        await Save(new Wallet { Id = recipient.Id.ToString("N"), UserId = "user:" + _actor.ToString("N"),
            Address = recipient.Address, ChainType = recipient.ChainType, WalletType = Wallet.WalletTypeKind.Platform });
        var costs = new ValueCustodyCostBreakdown("reserve", "reserve", "1000", "0", "0");
        var quote = new ValueCustodyQuote(1, "", new("Algorand", ChainNetwork.Devnet, "42", "reserve", "recipient", "400", "reserve"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "0", "0", now, now.AddMinutes(5), costs);
        quote = quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
        var terms = new HoldReleaseMandateTerms(1, HoldCommandIdentity.Hold(open), CanonicalJson.Hash(open.Definition!), open.Asset,
            "400", recipient, open.Definition!.ReleaseNotBefore!.Value, now.AddDays(1), "ALGO", 6, costs);
        var draft = new HoldWorkflowMandateDraft(1, executionId.ToString("N"), terms, quote);
        var pending = (await _store.AdmitWorkflowOpeningAsync(open, authority, draft))!;
        pending.State.Should().Be("AwaitingConsent");
        (await _store.GetHoldAsync(terms.HoldRecordId)).Should().BeNull(); (await Counters()).Held.Should().Be(0);
        (await _store.CommitLocalAsync(open, authority)).Should().BeNull();
        var acceptance = new HoldWorkflowOpeningAcceptance(Guid.NewGuid(), pending.Version, CanonicalJson.Hash(draft));
        (await _store.CommitWorkflowOpeningAsync(pending, acceptance with { DraftHash = "different" }, direct.Direct)).Should().BeNull();
        if (recovery == "Replace")
        {
            var expired = quote with { QuotedAt = now.AddMinutes(-10), ExpiresAt = now.AddMinutes(-5) };
            expired = expired with { QuoteId = ValueCustodyIdentity.QuoteId(expired) };
            var expiredDraft = draft with { Quote = expired };
            pending.WorkflowMandateDraftJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(expiredDraft));
            await Execute(SurrealWriter.UpdateOnly<HoldCommand>(pending.Id)
                .Set(row => row.WorkflowMandateDraftJson, pending.WorkflowMandateDraftJson).Build());
            var renewal = new HoldRecoveryRequest(Guid.NewGuid(), pending.Version, "Refresh expired unaccepted disclosure.");
            var replaced = (await _store.ReplaceWorkflowOpeningAsync(pending, draft, renewal, direct.Direct))!;
            replaced.Version.Should().Be(pending.Version + 1);
            (await _store.CommitWorkflowOpeningAsync(pending, acceptance, direct.Direct)).Should().BeNull();
            (await _store.ReplaceWorkflowOpeningAsync(pending, draft, renewal, direct.Direct))!.Version.Should().Be(replaced.Version);
            (await Counters()).Held.Should().Be(0);
            (await _store.GetHoldAsync(terms.HoldRecordId)).Should().BeNull();
            pending = replaced;
            acceptance = new(Guid.NewGuid(), pending.Version, CanonicalJson.Hash(draft));
        }
        if (recovery == "WithdrawRace")
        {
            var withdrawal = new HoldRecoveryRequest(Guid.NewGuid(), pending.Version, "Withdraw before reserve draw.");
            var accepting = _store.CommitWorkflowOpeningAsync(pending, acceptance, direct.Direct);
            var withdrawing = _store.WithdrawWorkflowOpeningAsync(pending.Id, withdrawal, direct.Direct.Credential);
            await Task.WhenAll(accepting, withdrawing).WaitAsync(TimeSpan.FromSeconds(30));
            var accepted = await accepting; var withdrawn = await withdrawing;
            (accepted is not null).Should().NotBe(withdrawn is not null);
            (await Counters()).Held.Should().Be(accepted is null ? 0 : 400);
            if (withdrawn is not null)
            {
                withdrawn.State.Should().Be("Cancelled");
                (await _store.GetHoldAsync(terms.HoldRecordId)).Should().BeNull();
                (await _store.WithdrawWorkflowOpeningAsync(pending.Id, withdrawal, direct.Direct.Credential))!.Version.Should().Be(withdrawn.Version);
                (await _store.CommitWorkflowOpeningAsync(pending, acceptance, direct.Direct)).Should().BeNull();
            }
            return;
        }
        if (cancelBeforeConsent)
            await Execute(SurrealWriter.UpdateOnly<ActionRun>(runId.ToString("N")).Set(row => row.Status, ActionRun.ActionRunStatusKind.Cancelled).Build());
        var completed = await _store.CommitWorkflowOpeningAsync(pending, acceptance, direct.Direct);
        if (cancelBeforeConsent)
        {
            completed.Should().BeNull(); (await Counters()).Held.Should().Be(0);
            (await _executor.QueryAsync<HoldReleaseMandate>(SurrealQuery<HoldReleaseMandate>.From())).Should().BeEmpty();
            return;
        }
        completed.Should().NotBeNull(); (await Counters()).Held.Should().Be(400);
        var hold = (await _store.GetHoldAsync(terms.HoldRecordId))!;
        if (recovery == "Chain")
        {
            var chain = open with { RequestKey = "child-command", Kind = "ReleaseIntoHold", Definition = null,
                NextHoldId = "child", NextBeneficiaryUserId = _actor, NextReleaseNotBefore = now.AddHours(1), NextAutoRelease = true };
            var childNodeId = Guid.NewGuid(); var childExecutionId = Guid.NewGuid();
            var childFrozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { runId, childNodeId, Intent = chain }));
            var childWitness = witness with { Action = new(runId, childNodeId, _actor, null, CanonicalJson.HashCanonical(childFrozen)) };
            await Save(new ActionNodeExecution { Id = childExecutionId.ToString("N"), RunId = "action_run:" + runId.ToString("N"),
                NodeId = "action_node:" + childNodeId.ToString("N"), State = ActionNodeExecution.ActionNodeState.Running,
                FrozenInput = childFrozen, SigningAuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(childWitness)) });
            var childDefinition = open.Definition! with { ReleaseNotBefore = chain.NextReleaseNotBefore, AutoRelease = true };
            var childTerms = terms with { HoldRecordId = HoldCommandIdentity.Hold(chain, chain.NextHoldId),
                DefinitionHash = CanonicalJson.Hash(childDefinition), NotBefore = chain.NextReleaseNotBefore!.Value };
            var childDraft = draft with { ExecutionRecordId = childExecutionId.ToString("N"), Terms = childTerms };
            var childPending = (await _store.AdmitWorkflowOpeningAsync(chain, new(1, Workflow: childWitness), childDraft))!;
            (await Counters()).Held.Should().Be(400);
            var childReceipt = await _store.CommitWorkflowOpeningAsync(childPending,
                new(Guid.NewGuid(), childPending.Version, CanonicalJson.Hash(childDraft)), direct.Direct);
            childReceipt.Should().NotBeNull();
            var child = (await _store.GetHoldAsync(childTerms.HoldRecordId))!;
            child.ActiveReleaseMandateId.Should().NotBeNull();
            child.ActiveReleaseMandateId.Should().NotBe(hold.ActiveReleaseMandateId);
            (await _store.GetHoldAsync(hold.Id))!.State.Should().Be("ReleasedIntoHold");
            (await Counters()).Held.Should().Be(400);
            var childMandate = await new SurrealHoldReleaseMandateStore(_executor).GetAsync(child.ActiveReleaseMandateId!);
            childMandate!.HoldRecordId.Should().Be(child.Id);
            JsonSerializer.Deserialize<HoldReleaseWorkflowSource>(childMandate.WorkflowSourceJson!)!.OpeningCommandId.Should().Be(childPending.Id);
            return;
        }
        var mandates = new SurrealHoldReleaseMandateStore(_executor);
        var mandate = (await mandates.GetAsync(hold.ActiveReleaseMandateId!))!;
        mandate.WorkflowSourceJson.Should().NotBeNull();
        var release = open with { RequestKey = "mandate:" + mandate.Id, Kind = "Release", Definition = null };
        var plan = new HoldRailPlan(1, "Release", HoldCommandIdentity.Operation(release), 0, quote, null, recipient,
            SigningContext.PlatformFor("Algorand") with { Scope = AzoaScopes.TransferSign });
        var releaseAuthority = new HoldCommandAuthority(1, ReleaseMandateId: mandate.Id);
        (await _store.ClaimRailAsync(release, plan, releaseAuthority)).Should().BeNull();
        await Execute(SurrealWriter.UpdateOnly<ActionNodeExecution>(executionId.ToString("N")).Set(row => row.State, ActionNodeExecution.ActionNodeState.Succeeded).Build());
        await Execute(SurrealWriter.UpdateOnly<ActionRun>(runId.ToString("N")).Set(row => row.Status, ActionRun.ActionRunStatusKind.Succeeded).Build());
        if (recovery == "TenantedRun")
        {
            // Captured-null negative (HoldReleaseWorkflowFence run guard): a tenant-less mandate never matches a tenant-driven run.
            await Execute(SurrealWriter.UpdateOnly<ActionRun>(runId.ToString("N"))
                .Set(row => row.ActingTenantId, "user:" + Guid.NewGuid().ToString("N")).Build());
            (await _store.ClaimRailAsync(release, plan, releaseAuthority)).Should().BeNull();
            await Execute(SurrealWriter.UpdateOnly<ActionRun>(runId.ToString("N")).Set(row => row.ActingTenantId, (string?)null).Build());
        }
        var releaseCommand = (await _store.ClaimRailAsync(release, plan, releaseAuthority))!;
        releaseCommand.State.Should().Be("Admitted");
        await Execute(SurrealWriter.UpdateOnly<ActionRun>(runId.ToString("N")).Set(row => row.Status, ActionRun.ActionRunStatusKind.Cancelled).Build());
        (await _store.BeginPreparationAsync(releaseCommand)).Should().BeNull();
        (await _store.CommitWorkflowOpeningAsync(pending, acceptance, direct.Direct))!.OutcomeJson.Should().Be(completed!.OutcomeJson);
    }
}

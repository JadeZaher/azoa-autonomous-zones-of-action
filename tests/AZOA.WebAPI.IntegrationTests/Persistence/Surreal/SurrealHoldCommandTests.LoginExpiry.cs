using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Participant stages renew explicitly after the original login expires (Services/Hold/AGENTS.md §participant-renewal).</summary>
public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task ExpiredOriginalLoginRenewsTransferConsentWithoutRestartingTheStage()
    {
        var opening = Open();
        (await _store.CommitLocalAsync(opening, Authority())).Should().NotBeNull();
        var recipient = await RecipientWalletAsync();
        var release = opening with { RequestKey = "release", Kind = "Release", Definition = null };
        var quote = ReleaseQuote(recipient, DateTimeOffset.UtcNow);
        var plan = new HoldRailPlan(1, "Release", HoldCommandIdentity.Operation(release), 0, quote, null, recipient,
            SigningContext.PlatformFor("Algorand") with { Scope = AzoaScopes.TransferSign });
        var claimed = (await _store.ClaimRailAsync(release, plan, Authority()))!;

        (await _store.AuthorizeRailAsync(claimed, Consent(claimed, quote, ExpiredLogin()))).Should().BeNull();
        var unchanged = (await _store.GetCommandAsync(claimed.Id))!;
        unchanged.Version.Should().Be(claimed.Version);
        unchanged.State.Should().Be(claimed.State);

        var renewed = (await _store.AuthorizeRailAsync(claimed, Consent(claimed, quote, Authority())))!;
        renewed.State.Should().Be("Admitted");
        renewed.RailPlanJson.Should().Be(claimed.RailPlanJson);
        renewed.Generation.Should().Be(claimed.Generation);
        renewed.AcceptedQuoteId.Should().Be(quote.QuoteId);
    }

    [Fact]
    public async Task WorkflowWitnessOutlivesItsLoginAndTheParticipantRenewsOpeningConsent()
    {
        var open = Open(); var now = DateTimeOffset.UtcNow;
        open = open with { Definition = open.Definition! with { AutoRelease = true, ReleaseNotBefore = now.AddMinutes(-1) } };
        var runId = Guid.NewGuid(); var nodeId = Guid.NewGuid(); var executionId = Guid.NewGuid();
        var frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { runId, nodeId, Actor = _actor, Intent = open }));
        var witness = new WorkflowSigningWitness(1, ExpiredLogin().Direct!.Credential,
            new(runId, nodeId, _actor, null, CanonicalJson.HashCanonical(frozen)), [AzoaScopes.ActionExecute, AzoaScopes.TransferSign], []);
        await Save(new ActionRun { Id = runId.ToString("N"), ActionId = "action:" + Guid.NewGuid().ToString("N"),
            UserId = "user:" + _actor.ToString("N"), Status = ActionRun.ActionRunStatusKind.Running, StartedAt = now });
        await Save(new ActionNodeExecution { Id = executionId.ToString("N"), RunId = "action_run:" + runId.ToString("N"),
            NodeId = "action_node:" + nodeId.ToString("N"), State = ActionNodeExecution.ActionNodeState.Running,
            FrozenInput = frozen, SigningAuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(witness)) });
        var recipient = await RecipientWalletAsync();
        var quote = ReleaseQuote(recipient, now);
        var terms = new HoldReleaseMandateTerms(1, HoldCommandIdentity.Hold(open), CanonicalJson.Hash(open.Definition!), open.Asset,
            "400", recipient, open.Definition!.ReleaseNotBefore!.Value, now.AddDays(1), "ALGO", 6, quote.TransferCosts!);
        var draft = new HoldWorkflowMandateDraft(1, executionId.ToString("N"), terms, quote);

        var pending = (await _store.AdmitWorkflowOpeningAsync(open, new HoldCommandAuthority(1, Workflow: witness), draft))!;
        pending.State.Should().Be("AwaitingConsent", "a frozen workflow witness is fenced on revocation, not on login expiry");

        var acceptance = new HoldWorkflowOpeningAcceptance(Guid.NewGuid(), pending.Version, CanonicalJson.Hash(draft));
        (await _store.CommitWorkflowOpeningAsync(pending, acceptance, ExpiredLogin().Direct!)).Should().BeNull();
        (await Counters()).Held.Should().Be(0);
        (await _store.CommitWorkflowOpeningAsync(pending, acceptance with { RequestId = Guid.NewGuid() }, Authority().Direct!))
            .Should().NotBeNull();
        (await Counters()).Held.Should().Be(400);
    }

    private HoldCommandAuthority ExpiredLogin() => new(1, Direct: new(1,
        new(1, "Jwt", _actor, null, null, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddMinutes(-1),
            null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin), [AzoaScopes.TransferSign], []));

    private async Task<SigningWalletWitness> RecipientWalletAsync()
    {
        var recipient = new SigningWalletWitness(Guid.NewGuid(), _actor, "recipient", "Algorand");
        await Save(new Wallet { Id = recipient.Id.ToString("N"), UserId = "user:" + _actor.ToString("N"),
            Address = recipient.Address, ChainType = recipient.ChainType, WalletType = Wallet.WalletTypeKind.Platform });
        return recipient;
    }

    private static ValueCustodyQuote ReleaseQuote(SigningWalletWitness recipient, DateTimeOffset now)
    {
        var quote = new ValueCustodyQuote(1, "", new("Algorand", ChainNetwork.Devnet, "42", "reserve", recipient.Address, "400", "reserve"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "0", "0", now, now.AddMinutes(5),
            new ValueCustodyCostBreakdown("reserve", "reserve", "1000", "0", "0"));
        return quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
    }

    private static HoldCommandAuthorization Consent(HoldCommand row, ValueCustodyQuote quote, HoldCommandAuthority authority) => new()
    {
        Id = Guid.NewGuid().ToString("N"), OperationId = row.Id, ActorUserId = row.ActorUserId, ExpectedVersion = row.Version,
        RequestHash = Guid.NewGuid().ToString("N"), QuoteId = quote.QuoteId, MaximumNetworkFeeBaseUnits = "1000",
        AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority)), CreatedAt = DateTimeOffset.UtcNow,
    };
}

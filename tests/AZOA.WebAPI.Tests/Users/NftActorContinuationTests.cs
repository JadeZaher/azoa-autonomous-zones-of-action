using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Users;

/// <summary>A lapsed direct requester credential is renewed explicitly by the same requester, never by a participant.</summary>
public sealed class NftActorContinuationTests
{
    [Fact]
    public async Task LapsedIssuerAuthorityStallsAcceptanceUntilOnlyTheIssuerRenewsIt()
    {
        var h = new NftLifecycleHarness();
        var issuer = Guid.NewGuid(); var recipient = Guid.NewGuid();
        h.AddWallet(issuer); h.AddWallet(recipient);
        var request = NftLifecycleHarness.MintRequest(false); request.IsSoulbound = true;
        var issued = (await h.Service.MintAsync(issuer, recipient, request, "lapsed", credential: NftLifecycleHarness.Credential(issuer))).Result!;
        issued.State.Should().Be("AwaitingRecipientAcceptance");
        h.Clock.Offset = TimeSpan.FromHours(25);
        var waiting = (await h.Service.GetAsync(recipient, issued.OperationId)).Result!;
        waiting.NeedsActorContinuation.Should().BeTrue("the recipient learns why the stage waits");
        (await h.Service.RefreshAsync(recipient, issued.OperationId, true, credential: h.FreshCredential(recipient),
            acceptance: await h.Acceptance(issued.OperationId))).IsError.Should().BeTrue("the lapsed issuer witness is still fenced");
        var original = (await h.Store.GetAsync(issued.OperationId))!.RequestJson;
        var signatures = h.Prepared.Count;
        var renewal = new NftStageAcceptance(Guid.NewGuid(), waiting.Version, waiting.State, waiting.Stage, waiting.PreparationGeneration);

        (await h.Service.RenewActorAuthorityAsync(issued.OperationId, renewal, h.FreshCredential(recipient))).IsError.Should().BeTrue();
        (await h.Service.RenewActorAuthorityAsync(issued.OperationId, renewal, NftLifecycleHarness.Credential(issuer)))
            .IsError.Should().BeTrue("a renewal credential that has itself lapsed cannot commit");
        var renewed = await h.Service.RenewActorAuthorityAsync(issued.OperationId, renewal, h.FreshCredential(issuer));

        renewed.Result!.NeedsActorContinuation.Should().BeFalse();
        h.Prepared.Should().HaveCount(signatures, "renewal records permission only");
        (await h.Store.GetAsync(issued.OperationId))!.RequestJson.Should().Be(original);
        var accepted = await h.Service.RefreshAsync(recipient, issued.OperationId, true, credential: h.FreshCredential(recipient),
            acceptance: await h.Acceptance(issued.OperationId));
        accepted.Result!.Stage.Should().Be("Deliver");
        (await h.Service.RefreshAsync(issuer, issued.OperationId, false)).Result!.State.Should().Be("Completed",
            "delivery proceeds under the renewed requester authority");
    }

    [Theory]
    [InlineData("direct", true)]
    [InlineData("workflow-original", true)] // F-H12: the owner's direct credential takes over a workflow-admitted lifecycle
    [InlineData("other-actor", false)]
    [InlineData("other-tenant", false)]
    [InlineData("other-wallet", false)]
    [InlineData("other-scope", false)]
    public void RenewalBindsTheOriginalDirectRequesterTenantWalletAndOperationScope(string change, bool expected)
    {
        var actor = Guid.NewGuid(); var wallet = Guid.NewGuid();
        SigningAdmissionAuthority Direct(Guid user, Guid tenant, Guid walletId, string scope) => new(1, "DirectJwt",
            NftLifecycleHarness.Credential(user) with { TenantId = tenant == Guid.Empty ? null : tenant },
            new(walletId, user, "requester", "Algorand"), [scope], []);
        var original = Direct(actor, Guid.Empty, wallet, AzoaScopes.NftMint);
        var action = new ActionEffectAuthority(Guid.NewGuid(), Guid.NewGuid(), actor, null, new string('a', 64));
        var command = new NftLifecycleCommand(actor, actor, wallet, wallet, "requester", "requester", new UserNFTMintModel(), -1, Guid.Empty,
            ActionAuthority: change == "workflow-original" ? action : null,
            ActorAuthority: change == "workflow-original" ? original with { Kind = "Workflow", Action = action } : original);
        var row = new NftLifecycle { Id = Guid.NewGuid().ToString("N"), Operation = "Mint", ChainType = "Algorand", State = "Preparing" };
        var fresh = change switch
        {
            "other-actor" => Direct(Guid.NewGuid(), Guid.Empty, wallet, AzoaScopes.NftMint),
            "other-tenant" => Direct(actor, Guid.NewGuid(), wallet, AzoaScopes.NftMint),
            "other-wallet" => Direct(actor, Guid.Empty, Guid.NewGuid(), AzoaScopes.NftMint),
            "other-scope" => Direct(actor, Guid.Empty, wallet, AzoaScopes.TransferSign),
            _ => Direct(actor, Guid.Empty, wallet, AzoaScopes.NftMint),
        };
        var renewal = new NftActorContinuation(new(Guid.NewGuid(), 0, "Preparing", "Create", 0), fresh, DateTimeOffset.UtcNow);
        row.ActorContinuationsJson = JsonSerializer.Serialize(new[] { renewal });

        AZOA.WebAPI.Helpers.NftActorContinuationIdentity.Matches(row, command, renewal).Should().Be(expected);
        (JsonSerializer.Serialize(AZOA.WebAPI.Helpers.NftActorContinuationIdentity.Effective(row, command)) == JsonSerializer.Serialize(fresh))
            .Should().Be(expected,
            "a renewal that does not bind the original requester fails closed instead of falling back");
    }
}

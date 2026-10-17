using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Helpers;

public sealed class NftWorkflowCostAcceptanceIdentityTests
{
    [Fact]
    public void AcceptanceCannotMoveToAnotherExecutionActorOfferOrLifecycle()
    {
        var receipt = Receipt();
        NftWorkflowCostAcceptanceIdentity.Matches(receipt).Should().BeTrue();
        NftWorkflowCostAcceptanceIdentity.Matches(receipt with {
            Reference = receipt.Reference with { ActorUserId = Guid.NewGuid() } }).Should().BeFalse();
        NftWorkflowCostAcceptanceIdentity.Matches(receipt with {
            Reference = receipt.Reference with { OperationId = Guid.NewGuid().ToString("N") } }).Should().BeFalse();
        NftWorkflowCostAcceptanceIdentity.Matches(receipt with {
            OriginalAuthority = receipt.OriginalAuthority with { Action = receipt.OriginalAuthority.Action with { NodeId = Guid.NewGuid() } } }).Should().BeFalse();
        NftWorkflowCostAcceptanceIdentity.Matches(receipt with {
            Request = receipt.Request with { Authorization = receipt.Request.Authorization with { QuoteHash = new string('f', 64) } } }).Should().BeFalse();
        NftWorkflowCostAcceptanceIdentity.Matches(receipt with {
            AcceptingAuthority = receipt.AcceptingAuthority with { RequiredScopes = [AzoaScopes.ActionExecute] } }).Should().BeFalse();
    }

    [Fact]
    public void SameClientKeyHasStableAuditIdentityButChangedVersionIsNotTheSameRequest()
    {
        var receipt = Receipt();
        var actor = receipt.Reference.ActorUserId;
        var request = receipt.Request;
        var identity = NftWorkflowCostAcceptanceIdentity.Id(actor, null, request.ExecutionId, request.RequestId);
        identity.Should().NotBe(NftWorkflowCostAcceptanceIdentity.Id(actor, null, Guid.NewGuid(), request.RequestId));
        identity.Should().NotBe(NftWorkflowCostAcceptanceIdentity.Id(actor, Guid.NewGuid(), request.ExecutionId, request.RequestId));
        NftWorkflowCostAcceptanceIdentity.RequestHash(request, receipt.Reference.OperationId!, receipt.Reference.IntentHash)
            .Should().NotBe(NftWorkflowCostAcceptanceIdentity.RequestHash(request with { ExpectedExecutionVersion = 8 },
                receipt.Reference.OperationId!, receipt.Reference.IntentHash));
    }

    private static NftWorkflowCostAcceptanceReceipt Receipt()
    {
        var actor = Guid.NewGuid();
        var run = Guid.NewGuid();
        var node = Guid.NewGuid();
        var hash = new string('a', 64);
        var key = $"action:{run:N}:{node:N}:nft";
        var lifecycle = NftLifecycleIdentity.ForRequest(actor, "Mint", key);
        var credential = new SigningCredentialWitness(1, "AccessToken", actor, null, null, null, null,
            null, null, [AzoaScopes.ActionExecute, AzoaScopes.NftMint], true);
        var quote = new NftProtocolCostQuote(Guid.NewGuid().ToString("N"), actor, Guid.Empty, Guid.NewGuid(), "payer", "Solana",
            ChainNetwork.Devnet, "solana:fixture", lifecycle, hash, "Create", 0, "NFT", "{\"policy\":1}",
            new("1", "2", "3"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), hash);
        return new(1, new(Guid.NewGuid(), Guid.NewGuid(), 3, new(quote.QuoteId, NftProtocolCostContract.Hash(quote), quote.Limits)),
            new(1, "nft", key, lifecycle, hash, actor, null),
            new(1, credential, new(run, node, actor, null, hash), [AzoaScopes.ActionExecute, AzoaScopes.NftMint], []),
            new(1, credential, [AzoaScopes.NftMint], []), quote, DateTimeOffset.UtcNow);
    }
}

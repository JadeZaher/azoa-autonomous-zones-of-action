using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Interfaces.Managers;

namespace AZOA.WebAPI.Tests.Managers;

public class NftManagerTests
{
    private readonly NftLifecycleHarness _rail = new();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly IWallet _wallet;
    private readonly NftManager _manager;

    public NftManagerTests()
    {
        _wallet = _rail.AddWallet(_actor);
        _rail.Wallets.Setup(store => store.GetByIdAsync(_wallet.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IWallet>.Success(_wallet));
        _rail.Nfts.Setup(store => store.GetUserNFTsByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid owner, CancellationToken ct) =>
            {
                var rows = new List<IUserNFT>();
                foreach (var row in _rail.Store.Ownership.Values.Where(item => item.UserId == "user:" + owner.ToString("N")))
                {
                    var found = await _rail.Nfts.Object.GetUserNFTByIdAsync(Guid.Parse(row.Id), ct);
                    if (found.Result is not null) rows.Add(found.Result);
                }
                return AZOAResult<IEnumerable<IUserNFT>>.Success(rows);
            });
        _manager = new(_rail.Nfts.Object, _rail.Wallets.Object, _rail.Service);
    }

    private NftMintRequest Request() => new() { Name = "NFT", ChainId = "Algorand", WalletId = _wallet.Id, MetadataUri = "ipfs://metadata" };
    private AZOARequest Key(string key = "mint-key") => new() { IdempotencyKey = key, AuthenticationWitness = NftLifecycleHarness.Credential(_actor) };

    private async Task<Guid> MintConfirmedAsync()
    {
        var result = await _manager.MintAsync(Request(), _actor, Key());
        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(OperationStatus.Completed);
        return Guid.Parse(result.Result.Parameters["holonId"]);
    }

    [Fact]
    public async Task Mint_PendingChainEvidence_DoesNotWriteOwnership()
    {
        _rail.Confirm = false;
        var result = await _manager.MintAsync(Request(), _actor, Key());
        result.Code.Should().Be("NFT_PENDING");
        result.Result!.Status.Should().Be(OperationStatus.PendingConfirmation);
        _rail.Store.Ownership.Should().BeEmpty();
        _rail.Prepared.Should().ContainSingle();
    }

    [Fact]
    public async Task Mint_ConfirmedEvidence_PublishesOwnershipAndReplaysSameOperation()
    {
        var id = await MintConfirmedAsync();
        var replay = await _manager.MintAsync(Request(), _actor, Key());
        replay.IsError.Should().BeFalse();
        Guid.Parse(replay.Result!.Parameters["holonId"]).Should().Be(id);
        _rail.Store.Ownership.Should().ContainSingle();
        _rail.Prepared.Should().ContainSingle();
        (await _manager.GetAsync(id, _actor)).IsError.Should().BeFalse();
        (await _manager.GetAsync(id, Guid.NewGuid())).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Mint_ImportedTokenCannotEstablishIssuance()
    {
        var request = Request(); request.TokenId = "imported";
        (await _manager.MintAsync(request, _actor, Key())).IsError.Should().BeTrue();
        _rail.Prepared.Should().BeEmpty();
        _rail.Store.Ownership.Should().BeEmpty();
    }

    [Fact]
    public async Task Mint_UnverifiedActorOrTenant_NeverPrepares()
    {
        var tenant = Guid.NewGuid();
        _rail.Access.Setup(access => access.RequireValueAccessAsync(_actor, tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Failure(KycAuthorizationError.Forbidden + "Verification required"));
        var result = await _manager.MintAsync(Request(), _actor, Key(), tenant);
        result.IsError.Should().BeTrue(); result.Message.Should().StartWith(KycAuthorizationError.Forbidden);
        _rail.Prepared.Should().BeEmpty();
        _rail.Store.Ownership.Should().BeEmpty();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("invalid")]
    public async Task Mint_UnapprovedOrInvalidFee_NeverPrepares(string fee)
    {
        _rail.Configuration["NftFees:Policies:0:MintNativeBaseUnits"] = fee;
        (await _manager.MintAsync(Request(), _actor, Key())).IsError.Should().BeTrue();
        _rail.Prepared.Should().BeEmpty(); _rail.Store.Ownership.Should().BeEmpty();
    }

    [Fact]
    public async Task Query_FiltersChainAndRejectsAnotherOwnersScope()
    {
        await MintConfirmedAsync();
        (await _manager.QueryAsync(new() { ChainId = "algorand" }, _actor)).Result.Should().ContainSingle();
        (await _manager.QueryAsync(new() { ChainId = "Solana" }, _actor)).Result.Should().BeEmpty();
        (await _manager.QueryAsync(new() { OwnerUserId = Guid.NewGuid() }, _actor)).Result.Should().BeEmpty();
        (await _manager.QueryAsync(new(), null)).Result.Should().BeEmpty();
    }

    [Fact]
    public async Task Transfer_PendingEvidence_DoesNotChangeHolder()
    {
        var nftId = await MintConfirmedAsync();
        var recipient = Guid.NewGuid(); _rail.AddWallet(recipient);
        _rail.Confirm = false;
        var result = await _manager.TransferAsync(nftId, new() { WalletId = _wallet.Id, TargetUserId = recipient }, _actor, Key("transfer-key"));
        result.Code.Should().Be("NFT_PENDING");
        _rail.Store.Ownership[nftId.ToString("N")].UserId.Should().Be("user:" + _actor.ToString("N"));
    }

    [Fact]
    public async Task Transfer_WrongSigningWallet_DoesNotPrepareTransfer()
    {
        var nftId = await MintConfirmedAsync();
        _rail.Wallets.Setup(store => store.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IWallet>.Failure("Not found"));
        var result = await _manager.TransferAsync(nftId, new() { WalletId = Guid.NewGuid(), TargetUserId = Guid.NewGuid() }, _actor, Key("transfer-key"));
        result.IsError.Should().BeTrue();
        _rail.Prepared.Should().ContainSingle();
    }

    [Fact]
    public async Task Burn_PendingEvidence_DoesNotRemoveOwnership()
    {
        var nftId = await MintConfirmedAsync(); _rail.Confirm = false;
        var result = await _manager.BurnAsync(nftId, _wallet.Id, _actor, Key("burn-key"));
        result.Code.Should().Be("NFT_PENDING");
        _rail.Store.Ownership[nftId.ToString("N")].IsActive.Should().BeTrue();
    }
}

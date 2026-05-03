using FluentAssertions;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Providers;

namespace OASIS.WebAPI.Tests.Providers;

public class InMemoryStorageProviderTests
{
    private readonly InMemoryStorageProvider _provider = new();

    [Fact]
    public void ProviderName_ShouldBeInMemory()
    {
        _provider.ProviderName.Should().Be("InMemory");
    }

    [Fact]
    public async Task AvatarLifecycle_ShouldWork()
    {
        var avatar = new Avatar { Id = Guid.NewGuid(), Username = "test" };

        var save = await _provider.SaveAvatarAsync(avatar);
        save.Result.Should().Be(avatar);
        save.Message.Should().Be("Saved.");

        var load = await _provider.LoadAvatarAsync(avatar.Id);
        load.Result.Should().NotBeNull();
        load.Message.Should().Be("Success");

        var all = await _provider.LoadAllAvatarsAsync();
        all.Result.Should().ContainSingle();
        all.Message.Should().Be("Success");

        var del = await _provider.DeleteAvatarAsync(avatar.Id);
        del.Result.Should().BeTrue();
        del.Message.Should().Be("Deleted.");

        var missing = await _provider.LoadAvatarAsync(avatar.Id);
        missing.IsError.Should().BeTrue();
        missing.Message.Should().Be("Avatar not found.");
    }

    [Fact]
    public async Task DeleteMissingAvatar_ShouldReturnNotFound()
    {
        var del = await _provider.DeleteAvatarAsync(Guid.NewGuid());
        del.Result.Should().BeFalse();
        del.Message.Should().Be("Not found.");
    }

    [Fact]
    public async Task WalletLifecycle_ShouldWork()
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), Address = "addr" };

        var save = await _provider.SaveWalletAsync(wallet);
        save.Message.Should().Be("Saved.");

        var load = await _provider.LoadWalletAsync(wallet.Id);
        load.Result.Should().NotBeNull();
        load.Message.Should().Be("Success");

        var byAvatar = await _provider.LoadWalletsByAvatarAsync(wallet.AvatarId);
        byAvatar.Result.Should().ContainSingle();
        byAvatar.Message.Should().Be("Success");

        var del = await _provider.DeleteWalletAsync(wallet.Id);
        del.Result.Should().BeTrue();
        del.Message.Should().Be("Deleted.");
    }

    [Fact]
    public async Task DeleteMissingWallet_ShouldReturnNotFound()
    {
        var del = await _provider.DeleteWalletAsync(Guid.NewGuid());
        del.Result.Should().BeFalse();
        del.Message.Should().Be("Not found.");
    }

    [Fact]
    public async Task HolonQuery_WithFilters_ShouldFilter()
    {
        var avatarId = Guid.NewGuid();
        await _provider.SaveHolonAsync(new Holon { Id = Guid.NewGuid(), Name = "Alpha", AvatarId = avatarId, IsActive = true });
        await _provider.SaveHolonAsync(new Holon { Id = Guid.NewGuid(), Name = "Beta", AvatarId = Guid.NewGuid(), IsActive = false });
        await _provider.SaveHolonAsync(new Holon { Id = Guid.NewGuid(), Name = "Alpha", ProviderName = "Solana", AssetType = "NFT" });

        var query1 = await _provider.LoadAllHolonsAsync(new HolonQueryRequest { Name = "Alpha" });
        query1.Result.Should().HaveCount(2);
        query1.Message.Should().Be("Success");

        var query2 = await _provider.LoadAllHolonsAsync(new HolonQueryRequest { AvatarId = avatarId });
        query2.Result.Should().ContainSingle();

        var query3 = await _provider.LoadAllHolonsAsync(new HolonQueryRequest { IsActive = false });
        query3.Result.Should().ContainSingle();

        var query4 = await _provider.LoadAllHolonsAsync(new HolonQueryRequest { ProviderName = "Solana", AssetType = "NFT" });
        query4.Result.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteMissingHolon_ShouldReturnNotFound()
    {
        var del = await _provider.DeleteHolonAsync(Guid.NewGuid());
        del.Result.Should().BeFalse();
        del.Message.Should().Be("Not found.");
    }

    [Fact]
    public async Task BlockchainOperationLifecycle_ShouldWork()
    {
        var avatarId = Guid.NewGuid();
        var op = new BlockchainOperation { Id = Guid.NewGuid(), AvatarId = avatarId, OperationType = "Mint" };

        var save = await _provider.SaveBlockchainOperationAsync(op);
        save.Message.Should().Be("Saved.");

        var load = await _provider.LoadBlockchainOperationAsync(op.Id);
        load.Result.Should().NotBeNull();
        load.Message.Should().Be("Success");

        var byAvatar = await _provider.LoadBlockchainOperationsByAvatarAsync(avatarId);
        byAvatar.Result.Should().ContainSingle();
        byAvatar.Message.Should().Be("Success");

        var del = await _provider.DeleteBlockchainOperationAsync(op.Id);
        del.Result.Should().BeTrue();
        del.Message.Should().Be("Deleted.");
    }

    [Fact]
    public async Task DeleteMissingBlockchainOperation_ShouldReturnNotFound()
    {
        var del = await _provider.DeleteBlockchainOperationAsync(Guid.NewGuid());
        del.Result.Should().BeFalse();
        del.Message.Should().Be("Not found.");
    }

    [Fact]
    public async Task STARODKLifecycle_ShouldWork()
    {
        var odk = new STARODK { Id = Guid.NewGuid(), Name = "Test" };

        var save = await _provider.SaveSTARODKAsync(odk);
        save.Message.Should().Be("Saved.");

        var load = await _provider.LoadSTARODKAsync(odk.Id);
        load.Result.Should().NotBeNull();
        load.Message.Should().Be("Success");

        var all = await _provider.LoadAllSTARODKsAsync();
        all.Result.Should().ContainSingle();
        all.Message.Should().Be("Success");

        var del = await _provider.DeleteSTARODKAsync(odk.Id);
        del.Result.Should().BeTrue();
        del.Message.Should().Be("Deleted.");
    }

    [Fact]
    public async Task DeleteMissingSTARODK_ShouldReturnNotFound()
    {
        var del = await _provider.DeleteSTARODKAsync(Guid.NewGuid());
        del.Result.Should().BeFalse();
        del.Message.Should().Be("Not found.");
    }
}

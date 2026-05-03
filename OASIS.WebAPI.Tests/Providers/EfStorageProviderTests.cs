using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Providers;

namespace OASIS.WebAPI.Tests.Providers;

public class EfStorageProviderTests
{
    private static OASISDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OASISDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OASISDbContext(options);
    }

    [Fact]
    public void ProviderName_ShouldBePostgreSQL()
    {
        using var db = CreateDbContext();
        var provider = new EfStorageProvider(db);
        provider.ProviderName.Should().Be("PostgreSQL");
    }

    [Fact]
    public async Task AvatarLifecycle_ShouldWork()
    {
        using var db = CreateDbContext();
        var provider = new EfStorageProvider(db);
        var avatar = new Avatar { Id = Guid.NewGuid(), Username = "test", Email = "t@t.com" };

        var save = await provider.SaveAvatarAsync(avatar);
        save.Result.Should().Be(avatar);
        save.Message.Should().Be("Saved.");

        var load = await provider.LoadAvatarAsync(avatar.Id);
        load.Result.Should().NotBeNull();
        load.Message.Should().Be("Success");

        var all = await provider.LoadAllAvatarsAsync();
        all.Result.Should().ContainSingle();
        all.Message.Should().Be("Success");

        var del = await provider.DeleteAvatarAsync(avatar.Id);
        del.Result.Should().BeTrue();
        del.Message.Should().Be("Deleted.");

        var missing = await provider.LoadAvatarAsync(avatar.Id);
        missing.IsError.Should().BeTrue();
        missing.Message.Should().Be("Avatar not found.");
    }

    [Fact]
    public async Task DeleteMissingAvatar_ShouldReturnNotFound()
    {
        using var db = CreateDbContext();
        var provider = new EfStorageProvider(db);
        var del = await provider.DeleteAvatarAsync(Guid.NewGuid());
        del.Result.Should().BeFalse();
        del.Message.Should().Be("Avatar not found.");
    }

    [Fact]
    public async Task WalletLifecycle_ShouldWork()
    {
        using var db = CreateDbContext();
        var provider = new EfStorageProvider(db);
        var wallet = new Wallet { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), Address = "addr" };

        var save = await provider.SaveWalletAsync(wallet);
        save.Message.Should().Be("Saved.");

        var load = await provider.LoadWalletAsync(wallet.Id);
        load.Result.Should().NotBeNull();
        load.Message.Should().Be("Success");

        var byAvatar = await provider.LoadWalletsByAvatarAsync(wallet.AvatarId);
        byAvatar.Result.Should().ContainSingle();
        byAvatar.Message.Should().Be("Success");

        var del = await provider.DeleteWalletAsync(wallet.Id);
        del.Result.Should().BeTrue();
        del.Message.Should().Be("Deleted.");
    }

    [Fact]
    public async Task DeleteMissingWallet_ShouldReturnNotFound()
    {
        using var db = CreateDbContext();
        var provider = new EfStorageProvider(db);
        var del = await provider.DeleteWalletAsync(Guid.NewGuid());
        del.Result.Should().BeFalse();
        del.Message.Should().Be("Wallet not found.");
    }

    [Fact]
    public async Task HolonQuery_WithFilters_ShouldFilter()
    {
        using var db = CreateDbContext();
        var provider = new EfStorageProvider(db);
        var avatarId = Guid.NewGuid();
        await provider.SaveHolonAsync(new Holon { Id = Guid.NewGuid(), Name = "Alpha", AvatarId = avatarId, IsActive = true });
        await provider.SaveHolonAsync(new Holon { Id = Guid.NewGuid(), Name = "Beta", AvatarId = Guid.NewGuid(), IsActive = false });
        await provider.SaveHolonAsync(new Holon { Id = Guid.NewGuid(), Name = "Alpha", ProviderName = "Solana", AssetType = "NFT" });

        var query1 = await provider.LoadAllHolonsAsync(new HolonQueryRequest { Name = "Alpha" });
        query1.Result.Should().HaveCount(2);
        query1.Message.Should().Be("Success");

        var query2 = await provider.LoadAllHolonsAsync(new HolonQueryRequest { AvatarId = avatarId });
        query2.Result.Should().ContainSingle();

        var query3 = await provider.LoadAllHolonsAsync(new HolonQueryRequest { IsActive = false });
        query3.Result.Should().ContainSingle();

        var query4 = await provider.LoadAllHolonsAsync(new HolonQueryRequest { ProviderName = "Solana", AssetType = "NFT" });
        query4.Result.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteMissingHolon_ShouldReturnNotFound()
    {
        using var db = CreateDbContext();
        var provider = new EfStorageProvider(db);
        var del = await provider.DeleteHolonAsync(Guid.NewGuid());
        del.Result.Should().BeFalse();
        del.Message.Should().Be("Holon not found.");
    }

    [Fact]
    public async Task BlockchainOperationLifecycle_ShouldWork()
    {
        using var db = CreateDbContext();
        var provider = new EfStorageProvider(db);
        var avatarId = Guid.NewGuid();
        var op = new BlockchainOperation { Id = Guid.NewGuid(), AvatarId = avatarId, OperationType = "Mint" };

        var save = await provider.SaveBlockchainOperationAsync(op);
        save.Message.Should().Be("Saved.");

        var load = await provider.LoadBlockchainOperationAsync(op.Id);
        load.Result.Should().NotBeNull();
        load.Message.Should().Be("Success");

        var byAvatar = await provider.LoadBlockchainOperationsByAvatarAsync(avatarId);
        byAvatar.Result.Should().ContainSingle();
        byAvatar.Message.Should().Be("Success");

        var del = await provider.DeleteBlockchainOperationAsync(op.Id);
        del.Result.Should().BeTrue();
        del.Message.Should().Be("Deleted.");
    }

    [Fact]
    public async Task DeleteMissingBlockchainOperation_ShouldReturnNotFound()
    {
        using var db = CreateDbContext();
        var provider = new EfStorageProvider(db);
        var del = await provider.DeleteBlockchainOperationAsync(Guid.NewGuid());
        del.Result.Should().BeFalse();
        del.Message.Should().Be("Operation not found.");
    }

    [Fact]
    public async Task STARODKLifecycle_ShouldWork()
    {
        using var db = CreateDbContext();
        var provider = new EfStorageProvider(db);
        var odk = new STARODK { Id = Guid.NewGuid(), Name = "Test" };

        var save = await provider.SaveSTARODKAsync(odk);
        save.Message.Should().Be("Saved.");

        var load = await provider.LoadSTARODKAsync(odk.Id);
        load.Result.Should().NotBeNull();
        load.Message.Should().Be("Success");

        var all = await provider.LoadAllSTARODKsAsync();
        all.Result.Should().ContainSingle();
        all.Message.Should().Be("Success");

        var del = await provider.DeleteSTARODKAsync(odk.Id);
        del.Result.Should().BeTrue();
        del.Message.Should().Be("Deleted.");
    }

    [Fact]
    public async Task DeleteMissingSTARODK_ShouldReturnNotFound()
    {
        using var db = CreateDbContext();
        var provider = new EfStorageProvider(db);
        var del = await provider.DeleteSTARODKAsync(Guid.NewGuid());
        del.Result.Should().BeFalse();
        del.Message.Should().Be("STAR ODK not found.");
    }
}

using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealNftStore"/>.
///
/// Covers all three aggregate types:
/// <list type="bullet">
///   <item><see cref="UserNFT"/> via <c>nft_ownership</c> table</item>
///   <item><see cref="HolonNFTBinding"/> via <c>holon_nft_binding</c> table</item>
///   <item><see cref="WalletNFTBinding"/> via <c>wallet_nft_binding</c> table</item>
/// </list>
///
/// Tests skip gracefully via <see cref="SkippableFact"/> and <see cref="Skip.IfNot"/>
/// when the SurrealDB container is unavailable, so the test runner reports them
/// as Skipped instead of Passed.
/// </summary>
public sealed class SurrealNftStoreTests : IAsyncLifetime
{
    // ── Connection config ─────────────────────────────────────────────────────

    // Connection config sourced from SurrealTestDefaults (points at local instance).

    // ── Per-instance state ────────────────────────────────────────────────────

    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealNftStore _store = null!;
    private ISurrealConnection _connection = null!;
    private bool _surrealAvailable;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        _surrealAvailable = await ProbeSurrealAsync();
        if (!_surrealAvailable) return;

        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = _testNamespace,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password
        };

        _connection = new SurrealDbNetConnection(options);
        var executor = new DefaultSurrealExecutor(_connection);
        _store = new SurrealNftStore(executor);

        await BootstrapSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable || _connection is null) return;
        try { await DropNamespaceAsync(); }
        catch { /* best-effort */ }
        finally { _connection.Dispose(); }
    }

    // ── UserNFT Tests ───────────────────────────────────────────────────────

    /// <summary>Test 1: Upsert creates a UserNFT; GetById retrieves it with identical fields.</summary>
    [SkippableFact]
    public async Task UpsertUserNFT_ThenGetById_RoundTrips()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var nft = MakeNft(Guid.NewGuid(), "Ethereum",
                          "0x1234567890abcdef1234567890abcdef12345678",
                          "42");

        var upsertResult = await _store.UpsertUserNFTAsync(nft);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");
        upsertResult.Result.Should().NotBeNull();

        var getResult = await _store.GetUserNFTByIdAsync(nft.Id);

        getResult.IsError.Should().BeFalse();
        getResult.Message.Should().Be("Success");
        getResult.Result.Should().NotBeNull();
        getResult.Result!.Id.Should().Be(nft.Id);
        getResult.Result.UserId.Should().Be(nft.UserId);
        getResult.Result.ChainType.Should().Be("Ethereum");
        getResult.Result.NFTContractAddress.Should().Be(nft.NFTContractAddress);
        getResult.Result.TokenId.Should().Be("42");
    }

    /// <summary>Test 2: GetUserNFTByTokenId uses composite (chain, contract, tokenId) lookup.</summary>
    [SkippableFact]
    public async Task GetUserNFTByTokenId_CompositeKey_ReturnsCorrectNft()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var contract = $"0x{Guid.NewGuid():N}";
        var nft      = MakeNft(Guid.NewGuid(), "Ethereum", contract, "999");

        await _store.UpsertUserNFTAsync(nft);

        var result = await _store.GetUserNFTByTokenIdAsync("Ethereum", contract, "999");

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        result.Result.Should().NotBeNull();
        result.Result!.Id.Should().Be(nft.Id);
    }

    /// <summary>Test 3: GetUserNFTByTokenId with unknown composite key returns not-found.</summary>
    [SkippableFact]
    public async Task GetUserNFTByTokenId_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.GetUserNFTByTokenIdAsync(
            "Algorand", "NONEXISTENT_CONTRACT", "0");

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("NFT not found.");
        result.Result.Should().BeNull();
    }

    /// <summary>Test 4: GetUserNFTsByUser returns only NFTs owned by that user.</summary>
    [SkippableFact]
    public async Task GetUserNFTsByUser_FiltersCorrectly()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var targetUser = Guid.NewGuid();
        var otherUser  = Guid.NewGuid();

        var nft1 = MakeNft(targetUser, "Solana",   $"sol_{Guid.NewGuid():N}", "1");
        var nft2 = MakeNft(targetUser, "Ethereum", $"eth_{Guid.NewGuid():N}", "2");
        var nft3 = MakeNft(otherUser,  "Solana",   $"sol_{Guid.NewGuid():N}", "3");

        await _store.UpsertUserNFTAsync(nft1);
        await _store.UpsertUserNFTAsync(nft2);
        await _store.UpsertUserNFTAsync(nft3);

        var result = await _store.GetUserNFTsByUserAsync(targetUser);

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        var list = result.Result!.ToList();
        list.Should().HaveCount(2);
        list.All(n => n.UserId == targetUser).Should().BeTrue();
    }

    /// <summary>Test 5: DeleteUserNFT removes the record; subsequent GetById returns not-found.</summary>
    [SkippableFact]
    public async Task DeleteUserNFT_RemovesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var nft = MakeNft(Guid.NewGuid(), "Ethereum",
                          $"0x{Guid.NewGuid():N}", "77");

        await _store.UpsertUserNFTAsync(nft);

        var deleteResult = await _store.DeleteUserNFTAsync(nft.Id);

        deleteResult.IsError.Should().BeFalse();
        deleteResult.Result.Should().BeTrue();
        deleteResult.Message.Should().Be("Deleted.");

        var getResult = await _store.GetUserNFTByIdAsync(nft.Id);
        getResult.IsError.Should().BeTrue();
        getResult.Message.Should().Be("NFT not found.");
    }

    /// <summary>Test 6: Upsert (update path) overwrites existing UserNFT record.</summary>
    [SkippableFact]
    public async Task UpsertUserNFT_UpdatePath_OverwritesExistingRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var userId = Guid.NewGuid();
        var contract = $"0x{Guid.NewGuid():N}";

        var original = MakeNft(userId, "Ethereum", contract, "100");
        original.Name = "Original Name";
        await _store.UpsertUserNFTAsync(original);

        var updated = new UserNFT
        {
            Id                 = original.Id,
            UserId           = userId,
            ChainType          = "Ethereum",
            NFTContractAddress = contract,
            TokenId            = "100",
            TokenStandard      = "ERC721",
            MetadataURI        = "ipfs://updated",
            Name               = "Updated Name",
            IsActive           = true,
            IsTransferable     = true,
            MintedDate         = DateTime.UtcNow
        };

        var upsertResult = await _store.UpsertUserNFTAsync(updated);

        upsertResult.IsError.Should().BeFalse();

        var getResult = await _store.GetUserNFTByIdAsync(original.Id);
        getResult.Result!.Name.Should().Be("Updated Name");
        getResult.Result.MetadataURI.Should().Be("ipfs://updated");
    }

    // ── HolonNFTBinding Tests ─────────────────────────────────────────────────

    /// <summary>Test 7: Upsert + GetById round-trip for HolonNFTBinding.</summary>
    [SkippableFact]
    public async Task UpsertHolonNFTBinding_ThenGetById_RoundTrips()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var nftId   = Guid.NewGuid();
        var holonId = Guid.NewGuid();
        var binding = new HolonNFTBinding
        {
            Id          = Guid.NewGuid(),
            HolonId     = holonId,
            UserNFTId = nftId,
            Role        = "owner",
            IsActive    = true,
            CreatedDate = DateTime.UtcNow
        };

        var upsertResult = await _store.UpsertHolonNFTBindingAsync(binding);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");

        var getResult = await _store.GetHolonNFTBindingByIdAsync(binding.Id);

        getResult.IsError.Should().BeFalse();
        getResult.Result.Should().NotBeNull();
        getResult.Result!.Id.Should().Be(binding.Id);
        getResult.Result.HolonId.Should().Be(holonId);
        getResult.Result.UserNFTId.Should().Be(nftId);
        getResult.Result.Role.Should().Be("owner");
    }

    /// <summary>Test 8: GetHolonNFTBindingsByUserNFT filters by UserNFTId.</summary>
    [SkippableFact]
    public async Task GetHolonNFTBindingsByUserNFT_FiltersCorrectly()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var targetNftId = Guid.NewGuid();
        var otherNftId  = Guid.NewGuid();

        var b1 = new HolonNFTBinding
        {
            Id = Guid.NewGuid(), HolonId = Guid.NewGuid(), UserNFTId = targetNftId,
            Role = "owner", CreatedDate = DateTime.UtcNow, IsActive = true
        };
        var b2 = new HolonNFTBinding
        {
            Id = Guid.NewGuid(), HolonId = Guid.NewGuid(), UserNFTId = targetNftId,
            Role = "delegate", CreatedDate = DateTime.UtcNow, IsActive = true
        };
        var b3 = new HolonNFTBinding
        {
            Id = Guid.NewGuid(), HolonId = Guid.NewGuid(), UserNFTId = otherNftId,
            Role = "owner", CreatedDate = DateTime.UtcNow, IsActive = true
        };

        await _store.UpsertHolonNFTBindingAsync(b1);
        await _store.UpsertHolonNFTBindingAsync(b2);
        await _store.UpsertHolonNFTBindingAsync(b3);

        var result = await _store.GetHolonNFTBindingsByUserNFTAsync(targetNftId);

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        var list = result.Result!.ToList();
        list.Should().HaveCount(2);
        list.All(b => b.UserNFTId == targetNftId).Should().BeTrue();
    }

    /// <summary>Test 9: DeleteHolonNFTBinding removes the record.</summary>
    [SkippableFact]
    public async Task DeleteHolonNFTBinding_RemovesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var binding = new HolonNFTBinding
        {
            Id = Guid.NewGuid(), HolonId = Guid.NewGuid(), UserNFTId = Guid.NewGuid(),
            Role = "operator", CreatedDate = DateTime.UtcNow, IsActive = true
        };
        await _store.UpsertHolonNFTBindingAsync(binding);

        var deleteResult = await _store.DeleteHolonNFTBindingAsync(binding.Id);

        deleteResult.IsError.Should().BeFalse();
        deleteResult.Result.Should().BeTrue();

        var getResult = await _store.GetHolonNFTBindingByIdAsync(binding.Id);
        getResult.IsError.Should().BeTrue();
        getResult.Message.Should().Be("Binding not found.");
    }

    // ── WalletNFTBinding Tests ────────────────────────────────────────────────

    /// <summary>Test 10: Upsert + GetById round-trip for WalletNFTBinding.</summary>
    [SkippableFact]
    public async Task UpsertWalletNFTBinding_ThenGetById_RoundTrips()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var walletId = Guid.NewGuid();
        var nftId    = Guid.NewGuid();
        var binding  = new WalletNFTBinding
        {
            Id          = Guid.NewGuid(),
            WalletId    = walletId,
            UserNFTId = nftId,
            BindingType = "primary",
            AccessLevel = "full",
            IsActive    = true,
            CreatedDate = DateTime.UtcNow
        };

        var upsertResult = await _store.UpsertWalletNFTBindingAsync(binding);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");

        var getResult = await _store.GetWalletNFTBindingByIdAsync(binding.Id);

        getResult.IsError.Should().BeFalse();
        getResult.Result.Should().NotBeNull();
        getResult.Result!.Id.Should().Be(binding.Id);
        getResult.Result.WalletId.Should().Be(walletId);
        getResult.Result.UserNFTId.Should().Be(nftId);
        getResult.Result.BindingType.Should().Be("primary");
        getResult.Result.AccessLevel.Should().Be("full");
    }

    /// <summary>Test 11: GetWalletNFTBindingsByUserNFT filters by UserNFTId.</summary>
    [SkippableFact]
    public async Task GetWalletNFTBindingsByUserNFT_FiltersCorrectly()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var targetNftId = Guid.NewGuid();
        var otherNftId  = Guid.NewGuid();

        var b1 = new WalletNFTBinding
        {
            Id = Guid.NewGuid(), WalletId = Guid.NewGuid(), UserNFTId = targetNftId,
            BindingType = "primary", IsActive = true, CreatedDate = DateTime.UtcNow
        };
        var b2 = new WalletNFTBinding
        {
            Id = Guid.NewGuid(), WalletId = Guid.NewGuid(), UserNFTId = targetNftId,
            BindingType = "secondary", IsActive = true, CreatedDate = DateTime.UtcNow
        };
        var b3 = new WalletNFTBinding
        {
            Id = Guid.NewGuid(), WalletId = Guid.NewGuid(), UserNFTId = otherNftId,
            BindingType = "primary", IsActive = true, CreatedDate = DateTime.UtcNow
        };

        await _store.UpsertWalletNFTBindingAsync(b1);
        await _store.UpsertWalletNFTBindingAsync(b2);
        await _store.UpsertWalletNFTBindingAsync(b3);

        var result = await _store.GetWalletNFTBindingsByUserNFTAsync(targetNftId);

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        var list = result.Result!.ToList();
        list.Should().HaveCount(2);
        list.All(b => b.UserNFTId == targetNftId).Should().BeTrue();
    }

    /// <summary>Test 12: DeleteWalletNFTBinding removes the record.</summary>
    [SkippableFact]
    public async Task DeleteWalletNFTBinding_RemovesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var binding = new WalletNFTBinding
        {
            Id = Guid.NewGuid(), WalletId = Guid.NewGuid(), UserNFTId = Guid.NewGuid(),
            BindingType = "authorized", IsActive = true, CreatedDate = DateTime.UtcNow
        };
        await _store.UpsertWalletNFTBindingAsync(binding);

        var deleteResult = await _store.DeleteWalletNFTBindingAsync(binding.Id);

        deleteResult.IsError.Should().BeFalse();
        deleteResult.Result.Should().BeTrue();

        var getResult = await _store.GetWalletNFTBindingByIdAsync(binding.Id);
        getResult.IsError.Should().BeTrue();
        getResult.Message.Should().Be("Binding not found.");
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    private static async Task<bool> ProbeSurrealAsync()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var r = await probe.GetAsync($"{SurrealTestDefaults.Endpoint}/health");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Builds a minimal UserNFT for testing. The ID is derived from
    /// <paramref name="userId"/> unless an explicit ID is needed —
    /// callers always pass userId as first arg.
    /// </summary>
    private static UserNFT MakeNft(Guid userId, string chain, string contract, string tokenId)
        => new()
        {
            Id                 = Guid.NewGuid(),
            UserId           = userId,
            ChainType          = chain,
            NFTContractAddress = contract,
            TokenId            = tokenId,
            TokenStandard      = "ERC721",
            MetadataURI        = $"ipfs://{Guid.NewGuid():N}",
            Name               = $"NFT-{tokenId}",
            IsActive           = true,
            IsTransferable     = true,
            MintedDate         = DateTime.UtcNow
        };

    private Task BootstrapSchemaAsync()
        // Apply the real nft_ownership golden + the two SCHEMALESS join tables
        // (holon_nft_binding / wallet_nft_binding have no committed golden).
        => SurrealTestSchema.BootstrapWithExtraAsync(
            _testNamespace,
            extraDdl: "DEFINE TABLE IF NOT EXISTS holon_nft_binding SCHEMALESS; " +
                      "DEFINE TABLE IF NOT EXISTS wallet_nft_binding SCHEMALESS",
            "nft_ownership");

    private Task DropNamespaceAsync() => SurrealTestSchema.DropAsync(_testNamespace);
}

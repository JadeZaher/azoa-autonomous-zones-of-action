using FluentAssertions;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Connection;
using Azoa.SurrealDb.Client.Query;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Store-layer integration tests for <see cref="SurrealNftStore"/>.
///
/// Covers all three aggregate types:
/// <list type="bullet">
///   <item><see cref="AvatarNFT"/> via <c>nft_ownership</c> table</item>
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
    private HttpSurrealConnection _connection = null!;
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

        var http = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        _connection = new HttpSurrealConnection(http, options);
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

    // ── AvatarNFT Tests ───────────────────────────────────────────────────────

    /// <summary>Test 1: Upsert creates an AvatarNFT; GetById retrieves it with identical fields.</summary>
    [SkippableFact]
    public async Task UpsertAvatarNFT_ThenGetById_RoundTrips()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var nft = MakeNft(Guid.NewGuid(), "Ethereum",
                          "0x1234567890abcdef1234567890abcdef12345678",
                          "42");

        var upsertResult = await _store.UpsertAvatarNFTAsync(nft);

        upsertResult.IsError.Should().BeFalse();
        upsertResult.Message.Should().Be("Saved.");
        upsertResult.Result.Should().NotBeNull();

        var getResult = await _store.GetAvatarNFTByIdAsync(nft.Id);

        getResult.IsError.Should().BeFalse();
        getResult.Message.Should().Be("Success");
        getResult.Result.Should().NotBeNull();
        getResult.Result!.Id.Should().Be(nft.Id);
        getResult.Result.AvatarId.Should().Be(nft.AvatarId);
        getResult.Result.ChainType.Should().Be("Ethereum");
        getResult.Result.NFTContractAddress.Should().Be(nft.NFTContractAddress);
        getResult.Result.TokenId.Should().Be("42");
    }

    /// <summary>Test 2: GetAvatarNFTByTokenId uses composite (chain, contract, tokenId) lookup.</summary>
    [SkippableFact]
    public async Task GetAvatarNFTByTokenId_CompositeKey_ReturnsCorrectNft()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var contract = $"0x{Guid.NewGuid():N}";
        var nft      = MakeNft(Guid.NewGuid(), "Ethereum", contract, "999");

        await _store.UpsertAvatarNFTAsync(nft);

        var result = await _store.GetAvatarNFTByTokenIdAsync("Ethereum", contract, "999");

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        result.Result.Should().NotBeNull();
        result.Result!.Id.Should().Be(nft.Id);
    }

    /// <summary>Test 3: GetAvatarNFTByTokenId with unknown composite key returns not-found.</summary>
    [SkippableFact]
    public async Task GetAvatarNFTByTokenId_NotFound_ReturnsError()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var result = await _store.GetAvatarNFTByTokenIdAsync(
            "Algorand", "NONEXISTENT_CONTRACT", "0");

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("NFT not found.");
        result.Result.Should().BeNull();
    }

    /// <summary>Test 4: GetAvatarNFTsByAvatar returns only NFTs owned by that avatar.</summary>
    [SkippableFact]
    public async Task GetAvatarNFTsByAvatar_FiltersCorrectly()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var targetAvatar = Guid.NewGuid();
        var otherAvatar  = Guid.NewGuid();

        var nft1 = MakeNft(targetAvatar, "Solana",   $"sol_{Guid.NewGuid():N}", "1");
        var nft2 = MakeNft(targetAvatar, "Ethereum", $"eth_{Guid.NewGuid():N}", "2");
        var nft3 = MakeNft(otherAvatar,  "Solana",   $"sol_{Guid.NewGuid():N}", "3");

        await _store.UpsertAvatarNFTAsync(nft1);
        await _store.UpsertAvatarNFTAsync(nft2);
        await _store.UpsertAvatarNFTAsync(nft3);

        var result = await _store.GetAvatarNFTsByAvatarAsync(targetAvatar);

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        var list = result.Result!.ToList();
        list.Should().HaveCount(2);
        list.All(n => n.AvatarId == targetAvatar).Should().BeTrue();
    }

    /// <summary>Test 5: DeleteAvatarNFT removes the record; subsequent GetById returns not-found.</summary>
    [SkippableFact]
    public async Task DeleteAvatarNFT_RemovesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var nft = MakeNft(Guid.NewGuid(), "Ethereum",
                          $"0x{Guid.NewGuid():N}", "77");

        await _store.UpsertAvatarNFTAsync(nft);

        var deleteResult = await _store.DeleteAvatarNFTAsync(nft.Id);

        deleteResult.IsError.Should().BeFalse();
        deleteResult.Result.Should().BeTrue();
        deleteResult.Message.Should().Be("Deleted.");

        var getResult = await _store.GetAvatarNFTByIdAsync(nft.Id);
        getResult.IsError.Should().BeTrue();
        getResult.Message.Should().Be("NFT not found.");
    }

    /// <summary>Test 6: Upsert (update path) overwrites existing AvatarNFT record.</summary>
    [SkippableFact]
    public async Task UpsertAvatarNFT_UpdatePath_OverwritesExistingRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var avatarId = Guid.NewGuid();
        var contract = $"0x{Guid.NewGuid():N}";

        var original = MakeNft(avatarId, "Ethereum", contract, "100");
        original.Name = "Original Name";
        await _store.UpsertAvatarNFTAsync(original);

        var updated = new AvatarNFT
        {
            Id                 = original.Id,
            AvatarId           = avatarId,
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

        var upsertResult = await _store.UpsertAvatarNFTAsync(updated);

        upsertResult.IsError.Should().BeFalse();

        var getResult = await _store.GetAvatarNFTByIdAsync(original.Id);
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
            AvatarNFTId = nftId,
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
        getResult.Result.AvatarNFTId.Should().Be(nftId);
        getResult.Result.Role.Should().Be("owner");
    }

    /// <summary>Test 8: GetHolonNFTBindingsByAvatarNFT filters by AvatarNFTId.</summary>
    [SkippableFact]
    public async Task GetHolonNFTBindingsByAvatarNFT_FiltersCorrectly()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var targetNftId = Guid.NewGuid();
        var otherNftId  = Guid.NewGuid();

        var b1 = new HolonNFTBinding
        {
            Id = Guid.NewGuid(), HolonId = Guid.NewGuid(), AvatarNFTId = targetNftId,
            Role = "owner", CreatedDate = DateTime.UtcNow, IsActive = true
        };
        var b2 = new HolonNFTBinding
        {
            Id = Guid.NewGuid(), HolonId = Guid.NewGuid(), AvatarNFTId = targetNftId,
            Role = "delegate", CreatedDate = DateTime.UtcNow, IsActive = true
        };
        var b3 = new HolonNFTBinding
        {
            Id = Guid.NewGuid(), HolonId = Guid.NewGuid(), AvatarNFTId = otherNftId,
            Role = "owner", CreatedDate = DateTime.UtcNow, IsActive = true
        };

        await _store.UpsertHolonNFTBindingAsync(b1);
        await _store.UpsertHolonNFTBindingAsync(b2);
        await _store.UpsertHolonNFTBindingAsync(b3);

        var result = await _store.GetHolonNFTBindingsByAvatarNFTAsync(targetNftId);

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        var list = result.Result!.ToList();
        list.Should().HaveCount(2);
        list.All(b => b.AvatarNFTId == targetNftId).Should().BeTrue();
    }

    /// <summary>Test 9: DeleteHolonNFTBinding removes the record.</summary>
    [SkippableFact]
    public async Task DeleteHolonNFTBinding_RemovesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var binding = new HolonNFTBinding
        {
            Id = Guid.NewGuid(), HolonId = Guid.NewGuid(), AvatarNFTId = Guid.NewGuid(),
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
            AvatarNFTId = nftId,
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
        getResult.Result.AvatarNFTId.Should().Be(nftId);
        getResult.Result.BindingType.Should().Be("primary");
        getResult.Result.AccessLevel.Should().Be("full");
    }

    /// <summary>Test 11: GetWalletNFTBindingsByAvatarNFT filters by AvatarNFTId.</summary>
    [SkippableFact]
    public async Task GetWalletNFTBindingsByAvatarNFT_FiltersCorrectly()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var targetNftId = Guid.NewGuid();
        var otherNftId  = Guid.NewGuid();

        var b1 = new WalletNFTBinding
        {
            Id = Guid.NewGuid(), WalletId = Guid.NewGuid(), AvatarNFTId = targetNftId,
            BindingType = "primary", IsActive = true, CreatedDate = DateTime.UtcNow
        };
        var b2 = new WalletNFTBinding
        {
            Id = Guid.NewGuid(), WalletId = Guid.NewGuid(), AvatarNFTId = targetNftId,
            BindingType = "secondary", IsActive = true, CreatedDate = DateTime.UtcNow
        };
        var b3 = new WalletNFTBinding
        {
            Id = Guid.NewGuid(), WalletId = Guid.NewGuid(), AvatarNFTId = otherNftId,
            BindingType = "primary", IsActive = true, CreatedDate = DateTime.UtcNow
        };

        await _store.UpsertWalletNFTBindingAsync(b1);
        await _store.UpsertWalletNFTBindingAsync(b2);
        await _store.UpsertWalletNFTBindingAsync(b3);

        var result = await _store.GetWalletNFTBindingsByAvatarNFTAsync(targetNftId);

        result.IsError.Should().BeFalse();
        result.Message.Should().Be("Success");
        var list = result.Result!.ToList();
        list.Should().HaveCount(2);
        list.All(b => b.AvatarNFTId == targetNftId).Should().BeTrue();
    }

    /// <summary>Test 12: DeleteWalletNFTBinding removes the record.</summary>
    [SkippableFact]
    public async Task DeleteWalletNFTBinding_RemovesRecord()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var binding = new WalletNFTBinding
        {
            Id = Guid.NewGuid(), WalletId = Guid.NewGuid(), AvatarNFTId = Guid.NewGuid(),
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
    /// Builds a minimal AvatarNFT for testing. The ID is derived from
    /// <paramref name="avatarId"/> unless an explicit ID is needed —
    /// callers always pass avatarId as first arg.
    /// </summary>
    private static AvatarNFT MakeNft(Guid avatarId, string chain, string contract, string tokenId)
        => new()
        {
            Id                 = Guid.NewGuid(),
            AvatarId           = avatarId,
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

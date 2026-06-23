using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

/// <summary>
/// Integration tests for AvatarNFTController.
///
/// Rebuilt from the old EF-InMemory harness. All seeding is now done through
/// the real HTTP API or the SurrealDB test harness (IntegrationTestBase).
/// No AZOADbContext. No EF InMemory swap. No db.SaveChangesAsync.
///
/// Tests tagged [Trait("Category","SurrealDbFull")] require the SurrealDB
/// container + schema definitions (Worker C) and are skipped gracefully
/// when unavailable — the SurrealDbFull trait acts as a feature gate.
/// </summary>
public class AvatarNFTControllerIntegrationTests : IntegrationTestBase
{
    public AvatarNFTControllerIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    // ── Mint ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MintAvatarNFTAsync_WithValidRequest_ShouldReturnSuccess()
    {
        var mintModel = new AvatarNFTMintModel
        {
            ChainType           = "Solana",
            NFTContractAddress  = "11111111111111111111111111111111",
            TokenStandard       = "ERC721",
            MetadataURI         = "https://api.example.com/metadata/123",
            Name                = "Test Avatar NFT",
            Description         = "Integration test NFT",
            IsSoulbound         = false,
            IsTransferable      = true,
            Attributes          = new Dictionary<string, string> { { "level", "1" }, { "karma", "100" } }
        };

        var response = await Client.PostAsJsonAsync("/api/AvatarNFT/mint", mintModel, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<AvatarNFT>(response);
        result.Should().NotBeNull();
        result!.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Result!.ChainType.Should().Be("Solana");
        result.Result.Name.Should().Be("Test Avatar NFT");
    }

    // ── Get by ID ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAvatarNFTAsync_WithInvalidId_ShouldReturnNotFound()
    {
        var invalidId = Guid.NewGuid();

        var response = await Client.GetAsync($"/api/AvatarNFT/{invalidId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAvatarNFTAsync_WithValidId_ShouldReturnNFT()
    {
        // Mint first, then retrieve by the returned ID.
        var mintModel = new AvatarNFTMintModel
        {
            ChainType  = "Solana",
            Name       = "Retrievable NFT",
            MetadataURI = "https://api.example.com/metadata/ret"
        };
        var mintResponse = await Client.PostAsJsonAsync("/api/AvatarNFT/mint", mintModel, JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var minted = await ReadResultAsync<AvatarNFT>(mintResponse);
        minted!.Result.Should().NotBeNull();

        var response = await Client.GetAsync($"/api/AvatarNFT/{minted.Result!.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<AvatarNFT>(response);
        result!.Result!.Id.Should().Be(minted.Result.Id);
        result.Result.Name.Should().Be("Retrievable NFT");
    }

    // ── Get by avatar ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAvatarNFTsByAvatarAsync_WithValidAvatarId_ShouldReturnNFTs()
    {
        // Mint two NFTs for the default test avatar (TestAuthHandler.DefaultAvatarId).
        var avatarId = Guid.Parse(TestAuthHandler.DefaultAvatarId);

        await Client.PostAsJsonAsync("/api/AvatarNFT/mint",
            new AvatarNFTMintModel { ChainType = "Solana", Name = "NFT 1" }, JsonOptions);
        await Client.PostAsJsonAsync("/api/AvatarNFT/mint",
            new AvatarNFTMintModel { ChainType = "Solana", Name = "NFT 2" }, JsonOptions);

        var response = await Client.GetAsync($"/api/AvatarNFT/avatar/{avatarId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<AvatarNFT>>(response);
        result!.Result.Should().HaveCountGreaterOrEqualTo(2);
    }

    // ── Holon binding ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BindHolonToAvatarNFTAsync_WithValidIds_ShouldCreateBinding()
    {
        // Mint an NFT
        var mintResponse = await Client.PostAsJsonAsync("/api/AvatarNFT/mint",
            new AvatarNFTMintModel { ChainType = "Solana", Name = "Bindable NFT" }, JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<AvatarNFT>(mintResponse))!.Result!;

        // Create a holon to bind to
        var holon = await SeedHolonAsync(h => h.WithName("BindTarget"));

        var bindingModel = new HolonNFTBindingModel
        {
            Role             = "owner",
            PermissionLevel  = "full",
            Permissions      = new Dictionary<string, string> { { "read", "true" }, { "write", "true" } }
        };

        var response = await Client.PostAsJsonAsync(
            $"/api/AvatarNFT/{nft.Id}/holons/{holon.Id}/bind", bindingModel, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<HolonNFTBinding>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.HolonId.Should().Be(holon.Id);
        result.Result.AvatarNFTId.Should().Be(nft.Id);
        result.Result.Role.Should().Be("owner");
    }

    [Fact]
    public async Task GetHolonBindingsAsync_WithValidAvatarNFTId_ShouldReturnBindings()
    {
        // Mint + bind, then retrieve bindings.
        var mintResponse = await Client.PostAsJsonAsync("/api/AvatarNFT/mint",
            new AvatarNFTMintModel { ChainType = "Solana", Name = "Bound NFT" }, JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<AvatarNFT>(mintResponse))!.Result!;

        var holon = await SeedHolonAsync(h => h.WithName("BoundHolon"));
        await Client.PostAsJsonAsync($"/api/AvatarNFT/{nft.Id}/holons/{holon.Id}/bind",
            new HolonNFTBindingModel { Role = "owner", PermissionLevel = "full" }, JsonOptions);

        var response = await Client.GetAsync($"/api/AvatarNFT/{nft.Id}/holons");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<HolonNFTBinding>>(response);
        result!.Result.Should().HaveCountGreaterOrEqualTo(1);
        result.Result.Should().Contain(b => b.HolonId == holon.Id && b.Role == "owner");
    }

    // ── Verify access ─────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyHolonAccessAsync_WithValidPermissions_ShouldReturnTrue()
    {
        var mintResponse = await Client.PostAsJsonAsync("/api/AvatarNFT/mint",
            new AvatarNFTMintModel { ChainType = "Solana", Name = "AccessNFT" }, JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<AvatarNFT>(mintResponse))!.Result!;

        var holon = await SeedHolonAsync(h => h.WithName("AccessHolon"));
        await Client.PostAsJsonAsync($"/api/AvatarNFT/{nft.Id}/holons/{holon.Id}/bind",
            new HolonNFTBindingModel
            {
                Role        = "owner",
                Permissions = new Dictionary<string, string> { { "execute", "true" } }
            }, JsonOptions);

        var verificationRequest = new
        {
            AvatarNFTId        = nft.Id,
            HolonId            = holon.Id,
            RequiredPermission = "execute"
        };

        var response = await Client.PostAsJsonAsync("/api/AvatarNFT/verify-holon-access",
            verificationRequest, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<bool>(response);
        result!.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        result.Message.Should().Contain("verified");
    }

    // ── Composite ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAvatarNFTCompositeAsync_WithValidId_ShouldReturnComposite()
    {
        var mintResponse = await Client.PostAsJsonAsync("/api/AvatarNFT/mint",
            new AvatarNFTMintModel
            {
                ChainType          = "Solana",
                Name               = "CompositeNFT",
                NFTContractAddress = "11111111111111111111111111111111"
            }, JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<AvatarNFT>(mintResponse))!.Result!;

        var holon  = await SeedHolonAsync(h => h.WithName("CompositeHolon"));
        var walletAddr = "comp_" + Guid.NewGuid().ToString("N")[..8];
        var wallet = await SeedWalletAsync(w => w.ForAvatar(Guid.Parse(TestAuthHandler.DefaultAvatarId))
                                                   .OnChain("Solana")
                                                   .WithAddress(walletAddr));

        await Client.PostAsJsonAsync($"/api/AvatarNFT/{nft.Id}/holons/{holon.Id}/bind",
            new HolonNFTBindingModel { Role = "owner", PermissionLevel = "full" }, JsonOptions);
        await Client.PostAsJsonAsync($"/api/AvatarNFT/{nft.Id}/wallets/{wallet.Id}/bind",
            new WalletNFTBindingModel { BindingType = "primary" }, JsonOptions);

        var response = await Client.GetAsync($"/api/AvatarNFT/{nft.Id}/composite");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<AvatarNFTCompositeResult>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.AvatarNFTId.Should().Be(nft.Id);
        result.Result.Name.Should().Be("CompositeNFT");
        result.Result.HolonBindings.Should().ContainSingle(b => b.HolonId == holon.Id);
        result.Result.WalletBindings.Should().ContainSingle(b => b.WalletId == wallet.Id);
    }

    // ── Transfer ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TransferAvatarNFTAsync_WithValidRequest_ShouldTransfer()
    {
        var mintResponse = await Client.PostAsJsonAsync("/api/AvatarNFT/mint",
            new AvatarNFTMintModel
            {
                ChainType      = "Solana",
                Name           = "Transferable NFT",
                IsSoulbound    = false,
                IsTransferable = true
            }, JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<AvatarNFT>(mintResponse))!.Result!;

        var transferRequest = new { RecipientAddress = "new_owner_address" };
        var response = await Client.PostAsJsonAsync($"/api/AvatarNFT/{nft.Id}/transfer",
            transferRequest, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<bool>(response);
        result!.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        result.Message.Should().Contain("transferred successfully");
    }

    [Fact]
    public async Task TransferAvatarNFTAsync_WithSoulboundNFT_ShouldReturnError()
    {
        var mintResponse = await Client.PostAsJsonAsync("/api/AvatarNFT/mint",
            new AvatarNFTMintModel
            {
                ChainType   = "Solana",
                Name        = "Soulbound NFT",
                IsSoulbound = true
            }, JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<AvatarNFT>(mintResponse))!.Result!;

        var transferRequest = new { RecipientAddress = "new_owner_address" };
        var response = await Client.PostAsJsonAsync($"/api/AvatarNFT/{nft.Id}/transfer",
            transferRequest, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await ReadResultAsync<bool>(response);
        result!.IsError.Should().BeTrue();
        result.Message.Should().Contain("Cannot transfer soulbound NFT");
    }
}

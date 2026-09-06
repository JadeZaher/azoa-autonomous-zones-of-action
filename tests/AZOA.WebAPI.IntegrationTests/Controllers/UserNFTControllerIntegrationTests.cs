using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

/// <summary>
/// Integration tests for UserNFTController.
///
/// Rebuilt from the old EF-InMemory harness. All seeding is now done through
/// the real HTTP API or the SurrealDB test harness (IntegrationTestBase).
/// No AZOADbContext. No EF InMemory swap. No db.SaveChangesAsync.
///
/// Tests tagged [Trait("Category","SurrealDbFull")] require the SurrealDB
/// container + schema definitions (Worker C) and are skipped gracefully
/// when unavailable — the SurrealDbFull trait acts as a feature gate.
/// </summary>
public class UserNFTControllerIntegrationTests : IntegrationTestBase
{
    public UserNFTControllerIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    /// Build a mint model that satisfies UserNFTMintModelValidator's required
    /// fields (ChainType/NFTContractAddress/TokenStandard/MetadataURI). Callers
    /// override only what a given test asserts on. The mint path is chain-agnostic
    /// storage (no blockchain provider call), so "Solana" is a valid ChainType here.
    private static UserNFTMintModel ValidMint(string name) => new()
    {
        ChainType          = "Solana",
        NFTContractAddress = "11111111111111111111111111111111",
        TokenStandard      = "ERC721",
        MetadataURI        = "https://api.example.com/metadata/" + name.Replace(" ", ""),
        Name               = name
    };

    // ── Mint ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MintUserNFTAsync_WithValidRequest_ShouldReturnSuccess()
    {
        var mintModel = new UserNFTMintModel
        {
            ChainType           = "Solana",
            NFTContractAddress  = "11111111111111111111111111111111",
            TokenStandard       = "ERC721",
            MetadataURI         = "https://api.example.com/metadata/123",
            Name                = "Test User NFT",
            Description         = "Integration test NFT",
            IsSoulbound         = false,
            IsTransferable      = true,
            Attributes          = new Dictionary<string, string> { { "level", "1" }, { "karma", "100" } }
        };

        var response = await Client.PostAsJsonAsync("/api/UserNFT/mint", mintModel, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<UserNFT>(response);
        result.Should().NotBeNull();
        result!.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Result!.ChainType.Should().Be("Solana");
        result.Result.Name.Should().Be("Test User NFT");
    }

    // ── Get by ID ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUserNFTAsync_WithInvalidId_ShouldReturnNotFound()
    {
        var invalidId = Guid.NewGuid();

        var response = await Client.GetAsync($"/api/UserNFT/{invalidId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUserNFTAsync_WithValidId_ShouldReturnNFT()
    {
        // Mint first, then retrieve by the returned ID.
        var mintModel = ValidMint("Retrievable NFT");
        var mintResponse = await Client.PostAsJsonAsync("/api/UserNFT/mint", mintModel, JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var minted = await ReadResultAsync<UserNFT>(mintResponse);
        minted!.Result.Should().NotBeNull();

        var response = await Client.GetAsync($"/api/UserNFT/{minted.Result!.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<UserNFT>(response);
        result!.Result!.Id.Should().Be(minted.Result.Id);
        result.Result.Name.Should().Be("Retrievable NFT");
    }

    // ── Get by user ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUserNFTsByUserAsync_WithValidUserId_ShouldReturnNFTs()
    {
        // Mint two NFTs for the default test user (TestAuthHandler.DefaultUserId).
        var userId = Guid.Parse(TestAuthHandler.DefaultUserId);

        await Client.PostAsJsonAsync("/api/UserNFT/mint", ValidMint("NFT 1"), JsonOptions);
        await Client.PostAsJsonAsync("/api/UserNFT/mint", ValidMint("NFT 2"), JsonOptions);

        var response = await Client.GetAsync($"/api/UserNFT/user/{userId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<UserNFT>>(response);
        result!.Result.Should().HaveCountGreaterOrEqualTo(2);
    }

    // ── Holon binding ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BindHolonToUserNFTAsync_WithValidIds_ShouldCreateBinding()
    {
        // Mint an NFT
        var mintResponse = await Client.PostAsJsonAsync("/api/UserNFT/mint",
            ValidMint("Bindable NFT"), JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<UserNFT>(mintResponse))!.Result!;

        // Create a holon to bind to
        var holon = await SeedHolonAsync(h => h.WithName("BindTarget"));

        var bindingModel = new HolonNFTBindingModel
        {
            Role             = "owner",
            PermissionLevel  = "full",
            Permissions      = new Dictionary<string, string> { { "read", "true" }, { "write", "true" } }
        };

        var response = await Client.PostAsJsonAsync(
            $"/api/UserNFT/{nft.Id}/holons/{holon.Id}/bind", bindingModel, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<HolonNFTBinding>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.HolonId.Should().Be(holon.Id);
        result.Result.UserNFTId.Should().Be(nft.Id);
        result.Result.Role.Should().Be("owner");
    }

    [Fact]
    public async Task GetHolonBindingsAsync_WithValidUserNFTId_ShouldReturnBindings()
    {
        // Mint + bind, then retrieve bindings.
        var mintResponse = await Client.PostAsJsonAsync("/api/UserNFT/mint",
            ValidMint("Bound NFT"), JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<UserNFT>(mintResponse))!.Result!;

        var holon = await SeedHolonAsync(h => h.WithName("BoundHolon"));
        await Client.PostAsJsonAsync($"/api/UserNFT/{nft.Id}/holons/{holon.Id}/bind",
            new HolonNFTBindingModel { Role = "owner", PermissionLevel = "full" }, JsonOptions);

        var response = await Client.GetAsync($"/api/UserNFT/{nft.Id}/holons");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<HolonNFTBinding>>(response);
        result!.Result.Should().HaveCountGreaterOrEqualTo(1);
        result.Result.Should().Contain(b => b.HolonId == holon.Id && b.Role == "owner");
    }

    // ── IDOR: cross-user bind + read (audit fix) ────────────────────────────

    [Fact]
    public async Task BindHolonToUserNFTAsync_WithHolonOwnedByDifferentUser_ShouldReject()
    {
        // Attacker mints their own NFT.
        var mintResponse = await Client.PostAsJsonAsync("/api/UserNFT/mint",
            ValidMint("Attacker NFT"), JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var attackerNft = (await ReadResultAsync<UserNFT>(mintResponse))!.Result!;

        // Victim (a different user) owns the holon under attack.
        var victimUserId = Guid.Parse("c4444444-4444-4444-4444-444444444444");
        using var victimClient = Factory.CreateAuthenticatedClientForUser(victimUserId);
        var victimHolonResponse = await victimClient.PostAsJsonAsync("/api/holon",
            new HolonBuilder()
                .WithName(($"VictimHolon{Guid.NewGuid():N}")[..24])
                .WithDescription("owned by victim")
                .BuildCreateModel(),
            JsonOptions);
        victimHolonResponse.EnsureSuccessStatusCode();
        var victimHolon = (await victimHolonResponse.Content.ReadFromJsonAsync<AZOAResult<Holon>>(JsonOptions))!.Result!;

        // Attacker (default Client / DefaultUserId) tries to bind the victim's holon to their own NFT.
        var response = await Client.PostAsJsonAsync(
            $"/api/UserNFT/{attackerNft.Id}/holons/{victimHolon.Id}/bind",
            new HolonNFTBindingModel { Role = "owner", PermissionLevel = "full" }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a caller must not be able to bind a holon they do not own to their own UserNFT (bind-target IDOR)");
        var result = await response.Content.ReadFromJsonAsync<AZOAResult<HolonNFTBinding>>(JsonOptions);
        result!.IsError.Should().BeTrue();
        result.Message.Should().Contain("different user");
    }

    [Fact]
    public async Task BindWalletToUserNFTAsync_WithWalletOwnedByDifferentUser_ShouldReject()
    {
        var mintResponse = await Client.PostAsJsonAsync("/api/UserNFT/mint",
            ValidMint("Attacker Wallet NFT"), JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var attackerNft = (await ReadResultAsync<UserNFT>(mintResponse))!.Result!;

        var victimUserId = Guid.Parse("c5555555-5555-5555-5555-555555555555");
        using var victimClient = Factory.CreateAuthenticatedClientForUser(victimUserId);
        var walletAddr = "victim" + Guid.NewGuid().ToString("N")[..8];
        var victimWalletResponse = await victimClient.PostAsJsonAsync("/api/wallet",
            new WalletCreateModel { ChainType = "Solana", Address = walletAddr }, JsonOptions);
        victimWalletResponse.EnsureSuccessStatusCode();
        var victimWallet = (await victimWalletResponse.Content.ReadFromJsonAsync<AZOAResult<Wallet>>(JsonOptions))!.Result!;

        var response = await Client.PostAsJsonAsync(
            $"/api/UserNFT/{attackerNft.Id}/wallets/{victimWallet.Id}/bind",
            new WalletNFTBindingModel { BindingType = "primary" }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a caller must not be able to bind a wallet they do not own to their own UserNFT (bind-target IDOR)");
        var result = await response.Content.ReadFromJsonAsync<AZOAResult<WalletNFTBinding>>(JsonOptions);
        result!.IsError.Should().BeTrue();
        result.Message.Should().Contain("different user");
    }

    [Fact]
    public async Task GetUserNFTAsync_WithNFTOwnedByDifferentUser_ShouldReturnForbidden()
    {
        var mintResponse = await Client.PostAsJsonAsync("/api/UserNFT/mint",
            ValidMint("Private NFT"), JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<UserNFT>(mintResponse))!.Result!;

        var otherUserId = Guid.Parse("c6666666-6666-6666-6666-666666666666");
        using var otherClient = Factory.CreateAuthenticatedClientForUser(otherUserId);

        var response = await otherClient.GetAsync($"/api/UserNFT/{nft.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a foreign user must not be able to read another user's UserNFT by id (read-scoping IDOR)");
    }

    [Fact]
    public async Task GetUserNFTsByUserAsync_WithForeignUserId_ShouldReturnForbidden()
    {
        var userId = Guid.Parse(TestAuthHandler.DefaultUserId);
        var otherUserId = Guid.Parse("c7777777-7777-7777-7777-777777777777");
        using var otherClient = Factory.CreateAuthenticatedClientForUser(otherUserId);

        var response = await otherClient.GetAsync($"/api/UserNFT/user/{userId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a caller must not be able to list another user's UserNFTs via an arbitrary route userId");
    }

    [Fact]
    public async Task GetHolonBindingsAsync_WithNFTOwnedByDifferentUser_ShouldReturnForbidden()
    {
        var mintResponse = await Client.PostAsJsonAsync("/api/UserNFT/mint",
            ValidMint("Private Bound NFT"), JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<UserNFT>(mintResponse))!.Result!;

        var holon = await SeedHolonAsync(h => h.WithName("PrivateBoundHolon"));
        await Client.PostAsJsonAsync($"/api/UserNFT/{nft.Id}/holons/{holon.Id}/bind",
            new HolonNFTBindingModel { Role = "owner", PermissionLevel = "full" }, JsonOptions);

        var otherUserId = Guid.Parse("c8888888-8888-8888-8888-888888888888");
        using var otherClient = Factory.CreateAuthenticatedClientForUser(otherUserId);

        var response = await otherClient.GetAsync($"/api/UserNFT/{nft.Id}/holons");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a foreign user must not be able to enumerate another user's holon bindings");
    }

    // ── Verify access ─────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyHolonAccessAsync_WithValidPermissions_ShouldReturnTrue()
    {
        var mintResponse = await Client.PostAsJsonAsync("/api/UserNFT/mint",
            ValidMint("AccessNFT"), JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<UserNFT>(mintResponse))!.Result!;

        var holon = await SeedHolonAsync(h => h.WithName("AccessHolon"));
        await Client.PostAsJsonAsync($"/api/UserNFT/{nft.Id}/holons/{holon.Id}/bind",
            new HolonNFTBindingModel
            {
                Role        = "owner",
                Permissions = new Dictionary<string, string> { { "execute", "true" } }
            }, JsonOptions);

        var verificationRequest = new
        {
            UserNFTId        = nft.Id,
            HolonId            = holon.Id,
            RequiredPermission = "execute"
        };

        var response = await Client.PostAsJsonAsync("/api/UserNFT/verify-holon-access",
            verificationRequest, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<bool>(response);
        result!.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        result.Message.Should().Contain("verified");
    }

    // ── Composite ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUserNFTCompositeAsync_WithValidId_ShouldReturnComposite()
    {
        var mintResponse = await Client.PostAsJsonAsync("/api/UserNFT/mint",
            ValidMint("CompositeNFT"), JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<UserNFT>(mintResponse))!.Result!;

        var holon  = await SeedHolonAsync(h => h.WithName("CompositeHolon"));
        var walletAddr = "comp" + Guid.NewGuid().ToString("N")[..8];
        var wallet = await SeedWalletAsync(w => w.ForUser(Guid.Parse(TestAuthHandler.DefaultUserId))
                                                   .OnChain("Solana")
                                                   .WithAddress(walletAddr));

        await Client.PostAsJsonAsync($"/api/UserNFT/{nft.Id}/holons/{holon.Id}/bind",
            new HolonNFTBindingModel { Role = "owner", PermissionLevel = "full" }, JsonOptions);
        await Client.PostAsJsonAsync($"/api/UserNFT/{nft.Id}/wallets/{wallet.Id}/bind",
            new WalletNFTBindingModel { BindingType = "primary" }, JsonOptions);

        var response = await Client.GetAsync($"/api/UserNFT/{nft.Id}/composite");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<UserNFTCompositeResult>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.UserNFTId.Should().Be(nft.Id);
        result.Result.Name.Should().Be("CompositeNFT");
        result.Result.HolonBindings.Should().ContainSingle(b => b.HolonId == holon.Id);
        result.Result.WalletBindings.Should().ContainSingle(b => b.WalletId == wallet.Id);
    }

    // ── Transfer ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TransferUserNFTAsync_WithValidRequest_ShouldTransfer()
    {
        var transferableMint = ValidMint("Transferable NFT");
        transferableMint.IsSoulbound = false;
        transferableMint.IsTransferable = true;
        var mintResponse = await Client.PostAsJsonAsync("/api/UserNFT/mint", transferableMint, JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<UserNFT>(mintResponse))!.Result!;

        var transferRequest = new { RecipientAddress = "new_owner_address" };
        var response = await Client.PostAsJsonAsync($"/api/UserNFT/{nft.Id}/transfer",
            transferRequest, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<bool>(response);
        result!.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        result.Message.Should().Contain("transferred");
    }

    [Fact]
    public async Task TransferUserNFTAsync_WithSoulboundNFT_ShouldReturnError()
    {
        var soulboundMint = ValidMint("Soulbound NFT");
        soulboundMint.IsSoulbound = true;
        var mintResponse = await Client.PostAsJsonAsync("/api/UserNFT/mint", soulboundMint, JsonOptions);
        mintResponse.EnsureSuccessStatusCode();
        var nft = (await ReadResultAsync<UserNFT>(mintResponse))!.Result!;

        var transferRequest = new { RecipientAddress = "new_owner_address" };
        var response = await Client.PostAsJsonAsync($"/api/UserNFT/{nft.Id}/transfer",
            transferRequest, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Read the error body directly — ReadResultAsync ensures-success and would
        // throw on the (expected) 400 before we can assert on the payload.
        var result = await response.Content.ReadFromJsonAsync<AZOAResult<bool>>(JsonOptions);
        result!.IsError.Should().BeTrue();
        result.Message.Should().Contain("oulbound");
    }
}

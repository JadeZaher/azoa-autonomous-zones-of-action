using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Interfaces.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

/// <summary>NFT ownership contracts and durable HTTP mutations; see Controllers/AGENTS.md.</summary>
public partial class UserNFTControllerIntegrationTests : IntegrationTestBase, IClassFixture<NftFlowWebApplicationFactory>
{
    public UserNFTControllerIntegrationTests(NftFlowWebApplicationFactory factory) : base(factory) => _flow = factory;

    // ── Mint ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MintUserNFTAsync_WithValidRequest_ShouldReturnSuccess()
    {
        using var actor = await VerifiedActorAsync();
        var minted = await MintCompletedAsync(actor, "Test User NFT");
        minted.Nft.ChainType.Should().Be("Algorand");
        minted.Nft.Name.Should().Be("Test User NFT");
        minted.Nft.UserId.Should().Be(actor.Id);
        var submissions = _flow.Nfts.SubmissionCount;
        using var replay = await SendKeyedAsync(actor.Client, "api/UserNFT/mint", minted.Draft, minted.Key);
        var repeated = await ReadPayloadAsync<NftLifecycleView>(replay);
        repeated.OperationId.Should().Be(minted.View.OperationId);
        repeated.NftId.Should().Be(minted.View.NftId);
        repeated.State.Should().Be("Completed");
        _flow.Nfts.SubmissionCount.Should().Be(submissions, "a replay cannot submit another chain effect");
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
        var nft = await SeedMintedAsync("Retrievable NFT");
        var response = await Client.GetAsync($"/api/UserNFT/{nft.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<UserNFT>(response);
        result!.Result!.Id.Should().Be(nft.Id);
        result.Result.Name.Should().Be("Retrievable NFT");
    }

    // ── Get by user ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUserNFTsByUserAsync_WithValidUserId_ShouldReturnNFTs()
    {
        // Mint two NFTs for the default test user (TestAuthHandler.DefaultUserId).
        var userId = Guid.Parse(TestAuthHandler.DefaultUserId);

        await SeedMintedAsync("NFT 1");
        await SeedMintedAsync("NFT 2");

        var response = await Client.GetAsync($"/api/UserNFT/user/{userId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<UserNFT>>(response);
        result!.Result.Should().HaveCountGreaterOrEqualTo(2);
    }

    // ── Holon binding ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BindHolonToUserNFTAsync_WithValidIds_ShouldCreateBinding()
    {
        var nft = await SeedMintedAsync("Bindable NFT");

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
        var nft = await SeedMintedAsync("Bound NFT");

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
        var attackerNft = await SeedMintedAsync("Attacker NFT");

        // Victim (a different user) owns the holon under attack.
        var victimUserId = Guid.Parse("c4444444-4444-4444-4444-444444444444");
        await EnsureFixtureUserAsync(victimUserId);
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
        var attackerNft = await SeedMintedAsync("Attacker Wallet NFT");

        var victimUserId = Guid.Parse("c5555555-5555-5555-5555-555555555555");
        var walletAddr = "fixturevictim" + Guid.NewGuid().ToString("N")[..8];
        var victimWallet = await SeedStoredWalletAsync(victimUserId, walletAddr);

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
        var nft = await SeedMintedAsync("Private NFT");

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
        var nft = await SeedMintedAsync("Private Bound NFT");

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
        var nft = await SeedMintedAsync("AccessNFT");

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
        var nft = await SeedMintedAsync("CompositeNFT");

        var holon  = await SeedHolonAsync(h => h.WithName("CompositeHolon"));
        var walletAddr = "fixturecomp" + Guid.NewGuid().ToString("N")[..8];
        var wallet = await SeedStoredWalletAsync(Guid.Parse(TestAuthHandler.DefaultUserId), walletAddr);

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
        using var actor = await VerifiedActorAsync();
        using var recipient = await VerifiedActorAsync();
        var minted = await MintCompletedAsync(actor, "Transferable NFT");
        var fee = await FeeAsync(actor.Client, "Transfer", minted.Nft.Id);
        var key = "transfer-" + Guid.NewGuid().ToString("N");
        var submissions = _flow.Nfts.SubmissionCount;
        using var response = await SendKeyedAsync(actor.Client, $"api/UserNFT/{minted.Nft.Id}/transfer",
            new { recipientAddress = recipient.Wallet.Address, feeAuthorization = fee }, key);
        var completed = await CompleteAsync(recipient.Client, await ReadPayloadAsync<NftLifecycleView>(response));
        completed.State.Should().Be("Completed");
        using var received = await recipient.Client.GetAsync($"api/UserNFT/{minted.Nft.Id}");
        var nft = await ReadPayloadAsync<UserNFT>(received);
        nft.UserId.Should().Be(recipient.Id);
        nft.CurrentOwner.Should().Be(recipient.Wallet.Address);
        _flow.Nfts.SubmissionCount.Should().Be(submissions + 2, "the recipient acceptance and transfer are distinct persisted effects");
        using var replay = await SendKeyedAsync(actor.Client, $"api/UserNFT/{minted.Nft.Id}/transfer",
            new { recipientAddress = recipient.Wallet.Address, feeAuthorization = fee }, key);
        (await ReadPayloadAsync<NftLifecycleView>(replay)).OperationId.Should().Be(completed.OperationId);
        _flow.Nfts.SubmissionCount.Should().Be(submissions + 2);
        using var previousOwner = await actor.Client.GetAsync($"api/UserNFT/{minted.Nft.Id}");
        previousOwner.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TransferUserNFTAsync_WithSoulboundNFT_ShouldReturnError()
    {
        using var actor = await VerifiedActorAsync();
        var minted = await MintCompletedAsync(actor, "Soulbound NFT", soulbound: true);
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<INftLifecycleStore>();
        var before = await store.GetAuthorityAsync(minted.Nft.Id.ToString("N"));
        before.Should().NotBeNull();
        before!.Soulbound.Should().BeTrue();
        var submissions = _flow.Nfts.SubmissionCount;
        using var response = await SendKeyedAsync(actor.Client, $"api/UserNFT/{minted.Nft.Id}/transfer",
            new { recipientAddress = "fixtureunclaimed", feeAuthorization = await FeeAsync(actor.Client, "Transfer", minted.Nft.Id) },
            "soulbound-" + Guid.NewGuid().ToString("N"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await response.Content.ReadFromJsonAsync<AZOAResult<NftLifecycleView>>(JsonOptions);
        result!.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.OperationNotAllowed);
        result.Message.Should().Be("The NFT is not transferable by this caller.");
        minted.Nft.IsSoulbound.Should().BeTrue();
        _flow.Nfts.SubmissionCount.Should().Be(submissions);
        JsonSerializer.Serialize(await store.GetAuthorityAsync(minted.Nft.Id.ToString("N"))).Should().Be(JsonSerializer.Serialize(before));
    }
}

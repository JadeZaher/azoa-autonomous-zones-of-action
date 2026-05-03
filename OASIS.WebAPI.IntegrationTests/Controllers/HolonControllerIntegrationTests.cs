using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OASIS.WebAPI.Controllers;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.IntegrationTests.Builders;
using OASIS.WebAPI.IntegrationTests.Factories;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.IntegrationTests.Controllers;

public class HolonControllerIntegrationTests : IntegrationTestBase
{
    public HolonControllerIntegrationTests(OASISTestWebApplicationFactory factory) : base(factory) { }

    // ═══════════════════════════════════════════════════════════════
    // CRUD
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_ShouldReturnHolonWithAvatarId()
    {
        var model = new HolonBuilder().WithName("TestHolon").BuildCreateModel();

        var response = await Client.PostAsJsonAsync("api/holon", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Holon>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be("TestHolon");
    }

    [Fact]
    public async Task Get_ExistingHolon_ShouldReturnHolon()
    {
        var holon = await SeedHolonAsync();

        var response = await Client.GetAsync($"api/holon/{holon.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Holon>(response);
        result!.Result!.Id.Should().Be(holon.Id);
    }

    [Fact]
    public async Task Get_NonExisting_ShouldReturnNotFound()
    {
        var response = await Client.GetAsync($"api/holon/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Query_WithFilters_ShouldReturnFilteredResults()
    {
        await SeedHolonAsync(h => h.WithName("Alpha").ForAvatar(Guid.NewGuid()));
        await SeedHolonAsync(h => h.WithName("Beta").ForAvatar(Guid.NewGuid()));

        var response = await Client.GetAsync("api/holon?Name=Alpha");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<Holon>>(response);
        result!.Result.Should().ContainSingle(h => h.Name == "Alpha");
    }

    [Fact]
    public async Task Update_ShouldModifyHolon()
    {
        var holon = await SeedHolonAsync();
        var update = new HolonUpdateModel { Name = "UpdatedName" };

        var response = await Client.PutAsJsonAsync($"api/holon/{holon.Id}", update);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Holon>(response);
        result!.Result!.Name.Should().Be("UpdatedName");
    }

    [Fact]
    public async Task Delete_ShouldRemoveHolon()
    {
        var holon = await SeedHolonAsync();

        var response = await Client.DeleteAsync($"api/holon/{holon.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_NonExisting_ShouldReturnNotFound()
    {
        var response = await Client.DeleteAsync($"api/holon/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════════
    // INTERACT
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Interact_ShouldAddPeersAndMetadata()
    {
        var holon = await SeedHolonAsync();
        var request = new HolonInteractionRequest
        {
            AddPeerHolonIds = new List<Guid> { Guid.NewGuid() },
            SetMetadata = new Dictionary<string, string> { ["color"] = "blue" }
        };

        var response = await Client.PostAsJsonAsync($"api/holon/{holon.Id}/interact", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Holon>(response);
        result!.Result!.PeerHolonIds.Should().HaveCount(1);
        result.Result.Metadata.Should().ContainKey("color");
    }

    [Fact]
    public async Task Interact_ShouldRemovePeersAndMetadata()
    {
        var peerId = Guid.NewGuid();
        var holon = await SeedHolonAsync(h => h.WithPeers(peerId).WithMetadata("temp", "val"));
        var request = new HolonInteractionRequest
        {
            RemovePeerHolonIds = new List<Guid> { peerId },
            RemoveMetadataKeys = new List<string> { "temp" }
        };

        var response = await Client.PostAsJsonAsync($"api/holon/{holon.Id}/interact", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Holon>(response);
        result!.Result!.PeerHolonIds.Should().BeEmpty();
        result.Result.Metadata.Should().NotContainKey("temp");
    }

    // ═══════════════════════════════════════════════════════════════
    // BLOCKCHAIN
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Mint_ShouldCreateBlockchainOperation()
    {
        var holon = await SeedHolonAsync();
        var wallet = await SeedWalletAsync(w => w.ForAvatar(Guid.Parse(TestAuthHandler.DefaultAvatarId)));
        var request = new MintRequest
        {
            WalletId = wallet.Id,
            TokenUri = "ipfs://test",
            Amount = 1,
            AssetType = "NFT"
        };

        var response = await Client.PostAsJsonAsync($"api/holon/{holon.Id}/mint", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<BlockchainOperation>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.OperationType.Should().Be("Mint");
    }

    [Fact]
    public async Task Exchange_ShouldCreateBlockchainOperation()
    {
        var holon = await SeedHolonAsync();
        var target = await SeedHolonAsync();
        var wallet = await SeedWalletAsync(w => w.ForAvatar(Guid.Parse(TestAuthHandler.DefaultAvatarId)));
        var request = new ExchangeRequest
        {
            WalletId = wallet.Id,
            TargetHolonId = target.Id,
            ExchangeRate = "1:1"
        };

        var response = await Client.PostAsJsonAsync($"api/holon/{holon.Id}/exchange", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<BlockchainOperation>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.OperationType.Should().Be("Exchange");
    }

    // ═══════════════════════════════════════════════════════════════
    // AUTH
    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════
    // HOLARCHY TRAVERSAL
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetChildren_ShouldReturnSubHolons()
    {
        var parent = await SeedHolonAsync();
        var child1 = await SeedHolonAsync(h => h.WithName("Child1").WithParent(parent.Id));
        var child2 = await SeedHolonAsync(h => h.WithName("Child2").WithParent(parent.Id));
        await SeedHolonAsync(h => h.WithName("Orphan"));

        var response = await Client.GetAsync($"api/holon/{parent.Id}/children");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<Holon>>(response);
        result!.Result.Should().HaveCount(2);
        result.Result.Select(h => h.Name).Should().Contain("Child1", "Child2");
    }

    [Fact]
    public async Task GetPeers_ShouldReturnLinkedPeers()
    {
        var peer = await SeedHolonAsync(h => h.WithName("Peer"));
        var holon = await SeedHolonAsync(h => h.WithPeers(peer.Id));

        var response = await Client.GetAsync($"api/holon/{holon.Id}/peers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<Holon>>(response);
        result!.Result.Should().ContainSingle(h => h.Id == peer.Id);
    }

    [Fact]
    public async Task GetAncestors_ShouldWalkParentChain()
    {
        var grandparent = await SeedHolonAsync(h => h.WithName("Grandparent"));
        var parent = await SeedHolonAsync(h => h.WithName("Parent").WithParent(grandparent.Id));
        var child = await SeedHolonAsync(h => h.WithName("Child").WithParent(parent.Id));

        var response = await Client.GetAsync($"api/holon/{child.Id}/ancestors");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<Holon>>(response);
        result!.Result.Should().HaveCount(2);
        result.Result.Select(h => h.Name).Should().ContainInOrder("Parent", "Grandparent");
    }

    [Fact]
    public async Task GetDescendants_ShouldTraverseSubtree()
    {
        var root = await SeedHolonAsync(h => h.WithName("Root"));
        var child = await SeedHolonAsync(h => h.WithName("Child").WithParent(root.Id));
        await SeedHolonAsync(h => h.WithName("Grandchild").WithParent(child.Id));

        var response = await Client.GetAsync($"api/holon/{root.Id}/descendants");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<Holon>>(response);
        result!.Result.Should().HaveCount(2);
        result.Result.Select(h => h.Name).Should().Contain("Child", "Grandchild");
    }

    [Fact]
    public async Task GetDescendants_CycleGuard_ShouldNotInfiniteLoop()
    {
        var a = await SeedHolonAsync(h => h.WithName("A"));
        var b = await SeedHolonAsync(h => h.WithName("B").WithParent(a.Id));
        // Manually create cycle: A's parent = B (impossible via normal API, simulating bad data)
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
            var holonA = db.Holons.Find(a.Id)!;
            holonA.ParentHolonId = b.Id;
            await db.SaveChangesAsync();
        }

        var response = await Client.GetAsync($"api/holon/{a.Id}/descendants");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<Holon>>(response);
        result!.IsError.Should().BeFalse();
        // Only B should be returned; cycle prevents re-adding A
        result.Result.Should().ContainSingle(h => h.Name == "B");
    }

    // ═══════════════════════════════════════════════════════════════
    // HOLONIC FUNCTIONALITY
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Propagate_ShouldDeactivateSubtree()
    {
        var root = await SeedHolonAsync(h => h.WithName("Root"));
        var child = await SeedHolonAsync(h => h.WithName("Child").WithParent(root.Id));
        var grandchild = await SeedHolonAsync(h => h.WithName("Grandchild").WithParent(child.Id));

        var request = new HolonPropagateRequest { Property = "IsActive", Value = false, IncludeSelf = true };
        var response = await Client.PostAsJsonAsync($"api/holon/{root.Id}/propagate", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<int>(response);
        result!.IsError.Should().BeFalse();
        result.Result.Should().Be(3);

        // Verify all are inactive
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
        db.Holons.Find(root.Id)!.IsActive.Should().BeFalse();
        db.Holons.Find(child.Id)!.IsActive.Should().BeFalse();
        db.Holons.Find(grandchild.Id)!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Compose_ShouldReturnSubtreeStats()
    {
        var root = await SeedHolonAsync(h => h.WithName("Album").AsAsset("Collection"));
        var child = await SeedHolonAsync(h => h.WithName("Track1").AsAsset("NFT").OnChain("algo").WithParent(root.Id));
        await SeedHolonAsync(h => h.WithName("Stem1").AsAsset("Audio").OnChain("algo").WithParent(child.Id));

        var response = await Client.GetAsync($"api/holon/{root.Id}/compose");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<HolonComposition>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.SourceHolonName.Should().Be("Album");
        result.Result.ChildCount.Should().Be(1);
        result.Result.TotalDescendantCount.Should().Be(2);
        result.Result.Depth.Should().Be(2);
        result.Result.AssetTypes.Should().Contain("NFT", "Audio");
        result.Result.ChainIds.Should().ContainSingle("algo");
        result.Result.AllActive.Should().BeTrue();
    }

    [Fact]
    public async Task Clone_ShouldCreateCopy()
    {
        var original = await SeedHolonAsync(h => h.WithName("Original"));
        var request = new HolonCloneRequest { IncludeSubtree = false };

        var response = await Client.PostAsJsonAsync($"api/holon/{original.Id}/clone", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Holon>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be("Original (Copy)");
        result.Result.Id.Should().NotBe(original.Id);
        result.Result.Metadata.Should().ContainKey("cloned_from");
    }

    [Fact]
    public async Task Clone_WithSubtree_ShouldCloneEntireTree()
    {
        var original = await SeedHolonAsync(h => h.WithName("Original"));
        var child = await SeedHolonAsync(h => h.WithName("Child").WithParent(original.Id));
        var request = new HolonCloneRequest { IncludeSubtree = true };

        var response = await Client.PostAsJsonAsync($"api/holon/{original.Id}/clone", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Holon>(response);
        result!.IsError.Should().BeFalse();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
        var clones = db.Holons.Where(h => h.Metadata.ContainsKey("cloned_from")).ToList();
        clones.Should().HaveCount(2); // original + child cloned
    }

    [Fact]
    public async Task MoveSubtree_ShouldChangeParent()
    {
        var root = await SeedHolonAsync(h => h.WithName("Root"));
        var child = await SeedHolonAsync(h => h.WithName("Child").WithParent(root.Id));
        var newParent = await SeedHolonAsync(h => h.WithName("NewParent"));

        var request = new MoveSubtreeRequest { NewParentId = newParent.Id };
        var response = await Client.PostAsJsonAsync($"api/holon/{child.Id}/move", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<bool>(response);
        result!.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
        db.Holons.Find(child.Id)!.ParentHolonId.Should().Be(newParent.Id);
    }

    [Fact]
    public async Task MoveSubtree_CyclePrevention_ShouldReturnError()
    {
        var a = await SeedHolonAsync(h => h.WithName("A"));
        var b = await SeedHolonAsync(h => h.WithName("B").WithParent(a.Id));

        var request = new MoveSubtreeRequest { NewParentId = b.Id };
        var response = await Client.PostAsJsonAsync($"api/holon/{a.Id}/move", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await response.Content.ReadFromJsonAsync<OASISResult<bool>>(JsonOptions);
        result!.IsError.Should().BeTrue();
        result.Message.Should().Contain("Cannot move");
    }

    // ═══════════════════════════════════════════════════════════════
    // AUTH
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Get_WithoutAuth_ShouldReturnUnauthorized()
    {
        var unauthClient = Factory.CreateClient();
        var response = await unauthClient.GetAsync($"api/holon/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

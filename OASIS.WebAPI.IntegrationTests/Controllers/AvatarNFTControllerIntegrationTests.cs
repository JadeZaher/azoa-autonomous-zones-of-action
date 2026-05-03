using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Models;
using Xunit;
using System.Net.Http;
using System.Text.Json;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.IntegrationTests.Controllers;

public class AvatarNFTControllerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AvatarNFTControllerIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove existing DbContext registration
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<OASISDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                // Add in-memory database for testing
                services.AddDbContext<OASISDbContext>(options =>
                    options.UseInMemoryDatabase("OASISTestDb"));
            });
        });

        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task MintAvatarNFTAsync_WithValidRequest_ShouldReturnSuccess()
    {
        // Arrange
        var avatar = new Avatar
        {
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hashed_password",
            FirstName = "Test",
            LastName = "User"
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
            context.Avatars.Add(avatar);
            await context.SaveChangesAsync();
        }

        var mintModel = new AvatarNFTMintModel
        {
            ChainType = "Solana",
            NFTContractAddress = "11111111111111111111111111111111",
            TokenStandard = "ERC721",
            MetadataURI = "https://api.example.com/metadata/123",
            Name = "Test Avatar NFT",
            Description = "Integration test NFT",
            IsSoulbound = false,
            IsTransferable = true,
            Attributes = new Dictionary<string, string>
            {
                { "level", "1" },
                { "karma", "100" }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/AvatarNFT/mint")
        {
            Content = new StringContent(JsonSerializer.Serialize(mintModel), System.Text.Encoding.UTF8, "application/json")
        };

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OASISResult<IAvatarNFT>>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.NotNull(result.Result);
        Assert.Equal(avatar.Id, result.Result.AvatarId);
        Assert.Equal("Solana", result.Result.ChainType);
        Assert.Equal("Test Avatar NFT", result.Result.Name);
    }

    [Fact]
    public async Task GetAvatarNFTAsync_WithValidId_ShouldReturnNFT()
    {
        // Arrange
        var avatar = new Avatar { Username = "testuser", Email = "test@example.com", PasswordHash = "hashed" };
        var nft = new AvatarNFT
        {
            AvatarId = avatar.Id,
            ChainType = "Solana",
            NFTContractAddress = "11111111111111111111111111111111",
            TokenId = "123",
            Name = "Test NFT",
            MetadataURI = "https://api.example.com/metadata/123"
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
            context.Avatars.Add(avatar);
            context.AvatarNFTs.Add(nft);
            await context.SaveChangesAsync();
        }

        // Act
        var response = await _client.GetAsync($"/api/AvatarNFT/{nft.Id}");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OASISResult<IAvatarNFT>>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.NotNull(result.Result);
        Assert.Equal(nft.Id, result.Result.Id);
        Assert.Equal("Test NFT", result.Result.Name);
    }

    [Fact]
    public async Task GetAvatarNFTAsync_WithInvalidId_ShouldReturnNotFound()
    {
        // Arrange
        var invalidId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/AvatarNFT/{invalidId}");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAvatarNFTsByAvatarAsync_WithValidAvatarId_ShouldReturnNFTs()
    {
        // Arrange
        var avatar = new Avatar { Username = "testuser", Email = "test@example.com", PasswordHash = "hashed" };
        var nft1 = new AvatarNFT { AvatarId = avatar.Id, ChainType = "Solana", Name = "NFT 1" };
        var nft2 = new AvatarNFT { AvatarId = avatar.Id, ChainType = "Solana", Name = "NFT 2" };

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
            context.Avatars.Add(avatar);
            context.AvatarNFTs.Add(nft1);
            context.AvatarNFTs.Add(nft2);
            await context.SaveChangesAsync();
        }

        // Act
        var response = await _client.GetAsync($"/api/AvatarNFT/avatar/{avatar.Id}");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OASISResult<IEnumerable<IAvatarNFT>>>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.NotNull(result.Result);
        Assert.Equal(2, result.Result.Count());
    }

    [Fact]
    public async Task BindHolonToAvatarNFTAsync_WithValidIds_ShouldCreateBinding()
    {
        // Arrange
        var avatar = new Avatar { Username = "testuser", Email = "test@example.com", PasswordHash = "hashed" };
        var holon = new Holon { Name = "Test Holon", AvatarId = avatar.Id, ProviderName = "TestProvider" };
        var nft = new AvatarNFT { AvatarId = avatar.Id, ChainType = "Solana", Name = "Test NFT" };

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
            context.Avatars.Add(avatar);
            context.Holons.Add(holon);
            context.AvatarNFTs.Add(nft);
            await context.SaveChangesAsync();
        }

        var bindingModel = new HolonNFTBindingModel
        {
            Role = "owner",
            PermissionLevel = "full",
            Permissions = new Dictionary<string, string>
            {
                { "read", "true" },
                { "write", "true" },
                { "execute", "true" }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/AvatarNFT/{nft.Id}/holons/{holon.Id}/bind")
        {
            Content = new StringContent(JsonSerializer.Serialize(bindingModel), System.Text.Encoding.UTF8, "application/json")
        };

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OASISResult<IHolonNFTBinding>>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.NotNull(result.Result);
        Assert.Equal(holon.Id, result.Result.HolonId);
        Assert.Equal(nft.Id, result.Result.AvatarNFTId);
        Assert.Equal("owner", result.Result.Role);
    }

    [Fact]
    public async Task GetHolonBindingsAsync_WithValidAvatarNFTId_ShouldReturnBindings()
    {
        // Arrange
        var avatar = new Avatar { Username = "testuser", Email = "test@example.com", PasswordHash = "hashed" };
        var holon = new Holon { Name = "Test Holon", AvatarId = avatar.Id, ProviderName = "TestProvider" };
        var nft = new AvatarNFT { AvatarId = avatar.Id, ChainType = "Solana", Name = "Test NFT" };
        var binding = new HolonNFTBinding
        {
            HolonId = holon.Id,
            AvatarNFTId = nft.Id,
            Role = "owner",
            PermissionLevel = "full",
            Permissions = new Dictionary<string, string> { { "read", "true" } }
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
            context.Avatars.Add(avatar);
            context.Holons.Add(holon);
            context.AvatarNFTs.Add(nft);
            context.HolonNFTBindings.Add(binding);
            await context.SaveChangesAsync();
        }

        // Act
        var response = await _client.GetAsync($"/api/AvatarNFT/{nft.Id}/holons");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OASISResult<IEnumerable<IHolonNFTBinding>>>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.NotNull(result.Result);
        Assert.Single(result.Result);
        Assert.Equal(holon.Id, result.Result.First().HolonId);
        Assert.Equal("owner", result.Result.First().Role);
    }

    [Fact]
    public async Task VerifyHolonAccessAsync_WithValidPermissions_ShouldReturnTrue()
    {
        // Arrange
        var avatar = new Avatar { Username = "testuser", Email = "test@example.com", PasswordHash = "hashed" };
        var holon = new Holon { Name = "Test Holon", AvatarId = avatar.Id, ProviderName = "TestProvider" };
        var nft = new AvatarNFT { AvatarId = avatar.Id, ChainType = "Solana", Name = "Test NFT" };
        var binding = new HolonNFTBinding
        {
            HolonId = holon.Id,
            AvatarNFTId = nft.Id,
            Role = "owner",
            Permissions = new Dictionary<string, string> { { "execute", "true" } }
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
            context.Avatars.Add(avatar);
            context.Holons.Add(holon);
            context.AvatarNFTs.Add(nft);
            context.HolonNFTBindings.Add(binding);
            await context.SaveChangesAsync();
        }

        var verificationRequest = new
        {
            AvatarNFTId = nft.Id,
            HolonId = holon.Id,
            RequiredPermission = "execute"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/AvatarNFT/verify-holon-access")
        {
            Content = new StringContent(JsonSerializer.Serialize(verificationRequest), System.Text.Encoding.UTF8, "application/json")
        };

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OASISResult<bool>>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.True(result.Result);
        Assert.Contains("verified", result.Message);
    }

    [Fact]
    public async Task GetAvatarNFTCompositeAsync_WithValidId_ShouldReturnComposite()
    {
        // Arrange
        var avatar = new Avatar { Username = "testuser", Email = "test@example.com", PasswordHash = "hashed" };
        var holon = new Holon { Name = "Test Holon", AvatarId = avatar.Id, ProviderName = "TestProvider" };
        var wallet = new Wallet { AvatarId = avatar.Id, ChainType = "Solana", Address = "test_wallet_address" };
        var nft = new AvatarNFT
        {
            AvatarId = avatar.Id,
            ChainType = "Solana",
            Name = "Test NFT",
            NFTContractAddress = "11111111111111111111111111111111",
            TokenId = "123"
        };
        var holonBinding = new HolonNFTBinding
        {
            HolonId = holon.Id,
            AvatarNFTId = nft.Id,
            Role = "owner",
            Permissions = new Dictionary<string, string> { { "read", "true" } }
        };
        var walletBinding = new WalletNFTBinding
        {
            WalletId = wallet.Id,
            AvatarNFTId = nft.Id,
            BindingType = "primary",
            AccessPermissions = new Dictionary<string, string> { { "sign", "true" } }
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
            context.Avatars.Add(avatar);
            context.Holons.Add(holon);
            context.Wallets.Add(wallet);
            context.AvatarNFTs.Add(nft);
            context.HolonNFTBindings.Add(holonBinding);
            context.WalletNFTBindings.Add(walletBinding);
            await context.SaveChangesAsync();
        }

        // Act
        var response = await _client.GetAsync($"/api/AvatarNFT/{nft.Id}/composite");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OASISResult<AvatarNFTCompositeResult>>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.NotNull(result.Result);
        Assert.Equal(nft.Id, result.Result.AvatarNFTId);
        Assert.Equal(avatar.Id, result.Result.AvatarId);
        Assert.Equal("Test NFT", result.Result.Name);
        Assert.Single(result.Result.HolonBindings);
        Assert.Single(result.Result.WalletBindings);
        Assert.Equal(holon.Id, result.Result.HolonBindings.First().HolonId);
        Assert.Equal(wallet.Id, result.Result.WalletBindings.First().WalletId);
    }

    [Fact]
    public async Task TransferAvatarNFTAsync_WithValidRequest_ShouldTransfer()
    {
        // Arrange
        var avatar = new Avatar { Username = "testuser", Email = "test@example.com", PasswordHash = "hashed" };
        var nft = new AvatarNFT
        {
            AvatarId = avatar.Id,
            ChainType = "Solana",
            Name = "Transferable NFT",
            IsSoulbound = false,
            IsTransferable = true,
            CurrentOwner = "original_owner"
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
            context.Avatars.Add(avatar);
            context.AvatarNFTs.Add(nft);
            await context.SaveChangesAsync();
        }

        var transferRequest = new { RecipientAddress = "new_owner_address" };

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/AvatarNFT/{nft.Id}/transfer")
        {
            Content = new StringContent(JsonSerializer.Serialize(transferRequest), System.Text.Encoding.UTF8, "application/json")
        };

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OASISResult<bool>>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.True(result.Result);
        Assert.Contains("transferred successfully", result.Message);
    }

    [Fact]
    public async Task TransferAvatarNFTAsync_WithSoulboundNFT_ShouldReturnError()
    {
        // Arrange
        var avatar = new Avatar { Username = "testuser", Email = "test@example.com", PasswordHash = "hashed" };
        var soulboundNFT = new AvatarNFT
        {
            AvatarId = avatar.Id,
            ChainType = "Solana",
            Name = "Soulbound NFT",
            IsSoulbound = true
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
            context.Avatars.Add(avatar);
            context.AvatarNFTs.Add(soulboundNFT);
            await context.SaveChangesAsync();
        }

        var transferRequest = new { RecipientAddress = "new_owner_address" };

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/AvatarNFT/{soulboundNFT.Id}/transfer")
        {
            Content = new StringContent(JsonSerializer.Serialize(transferRequest), System.Text.Encoding.UTF8, "application/json")
        };

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OASISResult<bool>>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("Cannot transfer soulbound NFT", result.Message);
    }
}
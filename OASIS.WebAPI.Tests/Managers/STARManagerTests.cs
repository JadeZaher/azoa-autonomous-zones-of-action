using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests.Managers;

public class STARManagerTests
{
    private readonly Mock<IOASISStorageProvider> _provider;
    private readonly ProviderContext _providerContext;
    private readonly STARManager _manager;

    public STARManagerTests()
    {
        _provider = new Mock<IOASISStorageProvider>();
        _provider.Setup(p => p.ProviderName).Returns("InMemory");

        var config = new ConfigurationBuilder().Build();
        _providerContext = new ProviderContext(new[] { _provider.Object }, config, null);
        _manager = new STARManager(_providerContext);
    }

    [Fact]
    public async Task GetAsync_Existing_ReturnsODK()
    {
        var odk = new STARODK { Id = Guid.NewGuid(), Name = "Test" };
        _provider.Setup(p => p.LoadSTARODKAsync(odk.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<ISTARODK> { Result = odk });

        var result = await _manager.GetAsync(odk.Id);

        result.Result.Should().NotBeNull();
        result.Result!.Name.Should().Be("Test");
    }

    [Fact]
    public async Task GetAsync_NonExisting_ReturnsError()
    {
        _provider.Setup(p => p.LoadSTARODKAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<ISTARODK> { IsError = true, Message = "Not found" });

        var result = await _manager.GetAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task GetAllAsync_ReturnsList()
    {
        _provider.Setup(p => p.LoadAllSTARODKsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<ISTARODK>>
                 {
                     Result = new[] { new STARODK { Name = "A" }, new STARODK { Name = "B" } }
                 });

        var result = await _manager.GetAllAsync();

        result.Result.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_New_ShouldCreate()
    {
        _provider.Setup(p => p.LoadAllSTARODKsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<ISTARODK>> { Result = Array.Empty<ISTARODK>() });
        _provider.Setup(p => p.SaveSTARODKAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((ISTARODK s, CancellationToken _) => new OASISResult<ISTARODK> { Result = s });

        var model = new STARODKCreateModel { Name = "Genesis", Description = "Desc" };
        var result = await _manager.CreateOrUpdateAsync(model);

        result.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be("Genesis");
        result.Result.Description.Should().Be("Desc");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_Existing_ShouldUpdate()
    {
        var existing = new STARODK { Id = Guid.NewGuid(), Name = "Existing", Description = "Old" };
        _provider.Setup(p => p.LoadAllSTARODKsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<ISTARODK>> { Result = new[] { existing } });
        _provider.Setup(p => p.SaveSTARODKAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((ISTARODK s, CancellationToken _) => new OASISResult<ISTARODK> { Result = s });

        var model = new STARODKCreateModel { Name = "Existing", Description = "New" };
        var result = await _manager.CreateOrUpdateAsync(model);

        result.IsError.Should().BeFalse();
        result.Result!.Description.Should().Be("New");
    }

    [Fact]
    public async Task DeleteAsync_ShouldDelegateToProvider()
    {
        _provider.Setup(p => p.DeleteSTARODKAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<bool> { Result = true });

        var result = await _manager.DeleteAsync(Guid.NewGuid());

        result.Result.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_ShouldSetGeneratedCode()
    {
        var odk = new STARODK { Id = Guid.NewGuid(), Name = "Test" };
        _provider.Setup(p => p.LoadSTARODKAsync(odk.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<ISTARODK> { Result = odk });
        _provider.Setup(p => p.SaveSTARODKAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((ISTARODK s, CancellationToken _) => new OASISResult<ISTARODK> { Result = s });

        var request = new STARDappGenerationRequest { TargetChain = "Solana", BoundHolonIds = new List<Guid> { Guid.NewGuid() } };
        var result = await _manager.GenerateAsync(odk.Id, request);

        result.IsError.Should().BeFalse();
        result.Result!.GeneratedCode.Should().NotBeNullOrEmpty();
        result.Result.TargetChain.Should().Be("Solana");
    }

    [Fact]
    public async Task GenerateAsync_NonExisting_ShouldReturnError()
    {
        _provider.Setup(p => p.LoadSTARODKAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<ISTARODK> { IsError = true, Message = "Not found" });

        var result = await _manager.GenerateAsync(Guid.NewGuid(), new STARDappGenerationRequest());

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task DeployAsync_ShouldSetDeploymentConfig()
    {
        var odk = new STARODK { Id = Guid.NewGuid(), Name = "Test", GeneratedCode = "code", TargetChain = "Algorand" };
        _provider.Setup(p => p.LoadSTARODKAsync(odk.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<ISTARODK> { Result = odk });
        _provider.Setup(p => p.SaveSTARODKAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((ISTARODK s, CancellationToken _) => new OASISResult<ISTARODK> { Result = s });

        var result = await _manager.DeployAsync(odk.Id);

        result.IsError.Should().BeFalse();
        result.Result!.DeploymentConfig.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DeployAsync_WithoutGeneration_ShouldReturnError()
    {
        var odk = new STARODK { Id = Guid.NewGuid(), Name = "Test" };
        _provider.Setup(p => p.LoadSTARODKAsync(odk.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<ISTARODK> { Result = odk });

        var result = await _manager.DeployAsync(odk.Id);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("must be generated");
    }

    [Fact]
    public async Task DeployAsync_NonExisting_ShouldReturnError()
    {
        _provider.Setup(p => p.LoadSTARODKAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<ISTARODK> { IsError = true, Message = "Not found" });

        var result = await _manager.DeployAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
    }
}

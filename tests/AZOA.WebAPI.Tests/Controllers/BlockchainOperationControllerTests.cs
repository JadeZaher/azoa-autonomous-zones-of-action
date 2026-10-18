using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Controllers;

public class BlockchainOperationControllerTests
{
    private static readonly Guid AuthenticatedUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly Mock<IBlockchainOperationManager> _manager;
    private readonly BlockchainOperationController _controller;

    public BlockchainOperationControllerTests()
    {
        _manager = new Mock<IBlockchainOperationManager>();
        var clientSigned = new AZOA.WebAPI.Services.Blockchain.ClientSigning.ClientSignedOperationService(
            Mock.Of<AZOA.WebAPI.Interfaces.Stores.IBlockchainOperationStore>(),
            Mock.Of<AZOA.WebAPI.Services.Blockchain.ClientSigning.IClientSignedOperationStore>(),
            Mock.Of<AZOA.WebAPI.Providers.Blockchain.IBlockchainProviderFactory>(), Mock.Of<IIdempotencyStore>(),
            Microsoft.Extensions.Options.Options.Create(new AZOA.WebAPI.Services.Reconciliation.ReconciliationOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AZOA.WebAPI.Services.Blockchain.ClientSigning.ClientSignedOperationService>.Instance,
            Mock.Of<AZOA.WebAPI.Interfaces.IRealValueKycGate>(MockBehavior.Strict));
        _controller = new BlockchainOperationController(_manager.Object, clientSigned, Mock.Of<IReconciliationService>());
        // Inject a ClaimsPrincipal so GetUserIdFromClaims() resolves.
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, AuthenticatedUserId.ToString())
                }, "TestScheme"))
            }
        };
    }

    [Fact]
    public async Task Get_Existing_ReturnsOk()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IBlockchainOperation> { Result = new BlockchainOperation() });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Get_NonExisting_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _manager.Setup(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IBlockchainOperation> { IsError = true, Result = null });

        var result = await _controller.Get(id, null);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Get_Existing_ProjectsOnlyTheSafeOperationAllowlist()
    {
        var id = Guid.NewGuid();
        var operation = new BlockchainOperation
        {
            Id = id,
            UserId = AuthenticatedUserId,
            InitiatorApiKeyId = Guid.NewGuid(),
            IdempotencyKey = new string('a', 64),
            Parameters = new Dictionary<string, string>
            {
                ["ChainType"] = "Algorand",
                ["ChainNetwork"] = "Devnet",
                ["TxHash"] = "public-transaction",
                ["IdempotencyKey"] = "alloc:private-api-key:payment-intent",
                ["IdempotencyResultPayload"] = "{\"secret\":\"internal\"}",
                ["Error"] = "provider-only detail",
            },
        };
        _manager.Setup(m => m.GetAsync(id, It.IsAny<Guid?>(), It.IsAny<AZOARequest?>()))
            .ReturnsAsync(AZOAResult<IBlockchainOperation>.Success(operation));

        var action = await _controller.Get(id, null);

        var payload = ((OkObjectResult)action.Result!).Value;
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        json.Should().Contain("public-transaction")
            .And.NotContain("alloc:private-api-key")
            .And.NotContain("IdempotencyResultPayload")
            .And.NotContain("provider-only detail")
            .And.NotContain("InitiatorApiKeyId");
    }

    [Fact]
    public async Task GetByUser_ReturnsOk()
    {
        var userId = AuthenticatedUserId;
        _manager.Setup(m => m.GetByUserAsync(userId, It.IsAny<AZOARequest?>()))
                .ReturnsAsync(new AZOAResult<IEnumerable<IBlockchainOperation>> { Result = Array.Empty<IBlockchainOperation>() });

        var result = await _controller.GetByUser(userId, null);

        result.Result.Should().BeOfType<OkObjectResult>();
    }
}

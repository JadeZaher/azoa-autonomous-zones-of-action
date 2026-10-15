using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class ActionControllerOutputProjectionTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task ExecuteNode_ProjectsLegacyOperationOutputWithoutLeakingInternalMetadata()
    {
        var actionId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var operation = new BlockchainOperation
        {
            Id = operationId,
            UserId = UserId,
            WalletId = Guid.NewGuid(),
            OperationType = "Mint",
            Status = "Completed",
            IdempotencyKey = "safe-correlation",
            InitiatorUserId = Guid.NewGuid(),
            InitiatorApiKeyId = Guid.NewGuid(),
            Parameters = new Dictionary<string, string>
            {
                ["TxHash"] = "public-tx-reference",
                ["IdempotencyKey"] = "alloc:private-key:payment-intent",
                ["IdempotencyResultPayload"] = "{\"provider\":\"private\"}",
            },
        };
        var rawOutput = JsonSerializer.Serialize(
            AZOAResult<IBlockchainOperation>.Success(operation),
            ActionNodeJson.Options);
        var manager = new Mock<IActionManager>();
        manager.Setup(m => m.ExecuteNodeAsync(
                actionId, nodeId, UserId, It.IsAny<AZOARequest?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(AZOAResult<ActionNodeExecution>.Success(new ActionNodeExecution
            {
                Id = Guid.NewGuid(),
                RunId = Guid.NewGuid(),
                NodeId = nodeId,
                State = ActionNodeState.Succeeded,
                Output = rawOutput,
            }));
        var controller = CreateController(manager.Object);

        var action = await controller.ExecuteNode(actionId, nodeId, null);

        var payload = ((OkObjectResult)action.Result!).Value
            .Should().BeOfType<AZOAResult<ActionNodeExecutionResponse>>().Subject;
        var output = payload.Result!.Output!;

        output.Should().Contain("public-tx-reference")
            .And.NotContain("alloc:private-key")
            .And.NotContain("IdempotencyResultPayload")
            .And.NotContain("InitiatorApiKeyId")
            .And.NotContain("safe-correlation");
        using var outputDoc = JsonDocument.Parse(output);
        outputDoc.RootElement.GetProperty("Result").GetProperty("Id").GetGuid()
            .Should().Be(operationId, "supported downstream bindings retain the operation id");
        outputDoc.RootElement.GetProperty("Result").TryGetProperty("Parameters", out _)
            .Should().BeFalse();
    }

    [Fact]
    public async Task GetExecutionState_ProjectsLegacyFungibleOutputWithoutIdempotencyKey()
    {
        var runId = Guid.NewGuid();
        var rawOutput = $$"""
            {
              "IsError": false,
              "Message": "Fungible token created.",
              "Result": {
                "UserId": "{{UserId}}",
                "WalletId": "{{Guid.NewGuid()}}",
                "WalletAddress": "ALGO-PUBLIC",
                "WalletProvisioned": true,
                "AssetId": "12345",
                "IdempotencyKey": "fungible:private-api-key:purchase-42",
                "Replayed": false
              }
            }
            """;
        var manager = new Mock<IActionManager>();
        manager.Setup(m => m.GetExecutionStateAsync(runId, UserId, It.IsAny<AZOARequest?>()))
            .ReturnsAsync(AZOAResult<ActionExecutionState>.Success(new ActionExecutionState
            {
                RunId = runId,
                ActionId = Guid.NewGuid(),
                NodeExecutions =
                [
                    new ActionNodeExecution
                    {
                        Id = Guid.NewGuid(),
                        RunId = runId,
                        NodeId = Guid.NewGuid(),
                        State = ActionNodeState.Succeeded,
                        Output = rawOutput,
                    },
                ],
            }));
        var controller = CreateController(manager.Object);

        var action = await controller.GetExecutionState(runId, null);

        var payload = ((OkObjectResult)action.Result!).Value
            .Should().BeOfType<AZOAResult<ActionExecutionStateResponse>>().Subject;
        var output = payload.Result!.NodeExecutions.Single().Output!;

        output.Should().Contain("12345")
            .And.NotContain("fungible:private-api-key")
            .And.NotContain("IdempotencyKey");
    }

    [Fact]
    public async Task GetExecutionState_RedactsIdempotencyKeyFromUnknownLegacyOutput()
    {
        var runId = Guid.NewGuid();
        var rawOutput = """
            {
              "Result": {
                "TransactionHash": "public-bridge-reference",
                "IdempotencyKey": "bridge:private-api-key:transfer-42"
              }
            }
            """;
        var manager = new Mock<IActionManager>();
        manager.Setup(m => m.GetExecutionStateAsync(runId, UserId, It.IsAny<AZOARequest?>()))
            .ReturnsAsync(AZOAResult<ActionExecutionState>.Success(new ActionExecutionState
            {
                RunId = runId,
                ActionId = Guid.NewGuid(),
                NodeExecutions =
                [
                    new ActionNodeExecution
                    {
                        Id = Guid.NewGuid(),
                        RunId = runId,
                        NodeId = Guid.NewGuid(),
                        State = ActionNodeState.Succeeded,
                        Output = rawOutput,
                    },
                ],
            }));
        var controller = CreateController(manager.Object);

        var action = await controller.GetExecutionState(runId, null);

        var payload = ((OkObjectResult)action.Result!).Value
            .Should().BeOfType<AZOAResult<ActionExecutionStateResponse>>().Subject;
        var output = payload.Result!.NodeExecutions.Single().Output!;

        output.Should().Contain("public-bridge-reference")
            .And.NotContain("bridge:private-api-key")
            .And.NotContain("IdempotencyKey");
    }

    private static ActionController CreateController(IActionManager manager)
    {
        var controller = new ActionController(manager);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, UserId.ToString())],
                    "TestScheme")),
            },
        };
        return controller;
    }
}

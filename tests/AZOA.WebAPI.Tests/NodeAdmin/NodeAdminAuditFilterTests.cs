using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.NodeAdmin;

/// <summary>Audit outages preserve reads and refuse mutations before their action runs.</summary>
public sealed class NodeAdminAuditFilterTests
{
    [Theory]
    [InlineData("GET", "api/admin/fiat-payouts", true)]
    [InlineData("POST", "api/search/operator", true)]
    [InlineData("POST", "api/admin/fiat-events/{id}/replay", false)]
    public async Task Audit_outages_preserve_read_availability_but_fail_closed_before_a_write(string method, string template, bool read)
    {
        var executor = new Mock<ISurrealExecutor>(MockBehavior.Strict);
        executor.Setup(candidate => candidate.QuerySingleAsync<NodeAdminAuditHead>(It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("audit unavailable"));
        var logger = new Mock<ILogger<NodeAdminAuditActionFilter>>();
        var audit = new NodeAdminAuditLog(executor.Object, NullLogger<NodeAdminAuditLog>.Instance);
        var filter = new NodeAdminAuditActionFilter(audit, logger.Object);
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0,
            new EndpointMetadataCollection(new AuthorizeAttribute("Operator")), template));
        http.Items[NodeAdminCaller.ItemKey] = new NodeAdminCaller("admin:" + Guid.NewGuid(), null, null, new string('a', 64),
            DateTimeOffset.UtcNow, ["ed25519", "totp"], NodeAdminPermissions.All.ToHashSet(StringComparer.Ordinal));
        var descriptor = new ControllerActionDescriptor { AttributeRouteInfo = new AttributeRouteInfo { Template = template } };
        var action = new ActionContext(http, new RouteData(), descriptor);
        var context = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), new object());
        var executed = 0;

        await filter.OnActionExecutionAsync(context, () =>
        {
            executed++;
            return Task.FromResult(new ActionExecutedContext(action, [], context.Controller) { Result = new OkObjectResult("available") });
        });

        executed.Should().Be(read ? 1 : 0);
        if (read)
        {
            context.Result.Should().BeNull("the audit outage must leave the read response available");
            logger.Invocations.Should().Contain(invocation => invocation.Method.Name == "Log"
                && (LogLevel)invocation.Arguments[0] == LogLevel.Critical
                && invocation.Arguments[2].ToString()!.Contains("NodeAdminReadAuditUnavailable", StringComparison.Ordinal));
        }
        else
        {
            context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
            ((ObjectResult)context.Result!).Value.Should().BeOfType<AZOAResult<object>>().Which.Code.Should().Be(NodeAdminErrorCodes.AuditUnavailable);
        }
    }
}

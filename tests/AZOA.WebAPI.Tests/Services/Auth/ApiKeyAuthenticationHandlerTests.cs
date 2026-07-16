using System.Text.Encodings.Web;
using AZOA.WebAPI.Services.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AZOA.WebAPI.Tests.Services.Auth;

public sealed class ApiKeyAuthenticationHandlerTests
{
    [Fact]
    public void CredentialFreeEndpoint_SelectsNoOpSchemeRegardlessOfHeaders()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "untrusted-rotating-value";
        context.Request.Headers.Authorization = "Bearer untrusted-token";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new CredentialFreePublicEndpointAttribute()),
            "credential-free"));

        AuthenticationSchemeSelector.Resolve(context).Should()
            .Be(CredentialFreeAuthenticationHandler.SchemeName);
    }

    [Fact]
    public async Task CredentialFreeEndpoint_IgnoresApiKeyBeforeOpeningStoreScope()
    {
        var options = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Setup(monitor => monitor.Get(ApiKeyAuthenticationHandler.SchemeName))
            .Returns(new AuthenticationSchemeOptions());
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var handler = new ApiKeyAuthenticationHandler(
            options.Object,
            loggerFactory,
            UrlEncoder.Default,
            scopeFactory.Object);
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        context.Request.Headers["X-Api-Key"] = "untrusted-rotating-value";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new CredentialFreePublicEndpointAttribute()),
            "credential-free"));
        await handler.InitializeAsync(
            new AuthenticationScheme(
                ApiKeyAuthenticationHandler.SchemeName,
                ApiKeyAuthenticationHandler.SchemeName,
                typeof(ApiKeyAuthenticationHandler)),
            context);

        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
        scopeFactory.Verify(factory => factory.CreateScope(), Times.Never);
    }
}

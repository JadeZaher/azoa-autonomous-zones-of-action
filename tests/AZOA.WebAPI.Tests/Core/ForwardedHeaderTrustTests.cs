using System.Net;
using AZOA.WebAPI.Core.Networking;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.Tests.Core;

public sealed class ForwardedHeaderTrustTests
{
    [Fact]
    public void DisabledByDefault_DoesNotConsumeSpoofableHeaders()
    {
        var configuration = Build(new Dictionary<string, string?>());

        ForwardedHeaderTrust.Build(configuration).Should().BeNull();
    }

    [Fact]
    public void TrustAll_WithoutEdgeOnlyAcknowledgement_FailsStartup()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:TrustAll"] = "true",
        });

        var act = () => ForwardedHeaderTrust.Build(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*EdgeOnlyDeploymentAcknowledged=true*");
    }

    [Fact]
    public void ExplicitProxyAndNetwork_AreTheOnlyTrustedForwarders()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.10",
            ["ForwardedHeaders:KnownNetworks:0"] = "192.168.50.0/24",
        });

        var options = ForwardedHeaderTrust.Build(configuration)!;

        options.KnownProxies.Should().Equal(IPAddress.Parse("10.0.0.10"));
        options.KnownIPNetworks.Should().ContainSingle(network =>
            network.Contains(IPAddress.Parse("192.168.50.42")));
        options.KnownIPNetworks.Should().NotContain(network =>
            network.Contains(IPAddress.Parse("203.0.113.42")));
    }

    [Fact]
    public void EnabledWithoutAnyTrustAnchor_FailsStartup()
    {
        var configuration = Build(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
        });

        var act = () => ForwardedHeaderTrust.Build(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no trusted proxy/network*");
    }

    [Fact]
    public async Task UntrustedRemote_CannotRewriteClientAddress()
    {
        var options = ForwardedHeaderTrust.Build(Build(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.10",
        }))!;
        var context = ForwardedContext("203.0.113.9", "198.51.100.7", "https");

        await Pipeline(options)(context);

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("203.0.113.9"));
    }

    [Fact]
    public async Task TrustedProxy_RewritesOnlyTheConfiguredForwardLimit()
    {
        var options = ForwardedHeaderTrust.Build(Build(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:ForwardLimit"] = "1",
            ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.10",
        }))!;
        var context = ForwardedContext(
            "10.0.0.10",
            "198.51.100.7, 192.0.2.44",
            "http, https");

        await Pipeline(options)(context);

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("192.0.2.44"));
        context.Request.Headers["X-Forwarded-For"].ToString()
            .Should().Be("198.51.100.7");
    }

    private static IConfiguration Build(IReadOnlyDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static DefaultHttpContext ForwardedContext(
        string remoteIp,
        string forwardedFor,
        string forwardedProto)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        return context;
    }

    private static RequestDelegate Pipeline(
        Microsoft.AspNetCore.Builder.ForwardedHeadersOptions options)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseForwardedHeaders(options);
        app.Run(_ => Task.CompletedTask);
        return app.Build();
    }
}

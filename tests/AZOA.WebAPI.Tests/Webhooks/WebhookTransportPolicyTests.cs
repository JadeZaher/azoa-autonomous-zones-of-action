using System.Net;
using AZOA.WebAPI.Core.Webhooks;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Webhooks;

public sealed class WebhookTransportPolicyTests
{
    [Theory]
    [InlineData("ff02::1")]
    [InlineData("fec0::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:2::1")]
    [InlineData("2002:a00:1::1")]
    [InlineData("3fff::1")]
    [InlineData("100::1")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("2606:4700::1111%4")]
    public void Nonpublic_or_transition_destinations_are_refused(string address)
        => WebhookSsrfGuard.IsBlockedIp(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("64:ff9b::808:808")]
    public void Public_unicast_and_public_well_known_translation_are_allowed(string address)
        => WebhookSsrfGuard.IsBlockedIp(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("https://user:secret@example.com/hook")]
    [InlineData("https://example.com/hook#fragment")]
    public void Url_credentials_and_fragments_are_refused_before_dns(string url)
    {
        var guard = new WebhookSsrfGuard();
        guard.UseResolver(_ => throw new InvalidOperationException("DNS must not run."));
        guard.IsAllowed(url, out _).Should().BeFalse();
    }

    [Fact]
    public void Mixed_public_and_multicast_dns_is_refused()
    {
        var guard = new WebhookSsrfGuard();
        guard.UseResolver(_ => [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("ff02::1")]);
        guard.IsAllowed("https://example.com/hook", out _).Should().BeFalse();
    }

    [Fact]
    public void Handler_has_no_ambient_proxy_credentials_cookies_or_redirects()
    {
        using var handler = WebhookHttpTransport.CreateHandler();
        handler.UseProxy.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
        handler.AllowAutoRedirect.Should().BeFalse();
        handler.Credentials.Should().BeNull();
        handler.PreAuthenticate.Should().BeFalse();
        handler.MaxResponseHeadersLength.Should().Be(32);
        handler.ConnectCallback.Should().NotBeNull();
        handler.SslOptions.RemoteCertificateValidationCallback.Should().BeNull();
    }
}

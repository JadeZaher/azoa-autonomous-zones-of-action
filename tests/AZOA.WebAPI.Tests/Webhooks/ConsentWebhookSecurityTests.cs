using System.Net;
using FluentAssertions;
using AZOA.WebAPI.Core.Webhooks;

namespace AZOA.WebAPI.Tests.Webhooks;

/// <summary>
/// tenant-consent-delegation AC7/H5: the webhook security primitives — the SSRF
/// guard (https-only + public-IP allowlist, DNS-rebinding defence) and the
/// replay-resistant timestamped HMAC.
/// </summary>
public class ConsentWebhookSecurityTests
{
    // ── SSRF guard: IP classifier ─────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1")]      // loopback
    [InlineData("10.1.2.3")]       // RFC1918
    [InlineData("172.16.0.1")]     // RFC1918
    [InlineData("172.31.255.255")] // RFC1918 upper
    [InlineData("192.168.1.1")]    // RFC1918
    [InlineData("169.254.169.254")]// cloud metadata
    [InlineData("0.0.0.0")]        // unspecified
    public void IsBlockedIp_BlocksPrivateAndMetadataRanges(string ip)
        => WebhookSsrfGuard.IsBlockedIp(IPAddress.Parse(ip)).Should().BeTrue();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")] // example.com — a genuinely routable public address
    public void IsBlockedIp_AllowsPublicRanges(string ip)
        => WebhookSsrfGuard.IsBlockedIp(IPAddress.Parse(ip)).Should().BeFalse();

    [Theory]
    [InlineData("::1")]            // v6 loopback
    [InlineData("fc00::1")]        // ULA
    [InlineData("fe80::1")]        // link-local
    [InlineData("::ffff:10.0.0.1")]// v4-mapped private
    public void IsBlockedIp_BlocksV6PrivateAndMappedPrivate(string ip)
        => WebhookSsrfGuard.IsBlockedIp(IPAddress.Parse(ip)).Should().BeTrue();

    // ── SSRF guard: URL policy ─────────────────────────────────────────────────

    [Fact]
    public void IsAllowed_RejectsNonHttps()
    {
        var guard = new WebhookSsrfGuard();
        guard.UseResolver(_ => new[] { IPAddress.Parse("8.8.8.8") });
        guard.IsAllowed("http://example.com/hook", out _).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_RejectsHttpsResolvingToPrivate_DnsRebind()
    {
        var guard = new WebhookSsrfGuard();
        // A public name that resolves (in part) to a private address must be rejected.
        guard.UseResolver(_ => new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.5") });
        guard.IsAllowed("https://evil.example.com/hook", out var reason).Should().BeFalse();
        reason.Should().Contain("blocked");
    }

    [Fact]
    public void IsAllowed_AllowsHttpsResolvingToPublic()
    {
        var guard = new WebhookSsrfGuard();
        guard.UseResolver(_ => new[] { IPAddress.Parse("93.184.216.34") });
        guard.IsAllowed("https://tenant.example.com/hook", out _).Should().BeTrue();
    }

    // ── Timestamped HMAC (replay resistance) ──────────────────────────────────

    [Fact]
    public void Hmac_IncludesTimestamp_SoSameBodyDifferentTimeDiffers()
    {
        var signer = new WebhookHmacSigner();
        var body = "{\"eventType\":\"consent.revoked\"}";
        var sigA = signer.Sign(body, "2026-06-22T10:00:00.0000000Z", "tenant-secret");
        var sigB = signer.Sign(body, "2026-06-22T11:00:00.0000000Z", "tenant-secret");

        // The signed timestamp is part of the MAC — a captured event cannot be
        // replayed at a different time with the same signature.
        sigA.Should().NotBe(sigB);
    }

    [Fact]
    public void Hmac_DifferentSecret_DiffersPerTenant()
    {
        var signer = new WebhookHmacSigner();
        var body = "{\"x\":1}";
        var ts = "2026-06-22T10:00:00.0000000Z";
        signer.Sign(body, ts, "tenant-A-secret").Should().NotBe(signer.Sign(body, ts, "tenant-B-secret"));
    }

    [Fact]
    public void Hmac_IsDeterministic_ForSameInputs()
    {
        var signer = new WebhookHmacSigner();
        var body = "{\"x\":1}"; var ts = "2026-06-22T10:00:00.0000000Z";
        signer.Sign(body, ts, "s").Should().Be(signer.Sign(body, ts, "s"));
    }
}

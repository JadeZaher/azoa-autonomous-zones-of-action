using System.Net;
using FluentAssertions;
using AZOA.WebAPI.Core.Webhooks;

namespace AZOA.WebAPI.Tests.Webhooks;

/// <summary>
/// SSRF hardening (tenant-consent-delegation AC7/H5): the additional non-routable IPv4
/// ranges and NAT64 unwrap added to <see cref="WebhookSsrfGuard.IsBlockedIp"/>. Each case
/// names the SSRF target it denies. These ranges are NEW surface and do not overlap the
/// pre-existing ConsentWebhookSecurityTests coverage (which already pins loopback/RFC1918/
/// metadata and the public 203.0.113.10 — untouched here).
/// </summary>
public class WebhookSsrfRangeTests
{
    // ── CGNAT 100.64.0.0/10 (RFC 6598 shared address space) ───────────────────
    [Theory]
    [InlineData("100.64.0.1")]       // CGNAT lower bound — internal NAT pool target blocked
    [InlineData("100.127.255.255")]  // CGNAT upper bound — internal NAT pool target blocked
    public void Cgnat_IsBlocked(string ip)
        => WebhookSsrfGuard.IsBlockedIp(IPAddress.Parse(ip)).Should().BeTrue();

    [Theory]
    [InlineData("100.63.255.255")]   // just below 100.64/10 — public, NOT blocked (b[1]==63)
    [InlineData("100.128.0.0")]      // just above 100.64/10 — public, NOT blocked (b[1]==128)
    public void JustOutsideCgnat_IsNotBlocked(string ip)
        => WebhookSsrfGuard.IsBlockedIp(IPAddress.Parse(ip)).Should().BeFalse();

    // ── 198.18.0.0/15 benchmarking (RFC 2544) ─────────────────────────────────
    [Theory]
    [InlineData("198.18.0.1")]       // benchmarking range lower — non-routable target blocked
    [InlineData("198.19.255.255")]   // benchmarking range upper — non-routable target blocked
    public void Benchmarking_IsBlocked(string ip)
        => WebhookSsrfGuard.IsBlockedIp(IPAddress.Parse(ip)).Should().BeTrue();

    // ── 192.0.0.0/24 IETF protocol assignments (RFC 6890) ──────────────────────
    [Fact]
    public void IetfProtocolAssignments_IsBlocked()
        // 192.0.0.1 — IETF protocol assignment block, never a legitimate webhook target.
        => WebhookSsrfGuard.IsBlockedIp(IPAddress.Parse("192.0.0.1")).Should().BeTrue();

    // ── 224.0.0.0/4 multicast + 240.0.0.0/4 reserved/broadcast (b[0] >= 224) ───
    [Theory]
    [InlineData("224.0.0.1")]        // multicast — non-unicast target blocked
    [InlineData("240.0.0.1")]        // reserved — non-routable target blocked
    [InlineData("255.255.255.255")]  // limited broadcast — blocked
    public void MulticastReservedBroadcast_IsBlocked(string ip)
        => WebhookSsrfGuard.IsBlockedIp(IPAddress.Parse(ip)).Should().BeTrue();

    // ── NAT64 64:ff9b::/96 unwrap (RFC 6052) ───────────────────────────────────
    [Fact]
    public void Nat64EmbeddingPrivateV4_IsBlocked()
    {
        // ATTACK BLOCKED: a host resolving to 64:ff9b::10.0.0.1 embeds private 10.0.0.1
        // behind the NAT64 well-known prefix; the unwrap classifies the embedded v4 so it
        // cannot slip the v6 checks unclassified.
        WebhookSsrfGuard.IsBlockedIp(IPAddress.Parse("64:ff9b::a00:1")).Should().BeTrue();
    }
}

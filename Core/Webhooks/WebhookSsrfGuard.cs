// SPDX-License-Identifier: UNLICENSED

using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace AZOA.WebAPI.Core.Webhooks;

/// <summary>
/// SSRF guard for tenant-registered webhook URLs (tenant-consent-delegation §4, AC7 —
/// H5). A tenant controls the callback URL, so without this guard a registered callback
/// could point at an AZOA-internal service (the SurrealDB admin port, a cloud metadata
/// endpoint, a sidecar), turning the delivery worker into a confused-deputy SSRF probe.
///
/// <para><b>Policy: https-only + public-IP allowlist.</b> A URL is allowed ONLY when
/// it is <c>https</c> AND <b>every</b> address its host resolves to is a public,
/// routable IP. Any non-https scheme, or any resolved address in a private / loopback /
/// link-local / ULA / metadata range, rejects the URL.</para>
///
/// <para><b>DNS-rebinding defence.</b> The host is resolved via
/// <see cref="Dns.GetHostAddresses(string)"/> and the URL is rejected if <b>ANY</b>
/// resolved address is blocked — a public name that resolves (in whole or in part) to a
/// private address cannot slip through. The worker calls this immediately before each
/// POST (not only at registration time), so a rebind between register and deliver is
/// still caught.</para>
///
/// <para>This is an injectable instance (so the DNS resolution can be faked in tests),
/// but the IP-classification core is exposed as the pure, unit-testable static helper
/// <see cref="IsBlockedIp(IPAddress)"/>.</para>
///
/// <para><b>Connect-time re-validation (DNS-rebinding TOCTOU).</b> The instance
/// <see cref="IsAllowed"/> pre-flight runs its OWN DNS lookup just before the POST, but a
/// rebind can still race between that lookup and the socket's lookup. The
/// <see cref="CreateGuardedConnectCallback"/> factory closes that window: it is installed
/// on the <see cref="System.Net.Sockets.SocketsHttpHandler"/> primary handler so the
/// SOCKET'S OWN resolution is re-validated at connect time — the exact address the
/// connection will use is classified with <see cref="IsBlockedIp(IPAddress)"/>, and a
/// blocked address aborts the connection with an <see cref="IOException"/>. Redirect
/// following is disabled on that same handler so a 3xx to an internal address cannot
/// bypass the guard. The two layers are defence in depth: pre-flight is the
/// registration/pre-POST policy check, the connect callback is the un-spoofable
/// last line.</para>
/// </summary>
public sealed class WebhookSsrfGuard
{
    /// <summary>
    /// Validates a webhook URL against the https-only + public-IP-allowlist policy.
    /// Returns <c>true</c> only when the URL is safe to POST to; otherwise <c>false</c>
    /// with a human-readable <paramref name="reason"/> the caller logs / dead-letters
    /// with. NEVER throws — a malformed URL or a DNS failure is a rejection, not an
    /// exception.
    /// </summary>
    public bool IsAllowed(string url, out string reason)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            reason = "URL is empty.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            reason = "URL is not a well-formed absolute URI.";
            return false;
        }

        // https-only — a captured http callback could be MITM'd and an internal-scheme
        // (file://, etc.) is never a legitimate webhook target.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"URL scheme '{uri.Scheme}' is not https.";
            return false;
        }

        // Resolve the host to its address set. A literal-IP host short-circuits the DNS
        // lookup (GetHostAddresses returns it directly), so both "https://10.0.0.1/" and
        // "https://internal.name/" (rebinding to a private A record) are covered.
        IPAddress[] addresses;
        try
        {
            addresses = ResolveHost(uri.DnsSafeHost);
        }
        catch (Exception ex)
        {
            // A resolution failure is fail-closed: we do not POST to a host we cannot
            // classify.
            reason = $"Host '{uri.DnsSafeHost}' could not be resolved: {ex.Message}";
            return false;
        }

        if (addresses.Length == 0)
        {
            reason = $"Host '{uri.DnsSafeHost}' resolved to no addresses.";
            return false;
        }

        // Reject if ANY resolved address is blocked — defence against a public name that
        // resolves (in whole or in part) to a private/metadata address.
        foreach (var addr in addresses)
        {
            if (IsBlockedIp(addr))
            {
                reason = $"Host '{uri.DnsSafeHost}' resolves to blocked address {addr} " +
                         "(private / loopback / link-local / ULA / metadata range).";
                return false;
            }
        }

        reason = "Allowed.";
        return true;
    }

    /// <summary>
    /// DNS resolution seam. Virtual-by-delegate so tests can substitute a deterministic
    /// resolver; production resolves via <see cref="Dns.GetHostAddresses(string)"/>.
    /// </summary>
    private Func<string, IPAddress[]>? _resolver;

    /// <summary>
    /// Test seam: override how hosts resolve to addresses. Production leaves this null
    /// and uses <see cref="Dns.GetHostAddresses(string)"/>.
    /// </summary>
    public void UseResolver(Func<string, IPAddress[]> resolver) => _resolver = resolver;

    private IPAddress[] ResolveHost(string host)
        => (_resolver ?? Dns.GetHostAddresses)(host);

    /// <summary>
    /// Pure, unit-testable IP classifier. Returns <c>true</c> when <paramref name="ip"/>
    /// is in a range a webhook callback must NEVER reach:
    /// <list type="bullet">
    ///   <item>IPv4 loopback <c>127.0.0.0/8</c></item>
    ///   <item>IPv4 private <c>10.0.0.0/8</c>, <c>172.16.0.0/12</c>, <c>192.168.0.0/16</c></item>
    ///   <item>IPv4 link-local <c>169.254.0.0/16</c> (INCLUDES the cloud metadata
    ///         endpoint <c>169.254.169.254</c>)</item>
    ///   <item>IPv4 CGNAT <c>100.64.0.0/10</c> (RFC 6598)</item>
    ///   <item>IPv4 IETF protocol assignments <c>192.0.0.0/24</c> (RFC 6890)</item>
    ///   <item>IPv4 documentation TEST-NET-1/2/3 <c>192.0.2.0/24</c>,
    ///         <c>198.51.100.0/24</c>, <c>203.0.113.0/24</c></item>
    ///   <item>IPv4 benchmarking <c>198.18.0.0/15</c> (RFC 2544)</item>
    ///   <item>IPv4 multicast <c>224.0.0.0/4</c> + reserved/broadcast <c>240.0.0.0/4</c>
    ///         (everything <c>&gt;= 224.0.0.0</c>)</item>
    ///   <item>IPv6 loopback <c>::1</c></item>
    ///   <item>IPv6 ULA <c>fc00::/7</c></item>
    ///   <item>IPv6 link-local <c>fe80::/10</c></item>
    ///   <item>the unspecified address (<c>0.0.0.0</c> / <c>::</c>)</item>
    /// </list>
    /// An IPv4-mapped IPv6 address (<c>::ffff:a.b.c.d</c>) is unwrapped to its IPv4 form
    /// and classified there, so a mapped private address cannot evade the v4 checks. A
    /// NAT64 address (<c>64:ff9b::/96</c>) is similarly unwrapped to its embedded IPv4 and
    /// classified there, so <c>64:ff9b::a9fe:a9fe</c> (the metadata endpoint behind NAT64)
    /// cannot evade the v4 checks either.
    /// </summary>
    public static bool IsBlockedIp(IPAddress ip)
    {
        if (ip is null) return true;

        // Unwrap an IPv4-mapped IPv6 address so ::ffff:10.0.0.1 is classified as v4.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        // Unwrap a NAT64 address (64:ff9b::/96, RFC 6052) so the embedded IPv4 is
        // classified as v4 — otherwise 64:ff9b::169.254.169.254 would slip the v4 checks.
        if (TryUnwrapNat64(ip, out var embedded))
            ip = embedded;

        if (IPAddress.IsLoopback(ip)) return true; // 127.0.0.0/8 and ::1

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes(); // network order: b[0].b[1].b[2].b[3]

            // 0.0.0.0/8 — "this host" / unspecified.
            if (b[0] == 0) return true;

            // 127.0.0.0/8 — loopback (IsLoopback already covers, defensive).
            if (b[0] == 127) return true;

            // 10.0.0.0/8 — RFC1918.
            if (b[0] == 10) return true;

            // 100.64.0.0/10 — RFC 6598 carrier-grade NAT (shared address space).
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;

            // 172.16.0.0/12 — RFC1918 (172.16.0.0 – 172.31.255.255).
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;

            // 192.0.0.0/24 — RFC 6890 IETF protocol assignments.
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;

            // 192.0.2.0/24 — TEST-NET-1 (documentation).
            if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;

            // 192.168.0.0/16 — RFC1918.
            if (b[0] == 192 && b[1] == 168) return true;

            // 198.18.0.0/15 — RFC 2544 benchmarking (198.18.0.0 – 198.19.255.255).
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;

            // 198.51.100.0/24 — TEST-NET-2 (documentation).
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;

            // 169.254.0.0/16 — link-local, INCLUDES 169.254.169.254 cloud metadata.
            if (b[0] == 169 && b[1] == 254) return true;

            // 203.0.113.0/24 — TEST-NET-3 (documentation).
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;

            // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved/broadcast — everything
            // from 224.0.0.0 up is non-routable as a unicast webhook target.
            if (b[0] >= 224) return true;

            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;      // fe80::/10
            if (IPAddress.IPv6Any.Equals(ip)) return true; // ::

            var b = ip.GetAddressBytes(); // 16 bytes

            // fc00::/7 — Unique Local Addresses (first 7 bits == 1111 110).
            if ((b[0] & 0xFE) == 0xFC) return true;

            // fe80::/10 — link-local (defensive: IsIPv6LinkLocal already covers).
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;

            return false;
        }

        // Unknown address family — fail closed.
        return true;
    }

    /// <summary>
    /// Unwraps a NAT64 address (<c>64:ff9b::/96</c>, RFC 6052 well-known prefix) to the
    /// IPv4 address embedded in its low 32 bits. Returns <c>false</c> for any address that
    /// is not in the NAT64 well-known prefix (leaving <paramref name="embedded"/> null).
    /// This closes the gap where a host resolves to <c>64:ff9b::&lt;private-v4&gt;</c> and
    /// would otherwise pass the v6 checks unclassified.
    /// </summary>
    private static bool TryUnwrapNat64(IPAddress ip, out IPAddress embedded)
    {
        embedded = IPAddress.None;
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;

        var b = ip.GetAddressBytes(); // 16 bytes
        // Well-known prefix 64:ff9b::/96 — bytes 0..1 == 0x0064, bytes 2..11 == 0.
        if (b[0] != 0x00 || b[1] != 0x64 || b[2] != 0xFF || b[3] != 0x9B) return false;
        for (var i = 4; i < 12; i++)
            if (b[i] != 0x00) return false;

        embedded = new IPAddress(new[] { b[12], b[13], b[14], b[15] });
        return true;
    }

    /// <summary>
    /// Builds the <see cref="SocketsHttpHandler.ConnectCallback"/> that re-validates the
    /// connection's resolved address at SOCKET-CONNECT time — the close on the
    /// DNS-rebinding TOCTOU. The instance <see cref="IsAllowed"/> pre-flight classifies a
    /// host's addresses just before the POST, but the kernel re-resolves the name when the
    /// socket actually connects; a rebind in that gap could land on a private address. This
    /// callback resolves the endpoint host ITSELF and classifies the EXACT address it is
    /// about to connect to, so there is no later resolution to rebind.
    ///
    /// <para>Behaviour:
    /// <list type="bullet">
    ///   <item>If the endpoint host is already an IP literal, it is classified directly;
    ///         a blocked literal throws <see cref="IOException"/> (no connect).</item>
    ///   <item>Otherwise the host is resolved via <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>
    ///         and EVERY returned address is classified with <see cref="IsBlockedIp(IPAddress)"/>;
    ///         if ANY is blocked the whole connection is rejected with
    ///         <see cref="IOException"/> (a partially-private resolution cannot slip a
    ///         single allowed address through).</item>
    ///   <item>The first allowed address is connected to via a raw
    ///         <see cref="Socket"/>, and a <see cref="NetworkStream"/> over it is returned
    ///         (owning the socket so the stack disposes it).</item>
    /// </list></para>
    /// </summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>
        CreateGuardedConnectCallback()
    {
        return async (context, cancellationToken) =>
        {
            var dnsEndPoint = context.DnsEndPoint;
            var host = dnsEndPoint.Host;
            var port = dnsEndPoint.Port;

            IPAddress[] candidates;
            if (IPAddress.TryParse(host, out var literal))
            {
                // An IP-literal host: classify it directly — no DNS to rebind.
                candidates = new[] { literal };
            }
            else
            {
                // Resolve the host OURSELVES and validate EVERY address before connecting,
                // so the address we connect to is the address we classified.
                candidates = await Dns.GetHostAddressesAsync(host, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (candidates.Length == 0)
                throw new IOException(
                    $"Webhook SSRF guard: host '{host}' resolved to no addresses at connect time.");

            // Reject the connection if ANY resolved address is blocked — a public name
            // that resolves (in whole or in part) to a private/metadata address cannot
            // slip a single allowed address through.
            foreach (var addr in candidates)
            {
                if (IsBlockedIp(addr))
                    throw new IOException(
                        $"Webhook SSRF guard: host '{host}' resolved to blocked address {addr} " +
                        "at connect time (private / loopback / link-local / ULA / metadata / " +
                        "reserved range). Connection aborted — closes the DNS-rebinding TOCTOU.");
            }

            // Every candidate passed; connect to the first (already-validated) address.
            var target = new IPEndPoint(candidates[0], port);
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }
}

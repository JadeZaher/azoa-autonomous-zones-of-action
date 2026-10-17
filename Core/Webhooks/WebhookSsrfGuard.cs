// SPDX-License-Identifier: UNLICENSED

using System.Net;
using System.Net.Sockets;

namespace AZOA.WebAPI.Core.Webhooks;
public sealed class WebhookSsrfGuard
{
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

        // https-only â€” a captured http callback could be MITM'd and an internal-scheme
        // (file://, etc.) is never a legitimate webhook target.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"URL scheme '{uri.Scheme}' is not https.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || url.Length > 2048)
        {
            reason = "Webhook URLs cannot contain credentials or fragments and must fit 2048 characters.";
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
        catch (SocketException)
        {
            // A resolution failure is fail-closed: we do not POST to a host we cannot
            // classify.
            reason = "Webhook host could not be resolved.";
            return false;
        }

        if (addresses.Length == 0)
        {
            reason = $"Host '{uri.DnsSafeHost}' resolved to no addresses.";
            return false;
        }

        // Reject if ANY resolved address is blocked â€” defence against a public name that
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
    private Func<string, IPAddress[]>? _resolver;
    public void UseResolver(Func<string, IPAddress[]> resolver) => _resolver = resolver;

    private IPAddress[] ResolveHost(string host)
        => (_resolver ?? Dns.GetHostAddresses)(host);
    public static bool IsBlockedIp(IPAddress ip)
    {
        if (ip is null) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0) return true;

        // Unwrap an IPv4-mapped IPv6 address so ::ffff:10.0.0.1 is classified as v4.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        // Unwrap a NAT64 address (64:ff9b::/96, RFC 6052) so the embedded IPv4 is
        // classified as v4 â€” otherwise 64:ff9b::169.254.169.254 would slip the v4 checks.
        if (TryUnwrapNat64(ip, out var embedded))
            ip = embedded;

        if (IPAddress.IsLoopback(ip)) return true; // 127.0.0.0/8 and ::1

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes(); // network order: b[0].b[1].b[2].b[3]

            // 0.0.0.0/8 â€” "this host" / unspecified.
            if (b[0] == 0) return true;

            // 127.0.0.0/8 â€” loopback (IsLoopback already covers, defensive).
            if (b[0] == 127) return true;

            // 10.0.0.0/8 â€” RFC1918.
            if (b[0] == 10) return true;

            // 100.64.0.0/10 â€” RFC 6598 carrier-grade NAT (shared address space).
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;

            // 172.16.0.0/12 â€” RFC1918 (172.16.0.0 â€“ 172.31.255.255).
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;

            // 192.0.0.0/24 â€” RFC 6890 IETF protocol assignments.
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;

            // 192.0.2.0/24 â€” TEST-NET-1 (documentation).
            if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;

            // 192.168.0.0/16 â€” RFC1918.
            if (b[0] == 192 && b[1] == 168) return true;

            // 198.18.0.0/15 â€” RFC 2544 benchmarking (198.18.0.0 â€“ 198.19.255.255).
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;

            // 198.51.100.0/24 â€” TEST-NET-2 (documentation).
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;

            // 169.254.0.0/16 â€” link-local, INCLUDES 169.254.169.254 cloud metadata.
            if (b[0] == 169 && b[1] == 254) return true;

            // 203.0.113.0/24 â€” TEST-NET-3 (documentation).
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;

            // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved/broadcast â€” everything
            // from 224.0.0.0 up is non-routable as a unicast webhook target.
            if (b[0] >= 224) return true;

            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.ScopeId != 0 || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return true;
            if (ip.IsIPv6LinkLocal) return true;      // fe80::/10
            if (IPAddress.IPv6Any.Equals(ip)) return true; // ::

            var b = ip.GetAddressBytes(); // 16 bytes
            // Public unicast only; special-purpose and transition prefixes are excluded.
            if ((b[0] & 0xE0) != 0x20) return true;
            if (b[0] == 0x20 && b[1] == 0x01 && (b[2] & 0xFE) == 0) return true;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return true;
            if (b[0] == 0x20 && b[1] == 0x02) return true;
            if (b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0) return true;

            // fc00::/7 â€” Unique Local Addresses (first 7 bits == 1111 110).
            if ((b[0] & 0xFE) == 0xFC) return true;

            // fe80::/10 â€” link-local (defensive: IsIPv6LinkLocal already covers).
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;

            return false;
        }

        // Unknown address family â€” fail closed.
        return true;
    }
    private static bool TryUnwrapNat64(IPAddress ip, out IPAddress embedded)
    {
        embedded = IPAddress.None;
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;

        var b = ip.GetAddressBytes(); // 16 bytes
        // Well-known prefix 64:ff9b::/96 â€” bytes 0..1 == 0x0064, bytes 2..11 == 0.
        if (b[0] != 0x00 || b[1] != 0x64 || b[2] != 0xFF || b[3] != 0x9B) return false;
        for (var i = 4; i < 12; i++)
            if (b[i] != 0x00) return false;

        embedded = new IPAddress(new[] { b[12], b[13], b[14], b[15] });
        return true;
    }
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
                // An IP-literal host: classify it directly â€” no DNS to rebind.
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

            // Reject the connection if ANY resolved address is blocked â€” a public name
            // that resolves (in whole or in part) to a private/metadata address cannot
            // slip a single allowed address through.
            foreach (var addr in candidates)
            {
                if (IsBlockedIp(addr))
                    throw new IOException(
                        $"Webhook SSRF guard: host '{host}' resolved to blocked address {addr} " +
                        "at connect time (private / loopback / link-local / ULA / metadata / " +
                        "reserved range). Connection aborted â€” closes the DNS-rebinding TOCTOU.");
            }

            // Every candidate passed validation; try each in turn so a host with
            // both an unreachable IPv6 and a reachable IPv4 (or vice-versa) still
            // connects â€” restoring the multi-address fallback that pinning to
            // candidates[0] would have lost.
            Socket? socket = null;
            foreach (var addr in candidates)
            {
                socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(addr, port), cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (OperationCanceledException)
                {
                    // Caller cancellation is terminal â€” don't fall through to the
                    // next address or swallow it as a connect failure.
                    socket.Dispose();
                    throw;
                }
                catch (SocketException)
                {
                    socket.Dispose();
                    socket = null;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            throw new IOException(
                $"Webhook SSRF guard: failed to connect to any resolved address for host '{host}'.");
        };
    }
}

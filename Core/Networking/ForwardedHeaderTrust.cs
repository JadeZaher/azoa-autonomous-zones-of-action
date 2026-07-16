using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace AZOA.WebAPI.Core.Networking;

public sealed class ForwardedHeaderTrustOptions
{
    public bool Enabled { get; set; }

    public bool TrustAll { get; set; }

    public bool EdgeOnlyDeploymentAcknowledged { get; set; }

    public int ForwardLimit { get; set; } = 1;

    public IReadOnlyList<string> KnownProxies { get; set; } = [];

    public IReadOnlyList<string> KnownNetworks { get; set; } = [];
}

public static class ForwardedHeaderTrust
{
    public static ForwardedHeadersOptions? Build(IConfiguration configuration)
    {
        var trust = configuration
            .GetSection("ForwardedHeaders")
            .Get<ForwardedHeaderTrustOptions>() ?? new ForwardedHeaderTrustOptions();
        if (!trust.Enabled)
            return null;
        if (trust.ForwardLimit is < 1 or > 10)
            throw new InvalidOperationException("ForwardedHeaders:ForwardLimit must be between 1 and 10.");

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = trust.ForwardLimit,
            RequireHeaderSymmetry = true,
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        if (trust.TrustAll)
        {
            if (!trust.EdgeOnlyDeploymentAcknowledged)
            {
                throw new InvalidOperationException(
                    "ForwardedHeaders:TrustAll requires EdgeOnlyDeploymentAcknowledged=true. " +
                    "Otherwise configure KnownProxies/KnownNetworks or disable forwarded headers.");
            }

            return options;
        }

        foreach (var raw in trust.KnownProxies)
        {
            if (!IPAddress.TryParse(raw, out var proxy))
                throw new InvalidOperationException($"ForwardedHeaders:KnownProxies contains invalid IP '{raw}'.");
            options.KnownProxies.Add(proxy);
        }

        foreach (var raw in trust.KnownNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(raw, out var network))
                throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks contains invalid CIDR '{raw}'.");
            options.KnownIPNetworks.Add(network);
        }

        if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
        {
            throw new InvalidOperationException(
                "ForwardedHeaders is enabled but no trusted proxy/network is configured. " +
                "Configure KnownProxies/KnownNetworks or explicitly acknowledge an edge-only TrustAll deployment.");
        }

        return options;
    }
}

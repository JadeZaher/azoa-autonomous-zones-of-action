using System.Collections.Concurrent;
using System.Net;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>One outbound request the app sent to a third-party vendor.</summary>
public sealed record VendorRequest(HttpMethod Method, Uri Uri, string Body, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Path without the query string, e.g. <c>/begin</c>.</summary>
    public string Path => Uri.AbsolutePath;
}

/// <summary>
/// In-process stand-in for the internet edge: routes the app's outbound HTTP by host to a
/// registered responder, forwards only declared local infrastructure (the test SurrealDB) to the
/// real network, and refuses every other host. See <c>Http/AGENTS.md</c> §vendor-edge.
/// </summary>
public sealed class VendorEdge
{
    private readonly ConcurrentDictionary<string, Func<VendorRequest, HttpResponseMessage>> _responders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _passthroughAuthorities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<VendorRequest> _received = new();

    /// <summary>Every vendor request in arrival order (passthrough traffic is not recorded).</summary>
    public IReadOnlyList<VendorRequest> Received => _received.ToArray();

    /// <summary>Routes requests for <paramref name="host"/> to <paramref name="responder"/>.</summary>
    public void Register(string host, Func<VendorRequest, HttpResponseMessage> responder)
        => _responders[host] = responder;

    /// <summary>Lets requests to <paramref name="origin"/> (scheme-less authority match) reach the real network.</summary>
    public void AllowPassthrough(Uri origin) => _passthroughAuthorities[origin.Authority] = 0;

    /// <summary>A fresh primary handler for one <see cref="IHttpClientFactory"/> client.</summary>
    public HttpMessageHandler CreateHandler() => new EdgeHandler(this);

    /// <summary>JSON response helper shared by the vendor fakes.</summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private bool IsPassthrough(Uri uri) => _passthroughAuthorities.ContainsKey(uri.Authority);

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => string.Join(",", group.SelectMany(header => header.Value)),
                StringComparer.OrdinalIgnoreCase);
        var received = new VendorRequest(request.Method, request.RequestUri!, body, headers);
        _received.Enqueue(received);

        // Hermetic by construction: a host nobody registered is an accidental real egress.
        if (!_responders.TryGetValue(received.Uri.Host, out var responder))
            throw new HttpRequestException($"Vendor edge refused unregistered host '{received.Uri.Host}'.");
        return responder(received);
    }

    /// <summary>The real network sits behind it only for declared passthrough authorities.</summary>
    private sealed class EdgeHandler(VendorEdge edge) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => edge.IsPassthrough(request.RequestUri!) ? base.SendAsync(request, ct) : edge.RespondAsync(request, ct);
    }
}

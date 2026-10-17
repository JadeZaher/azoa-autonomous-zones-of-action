using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.Options;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttGuardianTransportTests
{
    private static readonly string Emitter = new string('a', 63) + "1";

    [Theory]
    [InlineData("found", true)]
    [InlineData("pending", false)]
    [InlineData("other-sequence", false)]
    [InlineData("other-body", false)]
    [InlineData("oversized", false)]
    [InlineData("server-error", false)]
    public async Task OnlyAnExactEnvelopeForTheRequestedEmitterSequenceIsReturned(string response, bool found)
    {
        var handler = new Endpoint(response);
        var transport = Transport(handler, "https://guardian.test");
        var fetched = await transport.FetchAsync(8, Emitter, "42", Body(42), CancellationToken.None);
        Assert.Equal(found, fetched.Found);
        Assert.Equal("/v1/signed_vaa/8/" + Emitter + "/42", handler.Paths.Single());
        if (found) Assert.Equal(Convert.ToBase64String(Envelope(42)), fetched.EnvelopeBase64);
        else Assert.Null(fetched.EnvelopeBase64);
    }

    [Theory]
    [InlineData("http://guardian.test", false)]
    [InlineData("https://user:secret@guardian.test", false)]
    [InlineData("http://127.0.0.1:7071", true)]
    public async Task OnlyHttpsOrNumericLoopbackEndpointsAreContacted(string endpoint, bool contacted)
    {
        var handler = new Endpoint("found");
        var fetched = await Transport(handler, endpoint).FetchAsync(8, Emitter, "42", Body(42), CancellationToken.None);
        Assert.Equal(contacted, handler.Paths.Count == 1);
        Assert.Equal(contacted, fetched.Found);
    }

    [Fact]
    public async Task NonCanonicalSequenceIsRefusedWithoutAnyRequest()
    {
        var handler = new Endpoint("found");
        var fetched = await Transport(handler, "https://guardian.test").FetchAsync(8, Emitter, "042", Body(42), CancellationToken.None);
        Assert.False(fetched.Found);
        Assert.Empty(handler.Paths);
    }

    private static WttGuardianTransport Transport(HttpMessageHandler handler, string endpoint)
        => new(new HttpClient(handler), Options.Create(new WttGuardianOptions { Endpoints = [endpoint], TimeoutSeconds = 5 }));

    private static string Body(ulong sequence) => Convert.ToHexStringLower(Envelope(sequence).AsSpan(72));

    private static byte[] Envelope(ulong sequence, byte payload = 0)
    {
        var envelope = new byte[6 + 66 + 51 + 1];
        envelope[0] = 1; envelope[5] = 1;
        var body = envelope.AsSpan(72);
        BinaryPrimitives.WriteUInt16BigEndian(body[8..], 8);
        Convert.FromHexString(Emitter).CopyTo(body[10..]);
        BinaryPrimitives.WriteUInt64BigEndian(body[42..], sequence);
        body[50] = 32;
        body[51] = payload;
        return envelope;
    }

    private sealed class Endpoint(string response) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            var json = (byte[] envelope) => new StringContent(JsonSerializer.Serialize(new { vaaBytes = Convert.ToBase64String(envelope) }),
                Encoding.UTF8, "application/json");
            return Task.FromResult(response switch
            {
                "found" => new HttpResponseMessage(HttpStatusCode.OK) { Content = json(Envelope(42)) },
                "other-sequence" => new HttpResponseMessage(HttpStatusCode.OK) { Content = json(Envelope(43)) },
                "other-body" => new HttpResponseMessage(HttpStatusCode.OK) { Content = json(Envelope(42, payload: 7)) },
                "oversized" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 300000)) },
                "server-error" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }
    }
}

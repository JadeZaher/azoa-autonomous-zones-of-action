using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Server-configured guardian HTTP endpoints; responses are untrusted envelopes until verified.</summary>
public sealed class WttGuardianOptions
{
    public const string SectionName = "Wtt:Guardians";
    public List<string> Endpoints { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>Found carries the exact signed envelope; Pending never means source failure or refund eligibility.</summary>
public sealed record WttGuardianFetch(bool Found, string? EnvelopeBase64, string Detail);

/// <summary>Fetches a VAA for one exact published body from configured endpoints only; see AGENTS.md §Guardian transport.</summary>
public sealed class WttGuardianTransport(HttpClient http, IOptions<WttGuardianOptions> options)
{
    private const int MaximumResponseBytes = 262144;

    public async Task<WttGuardianFetch> FetchAsync(ushort emitterChain, string emitterAddressHex, string sequence, string expectedBodyHex,
        CancellationToken ct)
    {
        if (emitterChain == 0 || !WttOperationIdentity.IsCanonicalId(emitterAddressHex) || !UInt64Text.TryParse(sequence, out var parsed)
            || parsed.ToString(CultureInfo.InvariantCulture) != sequence || string.IsNullOrEmpty(expectedBodyHex))
            return new(false, null, "A canonical emitter chain, 32-byte emitter, uint64 sequence and published body are required.");
        var endpoints = (options.Value.Endpoints ?? []).Select(Endpoint).Where(uri => uri is not null).ToArray();
        if (endpoints.Length == 0) return new(false, null, "No guardian endpoint is configured; the publication remains awaiting proof.");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 1, 60));
        var details = new List<string>();
        foreach (var endpoint in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            var uri = new Uri(endpoint!, "v1/signed_vaa/" + emitterChain.ToString(CultureInfo.InvariantCulture) + "/" + emitterAddressHex + "/" + sequence);
            using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timed.CancelAfter(timeout);
            try
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timed.Token);
                if (response.StatusCode == HttpStatusCode.NotFound) { details.Add(endpoint!.Host + ": not yet signed"); continue; }
                if (!response.IsSuccessStatusCode) { details.Add(endpoint!.Host + ": HTTP " + (int)response.StatusCode); continue; }
                if (response.Content.Headers.ContentLength > MaximumResponseBytes) { details.Add(endpoint!.Host + ": oversized response"); continue; }
                var envelope = ReadEnvelope(await ReadBoundedAsync(response.Content, timed.Token), emitterChain, emitterAddressHex, sequence,
                    expectedBodyHex);
                if (envelope is not null) return new(true, envelope, endpoint!.Host);
                details.Add(endpoint!.Host + ": malformed envelope or a body other than the published one");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { details.Add(endpoint!.Host + ": timed out"); }
            catch (HttpRequestException exception) { details.Add(endpoint!.Host + ": " + exception.Message); }
            catch (IOException exception) { details.Add(endpoint!.Host + ": " + exception.Message); }
        }
        return new(false, null, "Awaiting guardian signatures: " + string.Join("; ", details));
    }

    private static Uri? Endpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)) return null;
        var loopback = uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)
            && IPAddress.IsLoopback(address);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && loopback)) return null;
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes) throw new HttpRequestException("Guardian response exceeds its bound.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string? ReadEnvelope(byte[] body, ushort emitterChain, string emitterAddressHex, string sequence, string expectedBodyHex)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("vaaBytes", out var field) || field.ValueKind != JsonValueKind.String) return null;
            var encoded = field.GetString()!;
            var vaa = WttVaaCodec.ParseBase64(encoded);
            var canonical = Convert.ToBase64String(Convert.FromHexString(vaa.EnvelopeHex));
            return vaa.EmitterChain == emitterChain && vaa.EmitterAddress == emitterAddressHex && vaa.Sequence == sequence
                && vaa.BodyHex == expectedBodyHex ? canonical : null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }
    }
}

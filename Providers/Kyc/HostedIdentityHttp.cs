using System.Net;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Kyc;

/// <summary>Bounded no-redirect transport for authenticated hosted identity protocols.</summary>
public sealed class HostedIdentityHttp(IHttpClientFactory clients)
{
    public const string ClientName = "hosted-identity";
    public sealed record Response(HttpStatusCode Status, string Body, IReadOnlyDictionary<string, string> Headers);

    public async Task<AZOAResult<Response>> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.Content.Headers.ContentLength > 65536) return AZOAResult<Response>.Failure("Identity provider response exceeded the protocol limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[65537];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (read == 0) break;
                length += read;
            }
            if (length > 65536) return AZOAResult<Response>.Failure("Identity provider response exceeded the protocol limit.");
            var body = new System.Text.UTF8Encoding(false, true).GetString(buffer, 0, length);
            var headers = response.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            return AZOAResult<Response>.Success(new(response.StatusCode, body, headers));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return AZOAResult<Response>.Failure("Identity provider request timed out; its outcome is unknown."); }
        catch (HttpRequestException) { return AZOAResult<Response>.Failure("Identity provider request could not be completed; its outcome is unknown."); }
        catch (System.Text.DecoderFallbackException) { return AZOAResult<Response>.Failure("Identity provider response encoding is invalid."); }
    }
}

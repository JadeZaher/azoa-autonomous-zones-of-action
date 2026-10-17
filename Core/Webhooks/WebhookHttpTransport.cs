namespace AZOA.WebAPI.Core.Webhooks;

/// <summary>Creates the shared bounded, direct webhook connection transport.</summary>
public static class WebhookHttpTransport
{
    public const int MaximumBodyBytes = 1024 * 1024;

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        MaxResponseHeadersLength = 32,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = WebhookSsrfGuard.CreateGuardedConnectCallback(),
    };
}

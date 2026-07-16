using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>Selects authentication without inspecting credentials on marked public routes.</summary>
public static class AuthenticationSchemeSelector
{
    /// <summary>Resolves the authentication handler for the routed request.</summary>
    public static string Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetEndpoint()?.Metadata
                .GetMetadata<CredentialFreePublicEndpointAttribute>() is not null)
        {
            return CredentialFreeAuthenticationHandler.SchemeName;
        }

        return context.Request.Headers.ContainsKey("X-Api-Key")
            ? ApiKeyAuthenticationHandler.SchemeName
            : JwtBearerDefaults.AuthenticationScheme;
    }
}

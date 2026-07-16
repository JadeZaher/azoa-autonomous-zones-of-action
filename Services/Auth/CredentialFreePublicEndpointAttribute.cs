namespace AZOA.WebAPI.Services.Auth;

/// <summary>Skips API-key authentication and its store lookup on a public endpoint.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class CredentialFreePublicEndpointAttribute : Attribute
{
}

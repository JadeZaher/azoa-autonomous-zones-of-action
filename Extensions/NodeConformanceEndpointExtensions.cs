using AZOA.WebAPI.Core.Diagnostics;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Services.Conformance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AZOA.WebAPI.Extensions;

/// <summary>Maps the narrow public L0 descriptor endpoint.</summary>
public static class NodeConformanceEndpointExtensions
{
    /// <summary>Maps the credential-free, bounded node conformance document.</summary>
    public static IEndpointRouteBuilder MapNodeConformanceDocument(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/.well-known/azoa-node.json", GetDocumentAsync)
            .AllowAnonymous()
            .WithMetadata(
                new CredentialFreePublicEndpointAttribute(),
                new SuppressDebugExceptionDetailsAttribute())
            .WithDisplayName("AZOA node conformance document");
        return app;
    }

    private static async Task<IResult> GetDocumentAsync(
        INodeConformanceManifestService manifests,
        HttpContext context,
        CancellationToken ct)
    {
        var availability = await manifests.TryGetDocumentAsync(ct);
        if (!availability.IsAvailable || availability.Document is null)
            return Results.StatusCode(StatusCodes.Status404NotFound);

        context.Response.Headers.CacheControl = "no-store";
        return Results.Bytes(
            NodeConformanceCanonicalizer.SerializeDocument(availability.Document),
            "application/json; charset=utf-8");
    }
}

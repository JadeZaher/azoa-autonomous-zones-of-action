using AZOA.WebAPI.Core.Diagnostics;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Middleware;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Services.Governance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/node-transparency")]
[AllowAnonymous]
[CredentialFreePublicEndpoint]
[EnableCors(DynamicCorsPolicyProvider.PublicTransparencyPolicy)]
[SuppressDebugExceptionDetails]
public sealed class NodeTransparencyController : ControllerBase
{
    private const int CacheSeconds = 30;
    private readonly INodeTransparencyManager _manager;

    public NodeTransparencyController(INodeTransparencyManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    [HttpGet("current")]
    public async Task<ActionResult<AZOAResult<NodeTransparencySnapshotResponse>>> GetCurrent(
        CancellationToken ct)
    {
        var result = await _manager.GetSnapshotAsync(ct);
        return Cacheable(result, result.Result?.ContentSha256);
    }

    [HttpGet("audit/governance")]
    public async Task<ActionResult<AZOAResult<NodeTransparencyPageResponse<PublicNodeGovernanceAuditResponse>>>> ListGovernanceAudit(
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        var result = await _manager.ListGovernanceAuditAsync(limit, cursor, ct);
        return Cacheable(result, result.Result?.ContentSha256);
    }

    [HttpGet("audit/fees")]
    public async Task<ActionResult<AZOAResult<NodeTransparencyPageResponse<PublicNodeFeeAuditResponse>>>> ListFeeAudit(
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        var result = await _manager.ListFeeAuditAsync(limit, cursor, ct);
        return Cacheable(result, result.Result?.ContentSha256);
    }

    [HttpGet("audit/treasury")]
    public async Task<ActionResult<AZOAResult<NodeTransparencyPageResponse<PublicNodeTreasuryAuditResponse>>>> ListTreasuryAudit(
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        var result = await _manager.ListTreasuryAuditAsync(limit, cursor, ct);
        return Cacheable(result, result.Result?.ContentSha256);
    }

    [HttpGet("audit/checkpoint")]
    public async Task<ActionResult<AZOAResult<NodeTransparencyHistoryDocument>>> GetAuditHistoryCheckpoint(
        CancellationToken ct)
    {
        var result = await _manager.GetAuditHistoryCheckpointAsync(ct);
        var digest = result.Result is null
            ? null
            : NodeTransparencyContentHash.Compute("audit-checkpoint", result.Result);
        return Cacheable(result, digest);
    }

    private ActionResult<AZOAResult<T>> Cacheable<T>(AZOAResult<T> result, string? digest)
    {
        if (result.IsError)
        {
            Response.Headers.CacheControl = "no-store";
            if (string.Equals(result.Message, NodeTransparencyMessages.InvalidCursor, StringComparison.Ordinal))
                return BadRequest(result);

            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new AZOAResult<T>
                {
                    IsError = true,
                    Message = NodeTransparencyMessages.Unavailable,
                });
        }

        if (result.Result is null || string.IsNullOrWhiteSpace(digest))
        {
            Response.Headers.CacheControl = "no-store";
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new AZOAResult<T>
                {
                    IsError = true,
                    Message = NodeTransparencyMessages.Unavailable,
                });
        }

        var etag = new EntityTagHeaderValue($"\"{digest}\"", isWeak: true);
        var responseHeaders = Response.GetTypedHeaders();
        responseHeaders.ETag = etag;
        responseHeaders.CacheControl = new CacheControlHeaderValue
        {
            Public = true,
            MaxAge = TimeSpan.FromSeconds(CacheSeconds),
        };
        Response.Headers.Vary = "Accept-Encoding";

        var suppliedEtags = Request.GetTypedHeaders().IfNoneMatch;
        if (suppliedEtags is not null
            && suppliedEtags.Any(candidate =>
                candidate.Equals(EntityTagHeaderValue.Any)
                || candidate.Compare(etag, useStrongComparison: false)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(result);
    }
}

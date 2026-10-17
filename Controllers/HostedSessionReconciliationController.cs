using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Providers.Kyb;
using AZOA.WebAPI.Services.HostedSessions;

namespace AZOA.WebAPI.Controllers;

/// <summary>Audited operator recovery for ambiguous hosted-session creation.</summary>
[ApiController]
[Route("api/operator/hosted-sessions")]
[Authorize(Policy = "RecentNodeOperatorSession")]
public sealed class HostedSessionReconciliationController(
    IHostedSessionStore store,
    IdentomatKycProviderService kyc,
    IdentomatKybProvider kyb,
    IHostedProviderAttemptStore? attempts = null,
    HostedIdentityRecovery? recovery = null) : ControllerBase
{
    [HttpGet("reconciliation")]
    public async Task<IActionResult> List([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var rows = await store.ListReconciliationAsync(DateTimeOffset.UtcNow.AddMinutes(-5), limit, ct);
        return Ok(AZOAResult<object>.Success(rows.Select(row => new
        {
            SessionId = row.Id,
            row.FlowKind,
            row.ProviderKey,
            row.SubjectUserId,
            row.TenantId,
            Status = "NeedsOperator",
            row.CreatedDate,
        })));
    }

    [HttpPost("{sessionId:guid}/reconcile")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> Reconcile(Guid sessionId, [FromBody] HostedSessionReconciliationRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)) return Unauthorized();
        var reason = request.Reason?.Trim();
        if (reason is null || reason.Length < 20 || reason.Length > 1000 || !request.OperatorReviewedSubjectAndProvider)
            return BadRequest("A reviewed provider/subject decision and a 20â€“1000 character evidence reference are required.");
        if (request.ConfirmedProviderAbsence == !string.IsNullOrWhiteSpace(request.ProviderSessionId))
            return BadRequest("Select either confirmed provider absence or a verified existing session.");

        var located = await store.GetByIdAsync(sessionId, ct);
        if (located.IsError) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        var row = located.Result;
        if (row is null) return NotFound();
        if (row.ProviderKey != "identomat")
        {
            var attempt = attempts is null ? null : await attempts.GetAsync(row.Id, ct);
            if (attempt is null || recovery is null) return Conflict("The saved provider recovery contract is unavailable.");
            if (request.ConfirmedProviderAbsence)
            {
                var quarantined = await store.ReconcileAdmissionAsync(row, actorId, reason, null, DateTimeOffset.UtcNow, ct);
                if (quarantined.IsError || quarantined.Result is null) return Conflict("The admission changed during reconciliation.");
                return Ok(AZOAResult<object>.Success(new { SessionId = row.Id, Status = quarantined.Result.State == HostedSessionStates.Abandoned ? "ResolvedUnsent" : "Quarantined", quarantined.Result.ReconciledAt }));
            }
            var recoveredAttempt = await recovery.RecoverAsync(attempt, request.ProviderSessionId?.Trim(), ct, actorId, reason);
            if (recoveredAttempt.IsError || recoveredAttempt.Result is null) return Conflict(recoveredAttempt.Message);
            return Ok(AZOAResult<object>.Success(new { SessionId = row.Id, Status = recoveredAttempt.Result.State, recoveredAttempt.Result.ReconciledAt }));
        }
        if (row.ProviderKey != "identomat" || row.State != HostedSessionStates.Claimed
            || row.ProviderSessionId is not null || (row.CreationClaimedAt ?? row.CreatedDate) > DateTimeOffset.UtcNow.AddMinutes(-5))
            return Conflict("This session is not eligible for reconciliation.");

        HostedSessionCreation? recovered = null;
        if (!request.ConfirmedProviderAbsence)
        {
            IHostedSessionProvider? provider = row.FlowKind switch
            {
                HostedSessionFlowKinds.Kyc => kyc,
                HostedSessionFlowKinds.Kyb => kyb,
                _ => null,
            };
            if (provider is null) return BadRequest("Unsupported hosted flow.");
            var providerId = request.ProviderSessionId!.Trim();
            if (providerId.Length > 256 || providerId.Any(char.IsControl)) return BadRequest("Invalid provider session reference.");
            var outcome = await provider.FetchAuthoritativeOutcomeAsync(providerId, ct);
            if (outcome.IsError || outcome.Result is null || !outcome.Result.SessionExists)
                return Conflict("The provider session could not be authoritatively verified.");
            if (!Uri.TryCreate(request.RedirectUrl, UriKind.Absolute, out var redirect)
                || redirect.Scheme != Uri.UriSchemeHttps || redirect.Host != "widget.identomat.com"
                || !string.IsNullOrEmpty(redirect.UserInfo) || !string.IsNullOrEmpty(redirect.Fragment))
                return BadRequest("A verified Identomat widget HTTPS redirect is required.");
            if (!string.IsNullOrWhiteSpace(request.ProviderCorrelationId)
                && request.ProviderCorrelationId.Trim() != providerId)
                return BadRequest("A recovered callback alias must be proven by the provider; only the verified canonical session is accepted.");
            recovered = new HostedSessionCreation
            {
                ProviderSessionId = providerId,
                ProviderCorrelationId = null,
                RedirectUrl = redirect.AbsoluteUri,
            };
        }

        var result = await store.ReconcileAdmissionAsync(row, actorId, reason, recovered, DateTimeOffset.UtcNow, ct);
        if (result.IsError) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (result.Result is null) return Conflict("The admission changed before reconciliation was committed.");
        return Ok(AZOAResult<object>.Success(new { SessionId = row.Id, Status = recovered is not null ? "Recovered"
            : result.Result.State == HostedSessionStates.Abandoned ? "ResolvedUnsent" : "Quarantined", result.Result.ReconciledAt }));
    }

    [HttpPost("{sessionId:guid}/refresh")]
    public async Task<IActionResult> Refresh(Guid sessionId, CancellationToken ct)
    {
        var attempt = attempts is null ? null : await attempts.GetAsync(sessionId.ToString("N"), ct);
        if (attempt is null || recovery is null) return NotFound();
        var result = await recovery.RecoverAsync(attempt, null, ct);
        return result.IsError || result.Result is null ? Conflict(result.Message)
            : Ok(AZOAResult<object>.Success(new { SessionId = sessionId, Status = result.Result.State, result.Result.ProviderKey }));
    }
}


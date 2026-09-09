// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Models.Responses
{
    /// <summary>One person whose identity verified but whose AML verdict is not clear.</summary>
    /// <remarks>
    /// Node-operator-only. It carries AZOA's own verdict and the match strength that
    /// produced it — never the provider's raw listing payload, and never the subject's
    /// name or date of birth, which stay in the screening ledger under D25 retention.
    /// </remarks>
    public sealed class AmlManualReviewItem
    {
        /// <summary>Verdict for a person with no screening row at all; nothing to supersede.</summary>
        public const string NotScreened = "NOT_SCREENED";

        public Guid UserId { get; set; }

        /// <summary>Null for a node-direct subject; screening is scoped by this pair.</summary>
        public Guid? TenantId { get; set; }

        /// <summary>Bare id of the screening row a decision must supersede; null when never screened.</summary>
        public string? ScreeningId { get; set; }

        /// <summary><c>NOT_SCREENED</c>, <c>MANUAL_REVIEW</c>, <c>HIT</c>, or <c>NO_HIT</c> once decided.</summary>
        public string Decision { get; set; } = NotScreened;

        public int TopScore { get; set; }

        public int HitCount { get; set; }

        /// <summary>Open string set from the provider; never a closed enum.</summary>
        public IReadOnlyList<string> Topics { get; set; } = Array.Empty<string>();

        /// <summary>True when the provider's hit list saturated; such a row can never read clear.</summary>
        public bool ResultsTruncated { get; set; }

        public DateTimeOffset? ScreenedAt { get; set; }

        /// <summary>The operator's own SLA deadline; null when nothing has been screened.</summary>
        public DateTimeOffset? ReviewDueAt { get; set; }

        public bool Overdue { get; set; }

        /// <summary>Risk-policy stamp on the row; the manual stamp marks an operator decision.</summary>
        public string PolicyVersion { get; set; } = string.Empty;

        /// <summary>True only when a screening row exists AND it is not already clear.</summary>
        public bool DecisionAllowed { get; set; }

        /// <summary>The operator's route to a human, or null when none is configured.</summary>
        public string? ReviewContact { get; set; }

        /// <summary>The exact wording the subject is shown, so the console cannot drift from it.</summary>
        public string SubjectNotice { get; set; } = string.Empty;
    }
}

namespace AZOA.WebAPI.Models.Requests
{
    /// <summary>An operator's AML decision over one screening row.</summary>
    public sealed class AmlManualReviewDecisionRequest
    {
        /// <summary>Records a clean verdict that re-opens the value gates.</summary>
        public const string Clear = "clear";

        /// <summary>Records an adverse verdict; the gates stay closed.</summary>
        public const string Deny = "deny";

        public string Decision { get; set; } = string.Empty;

        /// <summary>Screening is scoped by (subject, tenant); omit for a node-direct subject.</summary>
        public Guid? TenantId { get; set; }

        /// <summary>The row the operator actually read; a newer row conflicts instead of decided over.</summary>
        public string ExpectedScreeningId { get; set; } = string.Empty;

        /// <summary>Why. Required, bounded, and written to the operator log with the decision.</summary>
        public string Reason { get; set; } = string.Empty;
    }
}

namespace AZOA.WebAPI.Controllers
{
    /// <summary>
    /// The node operator's AML manual-review desk: the queue of people a sanctions
    /// screening flagged, and the decision that supersedes it.
    ///
    /// <para>It is a separate controller from <c>NodeOperatorController</c> because a
    /// decision here re-opens a value gate for a real person. That is not KYC
    /// configuration, and it carries its own step-up, rate limit and body cap.</para>
    /// </summary>
    [ApiController]
    [Route("api/operator")]
    [Authorize(Policy = "NodeOperatorSession")]
    public sealed class AmlReviewController : ControllerBase
    {
        private readonly IKycControlPlaneManager _manager;

        public AmlReviewController(IKycControlPlaneManager manager)
        {
            _manager = manager;
        }

        [HttpGet("kyc/aml-reviews")]
        public async Task<ActionResult<AZOAResult<CursorPage<AmlManualReviewItem>>>> ListReviews(
            [FromQuery] int limit = 25,
            [FromQuery] string? cursor = null,
            CancellationToken ct = default)
        {
            if (!AzoaClaims.TryGetSubjectId(User, out var operatorId))
                return Unauthorized();
            return Translate(await _manager.ListAmlReviewsAsync(limit, cursor, operatorId, ct));
        }

        [HttpPost("kyc/aml-reviews/{userId:guid}/decision")]
        [Authorize(Policy = "RecentNodeOperatorSession")]
        [EnableRateLimiting("financial")]
        [RequestSizeLimit(16384)]
        public async Task<ActionResult<AZOAResult<AmlManualReviewItem>>> Decide(
            Guid userId,
            [FromBody] AmlManualReviewDecisionRequest request,
            CancellationToken ct)
        {
            if (!AzoaClaims.TryGetSubjectId(User, out var operatorId))
                return Unauthorized();
            return Translate(await _manager.DecideAmlReviewAsync(userId, request, operatorId, ct));
        }

        private ActionResult<AZOAResult<T>> Translate<T>(AZOAResult<T> result)
        {
            if (!result.IsError)
                return Ok(result);
            return result.Code switch
            {
                AzoaErrorCodes.NotFound => NotFound(result),
                AzoaErrorCodes.Conflict => Conflict(result),
                AzoaErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, result),
                AzoaErrorCodes.InvalidRequest
                    or AzoaErrorCodes.PolicyUnavailable
                    or AzoaErrorCodes.OperationNotAllowed => BadRequest(result),
                _ => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
            };
        }
    }
}

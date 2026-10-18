using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Typed node-admin failures and their HTTP status mapping.</summary>
public static class NodeAdminResults
{
    public static AZOAResult<T> Fail<T>(string code, string message) => AZOAResult<T>.FailureWithCode(message, code);

    /// <summary>HTTP status for a node-admin <c>result.code</c>.</summary>
    public static int StatusFor(string? code) => code switch
    {
        NodeAdminErrorCodes.AuthFailed => StatusCodes.Status401Unauthorized,
        NodeAdminErrorCodes.AdminLocked => StatusCodes.Status423Locked,
        NodeAdminErrorCodes.PermissionDenied or NodeAdminErrorCodes.StepUpRequired or NodeAdminErrorCodes.DominanceRequired
            or NodeAdminErrorCodes.EscalationRefused or NodeAdminErrorCodes.SelfActionRefused => StatusCodes.Status403Forbidden,
        NodeAdminErrorCodes.NotFound => StatusCodes.Status404NotFound,
        NodeAdminErrorCodes.LastOwner or NodeAdminErrorCodes.UsernameTaken or NodeAdminErrorCodes.RoleInUse
            or NodeAdminErrorCodes.RoleBuiltIn or NodeAdminErrorCodes.EnrollmentAlreadyStarted
            or NodeAdminErrorCodes.FingerprintMismatch or NodeAdminErrorCodes.Conflict => StatusCodes.Status409Conflict,
        NodeAdminErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,
        NodeAdminErrorCodes.AuditUnavailable or NodeAdminErrorCodes.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };
}

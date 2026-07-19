// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;

namespace AZOA.WebAPI.Core;

/// <summary>
/// Central vocabulary for AZOA-specific JWT claim names + values (security-review S5).
/// Login tokens and tenant child credentials are signed with the SAME
/// <c>Jwt:Key</c> / HmacSha256 / issuer / audience — so without an explicit type
/// marker the only thing distinguishing a full-authority user login from a scoped,
/// consent-gated tenant child credential is the presence of <c>act_as_tenant</c>.
/// The <see cref="TokenUse"/> claim makes the credential class unambiguous and is the
/// hook any future endpoint can validate to refuse the wrong token type.
/// </summary>
public static class AzoaClaims
{
    /// <summary>Claim name carrying the credential class (<see cref="TokenUseLogin"/>
    /// or <see cref="TokenUseChild"/>).</summary>
    public const string TokenUse = "token_use";

    /// <summary>A full-authority interactive user login token (wallet-challenge or
    /// password). NEVER carries <c>act_as_tenant</c>.</summary>
    public const string TokenUseLogin = "login";

    /// <summary>A scoped, consent-gated tenant child credential minted by
    /// <c>TenantManager.IssueChildCredentialAsync</c>. ALWAYS carries
    /// <c>act_as_tenant</c> and a clamped set of <c>scope</c> claims.</summary>
    public const string TokenUseChild = "child";

    /// <summary>A short-lived dedicated node-operator session.</summary>
    public const string TokenUseNodeOperator = "node_operator";

    /// <summary>Monotonic durable operator credential revision.</summary>
    public const string OperatorRevision = "operator_revision";

    /// <summary>Monotonic revocation epoch for all active operator sessions.</summary>
    public const string OperatorSessionRevision = "operator_session_revision";

    /// <summary>Unix timestamp of the interactive authentication event.</summary>
    public const string AuthTime = "auth_time";

    public static bool IsChildCredential(ClaimsPrincipal? principal)
        => string.Equals(
            principal?.FindFirst(TokenUse)?.Value,
            TokenUseChild,
            StringComparison.Ordinal);

    public static bool IsNodeOperator(ClaimsPrincipal? principal)
        => string.Equals(
            principal?.FindFirst(TokenUse)?.Value,
            TokenUseNodeOperator,
            StringComparison.Ordinal);

    public static bool TryGetSubjectId(ClaimsPrincipal? principal, out Guid subjectId)
    {
        var value = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal?.FindFirst("sub")?.Value;
        return Guid.TryParse(value, out subjectId);
    }
}

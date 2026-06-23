// SPDX-License-Identifier: UNLICENSED

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
}

// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.HostedSessions;

/// <summary>
/// The reusable hosted-redirect provider seam (D11): begin, hosted redirect,
/// untrusted callback, AUTHORITATIVE server-side re-fetch, resume. This is
/// deliberately NOT part of <c>IKycProviderService</c> — a fiat-in checkout
/// provider implements exactly this interface and nothing KYC-shaped. See
/// <c>Services/HostedSessions/AGENTS.md</c>.
/// </summary>
public interface IHostedSessionProvider
{
    /// <summary>Flow family this provider serves, e.g. <c>kyc</c>.</summary>
    string FlowKind { get; }

    /// <summary>Stable public provider key.</summary>
    string ProviderKey { get; }

    /// <summary>Reports availability and, when unavailable, the missing key NAMES.</summary>
    HostedSessionCapabilities GetHostedCapabilities();

    /// <summary>
    /// Creates the external provider session. The orchestrator has already claimed
    /// durable admission, so this call may safely have an external side effect.
    /// </summary>
    Task<AZOAResult<HostedSessionCreation>> CreateHostedSessionAsync(
        HostedSessionRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies an untrusted inbound callback in constant time and extracts ONLY the
    /// correlation needed to re-fetch. Implementations must not return, log, or echo
    /// the shared secret, and must not surface the payload's own claimed outcome.
    /// </summary>
    AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody);

    /// <summary>
    /// Fetches the authoritative outcome server-side. This is the ONLY source of
    /// truth for a verdict; a callback body never is.
    /// </summary>
    Task<AZOAResult<HostedSessionOutcome>> FetchAuthoritativeOutcomeAsync(
        string providerSessionId,
        CancellationToken ct = default);
}

// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.HostedSessions;

/// <summary>A hosted provider that can delete a vendor session it created (late create responses after supersession).</summary>
public interface IDeletableHostedSessionProvider
{
    /// <summary>Deletes the vendor session; an already-absent session counts as deleted.</summary>
    Task<AZOAResult<bool>> DeleteProviderSessionAsync(string providerSessionId, CancellationToken ct = default);
}

/// <summary>Domain hook for an authoritative NON-terminal outcome (e.g. the vendor's manual review).</summary>
public interface IHostedSessionProgressHandler
{
    /// <summary>Records progress without deciding anything; a stale or mismatched attempt is a silent no-op.</summary>
    Task ObserveProgressAsync(HostedSession session, HostedSessionOutcome outcome, CancellationToken ct = default);
}

/// <summary>Whether a session's pending outcome can still change domain state.</summary>
public enum HostedOutcomeRelevance
{
    /// <summary>The bound attempt is open and unexpired: a verdict would still be applied.</summary>
    Live,

    /// <summary>The bound attempt is decided, retired, superseded or expired: any verdict is a no-op.</summary>
    Dead,

    /// <summary>The domain could not tell (read fault, unknown flow); keep polling until the max-age backstop.</summary>
    Unknown,
}

/// <summary>Domain probe the outcome poll asks before spending vendor budget on a session (RV-L2 L2-2).</summary>
public interface IHostedSessionRelevanceProbe
{
    /// <summary>Read-only; never mutates the session or the attempt.</summary>
    Task<HostedOutcomeRelevance> ProbeAsync(HostedSession session, DateTimeOffset now, CancellationToken ct = default);
}

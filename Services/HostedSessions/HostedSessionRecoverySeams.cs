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

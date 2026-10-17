// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Fiat;

/// <summary>What a Stripe-backed hosted admission may do next.</summary>
public enum FiatCreationVerdict
{
    /// <summary>The creation fence is held; send (or replay) the claim-keyed Stripe create.</summary>
    Send,

    /// <summary>The claim already carries its Stripe object; use it.</summary>
    Attached,

    /// <summary>Contention or an unreadable row; retry shortly.</summary>
    Busy,

    /// <summary>The send outcome can no longer be replayed safely; operator reconciliation required.</summary>
    NeedsOperator,
}

/// <summary>The verdict plus the row it was decided on.</summary>
public readonly record struct FiatCreationTurn(FiatCreationVerdict Verdict, HostedSession? Session);

/// <summary>
/// Creation fence for Stripe-backed hosted admissions: one durable creation token per claim keys
/// both the Stripe idempotency key and the attach predicate. See <c>Providers/Fiat/AGENTS.md</c> §creation-fence.
/// </summary>
public static class FiatHostedCreation
{
    /// <summary>Stripe keeps idempotency keys for at least 24 hours; replays stop an hour short of that.</summary>
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromHours(23);

    /// <summary>Stripe idempotency key bound to one claim incarnation (row id + creation token).</summary>
    public static string IdempotencyKey(string prefix, HostedSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session.CreationToken);
        return prefix + session.Id + "-" + session.CreationToken;
    }

    /// <summary>Begins the creation fence for a claimed row, or resumes a fence some earlier request already began.</summary>
    public static async Task<FiatCreationTurn> BeginOrResumeAsync(
        IHostedSessionStore store, HostedSession claimed, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(claimed);
        if (!Guid.TryParseExact(claimed.Id, "N", out var id))
            return new(FiatCreationVerdict.Busy, null);

        var session = claimed;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!string.IsNullOrWhiteSpace(session.ProviderSessionId))
                return new(FiatCreationVerdict.Attached, session);
            if (session.State != HostedSessionStates.Claimed || string.IsNullOrWhiteSpace(session.CreationToken)
                || session.CreationQuarantinedAt is not null)
                return new(FiatCreationVerdict.NeedsOperator, session);
            if (session.CreationStartedAt is { } started)
                return started > now - ReplayWindow
                    ? new(FiatCreationVerdict.Send, session)
                    : new(FiatCreationVerdict.NeedsOperator, session);

            var begun = await store.TryBeginCreationAsync(id, session.CreationToken, now, ct);
            if (!begun.IsError && begun.Result is not null)
                return new(FiatCreationVerdict.Send, begun.Result);

            var reread = await store.GetByIdAsync(id, ct);
            if (reread.IsError || reread.Result is null)
                return new(FiatCreationVerdict.Busy, null);
            session = reread.Result;
        }

        return new(FiatCreationVerdict.Busy, session);
    }
}

// SPDX-License-Identifier: UNLICENSED

using System.Diagnostics;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IHostedSessionStore"/>.
///
/// <para>Every mutation is conditional. <see cref="TryTransitionAsync"/> requires
/// both the exact live <c>state_version</c> AND a non-terminal current state, so a
/// redelivered callback that raced a first delivery gets an empty result set rather
/// than a second terminal write. The provider documents no dedup or retry
/// semantics, so this is the only thing standing between a retry storm and a
/// double-applied outcome.</para>
/// </summary>
public sealed class SurrealHostedSessionStore : IHostedSessionStore
{
    private const string PublicStoreError =
        "HOSTED_SESSION_STORE_UNAVAILABLE: Hosted session persistence is temporarily unavailable.";

    private static readonly string[] TerminalStates =
    [
        HostedSessionStates.Completed,
        HostedSessionStates.Failed,
        HostedSessionStates.Abandoned,
    ];

    /// <summary>States from which a provider session may be attached or re-attached.</summary>
    private static readonly string[] AttachableStates =
    [
        HostedSessionStates.Claimed,
        HostedSessionStates.AwaitingSubject,
    ];

    private readonly ISurrealExecutor _executor;
    private readonly ILogger<SurrealHostedSessionStore> _logger;

    public SurrealHostedSessionStore(
        ISurrealExecutor executor,
        ILogger<SurrealHostedSessionStore>? logger = null)
    {
        _executor = executor;
        _logger = logger ?? NullLogger<SurrealHostedSessionStore>.Instance;
    }

    public async Task<AZOAResult<HostedSession>> ClaimAdmissionAsync(
        HostedSession candidate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        try
        {
            // An existing claim for the same admission key wins; the unique index on
            // (flow_kind, provider_key, admission_key) is the backstop if two callers
            // pass this read at once.
            var existing = await GetByAdmissionKeyAsync(
                candidate.FlowKind,
                candidate.ProviderKey,
                candidate.AdmissionKey,
                ct);
            if (existing.IsError)
                return existing;
            if (existing.Result is not null)
                return AZOAResult<HostedSession>.Success(existing.Result, "Resumed.");

            var body = ToStorage(candidate);
            var create = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", HostedSession.SchemaNameConst)
                .WithParam("_id", body.Id)
                .WithParam("_body", body);

            try
            {
                var response = await _executor.ExecuteAsync(create, ct);
                response.EnsureAllOk();
                var created = response.GetValues<HostedSession>(0).FirstOrDefault();
                return AZOAResult<HostedSession>.Success(
                    created is null ? candidate : FromStorage(created),
                    "Claimed.");
            }
            catch (Exception)
            {
                // A concurrent claim tripped the unique index. Re-read: the winner's
                // row is the correct answer, not an error.
                var raced = await GetByAdmissionKeyAsync(
                    candidate.FlowKind,
                    candidate.ProviderKey,
                    candidate.AdmissionKey,
                    ct);
                if (!raced.IsError && raced.Result is not null)
                    return AZOAResult<HostedSession>.Success(raced.Result, "Resumed.");
                throw;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<HostedSession>(exception, nameof(ClaimAdmissionAsync), candidate.FlowKind);
        }
    }

    public async Task<AZOAResult<HostedSession>> GetByProviderSessionAsync(
        string flowKind,
        string providerKey,
        string providerSessionId,
        CancellationToken ct = default)
    {
        try
        {
            // Matches EITHER correlation: a provider may name the parent session id in
            // its callback while the authoritative fetch wants the child token, so a
            // single-column match would silently drop legitimate callbacks.
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE flow_kind = $_flow AND provider_key = $_provider AND (provider_session_id = $_session OR provider_correlation_id = $_session) LIMIT 1")
                .WithParam("_t", HostedSession.SchemaNameConst)
                .WithParam("_flow", flowKind)
                .WithParam("_provider", providerKey)
                .WithParam("_session", providerSessionId);

            var row = await _executor.QuerySingleAsync<HostedSession>(query, ct);
            return new AZOAResult<HostedSession>
            {
                Message = row is null ? "No hosted session found." : "Success",
                Result = row is null ? null : FromStorage(row),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<HostedSession>(exception, nameof(GetByProviderSessionAsync), flowKind);
        }
    }

    public async Task<AZOAResult<HostedSession>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery.SelectById(HostedSession.SchemaNameConst, id.ToString("N"));
            var row = await _executor.QuerySingleAsync<HostedSession>(query, ct);
            return new AZOAResult<HostedSession>
            {
                Message = row is null ? "No hosted session found." : "Success",
                Result = row is null ? null : FromStorage(row),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<HostedSession>(exception, nameof(GetByIdAsync), id);
        }
    }

    public async Task<AZOAResult<HostedSession>> TryAttachProviderSessionAsync(
        Guid id,
        long expectedStateVersion,
        string providerSessionId,
        string? providerCorrelationId,
        string redirectUrl,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("""
                    UPDATE type::record($_t, $_id)
                    SET provider_session_id = $_session,
                        provider_correlation_id = $_correlation,
                        redirect_url = $_redirect,
                        expires_at = $_expires_at,
                        state = $_state,
                        state_version = state_version + 1,
                        modified_date = time::now()
                    WHERE state INSIDE $_attachable AND state_version = $_expected_version
                    RETURN AFTER
                    """)
                .WithParam("_t", HostedSession.SchemaNameConst)
                .WithParam("_id", id.ToString("N"))
                .WithParam("_session", providerSessionId)
                .WithParam("_correlation", providerCorrelationId)
                .WithParam("_redirect", redirectUrl)
                .WithParam("_expires_at", expiresAt?.ToUniversalTime())
                .WithParam("_state", HostedSessionStates.AwaitingSubject)
                // AWAITING_SUBJECT is attachable as well as CLAIMED so an EXPIRED
                // hosted session can be renewed in place. Restricting this to CLAIMED
                // would leave an expired-but-non-terminal row permanently unrenewable.
                // The version guard still admits exactly one writer.
                .WithParam("_attachable", AttachableStates)
                .WithParam("_expected_version", expectedStateVersion);

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            var updated = response.GetValues<HostedSession>(0).FirstOrDefault();
            return new AZOAResult<HostedSession>
            {
                Message = updated is null ? "Hosted session admission changed." : "Attached.",
                Result = updated is null ? null : FromStorage(updated),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<HostedSession>(exception, nameof(TryAttachProviderSessionAsync), id);
        }
    }

    public async Task<AZOAResult<HostedSession>> TryTransitionAsync(
        Guid id,
        long expectedStateVersion,
        string newState,
        string? authoritativeOutcome,
        string? outcomeDetail,
        string? resumeTargetId,
        CancellationToken ct = default)
    {
        try
        {
            var terminal = HostedSessionStates.IsTerminal(newState);
            var query = SurrealQuery
                .Of("""
                    UPDATE type::record($_t, $_id)
                    SET state = $_state,
                        state_version = state_version + 1,
                        authoritative_outcome = $_outcome,
                        outcome_detail = $_detail,
                        resume_target_id = $_resume_target,
                        completed_at = IF $_terminal THEN time::now() ELSE completed_at END,
                        modified_date = time::now()
                    WHERE state_version = $_expected_version AND state NOTINSIDE $_terminal_states
                    RETURN AFTER
                    """)
                .WithParam("_t", HostedSession.SchemaNameConst)
                .WithParam("_id", id.ToString("N"))
                .WithParam("_state", newState)
                .WithParam("_outcome", authoritativeOutcome)
                .WithParam("_detail", outcomeDetail)
                .WithParam("_resume_target", resumeTargetId)
                .WithParam("_terminal", terminal)
                .WithParam("_expected_version", expectedStateVersion)
                .WithParam("_terminal_states", TerminalStates);

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();

            // A null row is the expected loser outcome, NOT an error: the winning
            // delivery already applied the single terminal write.
            var updated = response.GetValues<HostedSession>(0).FirstOrDefault();
            return new AZOAResult<HostedSession>
            {
                Message = updated is null ? "Hosted session already transitioned." : "Transitioned.",
                Result = updated is null ? null : FromStorage(updated),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<HostedSession>(exception, nameof(TryTransitionAsync), id);
        }
    }

    public async Task<AZOAResult<bool>> RecordCallbackAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET callback_count = callback_count + 1, last_callback_at = time::now() RETURN NONE")
                .WithParam("_t", HostedSession.SchemaNameConst)
                .WithParam("_id", id.ToString("N"));

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return AZOAResult<bool>.Success(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<bool>(exception, nameof(RecordCallbackAsync), id);
        }
    }

    private async Task<AZOAResult<HostedSession>> GetByAdmissionKeyAsync(
        string flowKind,
        string providerKey,
        string admissionKey,
        CancellationToken ct)
    {
        try
        {
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE flow_kind = $_flow AND provider_key = $_provider AND admission_key = $_admission LIMIT 1")
                .WithParam("_t", HostedSession.SchemaNameConst)
                .WithParam("_flow", flowKind)
                .WithParam("_provider", providerKey)
                .WithParam("_admission", admissionKey);

            var row = await _executor.QuerySingleAsync<HostedSession>(query, ct);
            return new AZOAResult<HostedSession>
            {
                Message = row is null ? "No hosted session found." : "Success",
                Result = row is null ? null : FromStorage(row),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<HostedSession>(exception, nameof(GetByAdmissionKeyAsync), flowKind);
        }
    }

    private AZOAResult<T> StoreFailure<T>(Exception exception, string operation, object entityId)
    {
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        _logger.LogError(
            exception,
            "Hosted session persistence failure in {Operation}; correlation={CorrelationId}; entity={EntityId}",
            operation,
            correlationId,
            entityId);
        return AZOAResult<T>.FailureWithCode(PublicStoreError, AzoaErrorCodes.DependencyUnavailable);
    }

    private static HostedSession ToStorage(HostedSession session) => new()
    {
        Id = session.Id,
        FlowKind = session.FlowKind,
        ProviderKey = session.ProviderKey,
        AdmissionKey = session.AdmissionKey,
        SubjectUserId = string.IsNullOrWhiteSpace(session.SubjectUserId)
            ? null
            : SurrealLink.ToLink("user", session.SubjectUserId),
        TenantId = string.IsNullOrWhiteSpace(session.TenantId)
            ? null
            : SurrealLink.ToLink("user", session.TenantId),
        ProviderSessionId = session.ProviderSessionId,
        ProviderCorrelationId = session.ProviderCorrelationId,
        RedirectUrl = session.RedirectUrl,
        State = session.State,
        StateVersion = session.StateVersion,
        AuthoritativeOutcome = session.AuthoritativeOutcome,
        OutcomeDetail = session.OutcomeDetail,
        CallbackCount = session.CallbackCount,
        ResumeTargetId = session.ResumeTargetId,
        CreatedDate = session.CreatedDate.ToUniversalTime(),
        ModifiedDate = session.ModifiedDate?.ToUniversalTime(),
        ExpiresAt = session.ExpiresAt?.ToUniversalTime(),
        LastCallbackAt = session.LastCallbackAt?.ToUniversalTime(),
        CompletedAt = session.CompletedAt?.ToUniversalTime(),
    };

    private static HostedSession FromStorage(HostedSession session) => new()
    {
        Id = session.Id,
        FlowKind = session.FlowKind,
        ProviderKey = session.ProviderKey,
        AdmissionKey = session.AdmissionKey,
        SubjectUserId = string.IsNullOrWhiteSpace(session.SubjectUserId)
            ? null
            : SurrealLink.FromLink(session.SubjectUserId),
        TenantId = string.IsNullOrWhiteSpace(session.TenantId)
            ? null
            : SurrealLink.FromLink(session.TenantId),
        ProviderSessionId = session.ProviderSessionId,
        ProviderCorrelationId = session.ProviderCorrelationId,
        RedirectUrl = session.RedirectUrl,
        State = session.State,
        StateVersion = session.StateVersion,
        AuthoritativeOutcome = session.AuthoritativeOutcome,
        OutcomeDetail = session.OutcomeDetail,
        CallbackCount = session.CallbackCount,
        ResumeTargetId = session.ResumeTargetId,
        CreatedDate = session.CreatedDate,
        ModifiedDate = session.ModifiedDate,
        ExpiresAt = session.ExpiresAt,
        LastCallbackAt = session.LastCallbackAt,
        CompletedAt = session.CompletedAt,
    };
}

// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using PackageReplay = SurrealForge.Client.Idempotency.IdempotencyReplay;
using PackageRecord = SurrealForge.Client.Idempotency.IdempotencyRecord;
using PackageState = SurrealForge.Client.Idempotency.IdempotencyState;

namespace AZOA.WebAPI.Core.Idempotency;

/// <summary>
/// AZOA-side idempotency-replay shim. All machinery lives in the package
/// (<see cref="PackageReplay"/>); this shim only binds the generic replay state
/// machine to AZOA's <see cref="AZOAResult{T}"/> envelope and maps the domain
/// <see cref="IdempotencyRecord"/> onto the package record. See
/// <c>Core/Idempotency/AGENTS.md</c>.
/// </summary>
public static class IdempotencyReplay
{
    /// <summary>Deterministic content-hash tail of an idempotency key.</summary>
    public static string ContentHash(string canonical) => PackageReplay.ContentHash(canonical);

    /// <summary>Serialize a completed result for caching on the idempotency record.</summary>
    public static string SerializeForReplay<T>(T result) => PackageReplay.SerializeForReplay(result);

    /// <summary>Deserialize a cached replay payload (returns null on malformed JSON).</summary>
    public static T? DeserializeForReplay<T>(string payload) where T : class
        => PackageReplay.DeserializeForReplay<T>(payload);

    /// <summary>
    /// Replay state machine bound to <see cref="AZOAResult{T}"/>. Behaviour is
    /// unchanged from the original inlined version; the package supplies the
    /// state machine and this call supplies the AZOAResult factory.
    /// </summary>
    public static AZOAResult<T> ReplayFromRecord<T>(
        IdempotencyRecord record,
        Func<string, T?> deserialize,
        Action<T> markReplayed,
        string replaySuccessMessage,
        string replayDeserializeFailedMessage,
        string originalFailedMessage,
        string inProgressMessage)
        where T : class
        => PackageReplay.ReplayFromRecord<T, AZOAResult<T>>(
            ToPackage(record),
            onSuccess: (r, msg) => new AZOAResult<T> { Result = r, Message = msg },
            onError:   msg      => new AZOAResult<T> { IsError = true, Message = msg },
            deserialize,
            markReplayed,
            replaySuccessMessage,
            replayDeserializeFailedMessage,
            originalFailedMessage,
            inProgressMessage);

    private static PackageRecord ToPackage(IdempotencyRecord r) => new()
    {
        Key           = r.Key,
        OperationType = r.OperationType,
        State         = r.State switch
        {
            IdempotencyState.Completed => PackageState.Completed,
            IdempotencyState.Failed    => PackageState.Failed,
            _                          => PackageState.InProgress,
        },
        ResultPayload = r.ResultPayload,
        Error         = r.Error,
        CreatedAt     = r.CreatedAt,
        UpdatedAt     = r.UpdatedAt,
    };
}

// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Core.Idempotency;

/// <summary>
/// Shared, behavior-preserving idempotency-replay machinery extracted from the
/// per-request managers (<c>AllocationManager</c>, <c>FungibleTokenManager</c>).
/// Every piece here is value-moving safety-critical: the content-hash key, the
/// replay state machine, and the JSON round-trip are reproduced VERBATIM from
/// the original inlined copies. The per-request canonical-field projection
/// (which fields are hashed) and the exact message text / result type stay with
/// each manager and are threaded in as parameters/callbacks.
/// </summary>
public static class IdempotencyReplay
{
    /// <summary>
    /// Deterministic content-hash tail of an idempotency key: SHA-256 over the
    /// already-canonicalised <paramref name="canonical"/> string, rendered as
    /// lowercase hex. This is the bare hash (no <c>op_…</c> prefix, no
    /// per-component escaping) — distinct from <see cref="OperationIdGenerator"/>,
    /// which formats a prefixed, separately-escaped operation id. Callers build
    /// the canonical string (e.g. <c>string.Join('|', fields…)</c>) so the field
    /// projection stays per-request.
    /// </summary>
    public static string ContentHash(string canonical)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// JSON options used for the replay round-trip. Web defaults, matching the
    /// original per-manager <c>ReplayJson</c>.
    /// </summary>
    public static readonly JsonSerializerOptions ReplayJson = new(JsonSerializerDefaults.Web);

    /// <summary>Serialize a completed result for caching on the idempotency record.</summary>
    public static string SerializeForReplay<T>(T result)
        => JsonSerializer.Serialize(result, ReplayJson);

    /// <summary>
    /// Deserialize a cached replay payload, swallowing malformed JSON (returns
    /// <c>null</c>) exactly as the original per-manager helpers did.
    /// </summary>
    public static T? DeserializeForReplay<T>(string payload) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(payload, ReplayJson); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The replay state machine shared by the per-request managers, generic over
    /// the result type <typeparamref name="T"/>. Behavior is identical to the
    /// original inlined <c>ReplayFromRecord</c>:
    /// <list type="bullet">
    /// <item><see cref="IdempotencyState.Completed"/> with a payload that
    /// deserializes ⇒ success carrying the cached result (after
    /// <paramref name="markReplayed"/>) and <paramref name="replaySuccessMessage"/>;
    /// if the payload cannot be replayed ⇒ failure with
    /// <paramref name="replayDeserializeFailedMessage"/>.</item>
    /// <item><see cref="IdempotencyState.Failed"/> ⇒ failure with the recorded
    /// <see cref="IdempotencyRecord.Error"/>, or <paramref name="originalFailedMessage"/>
    /// when none was recorded.</item>
    /// <item>InProgress / Completed-without-payload (default) ⇒ failure with
    /// <paramref name="inProgressMessage"/>.</item>
    /// </list>
    /// The caller supplies the type-specific deserialize, the replayed-flag
    /// mutation, and every message string so the exact wording and result shape
    /// are preserved per manager.
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
    {
        switch (record.State)
        {
            case IdempotencyState.Completed when !string.IsNullOrEmpty(record.ResultPayload):
                var replayed = deserialize(record.ResultPayload!);
                if (replayed is not null)
                {
                    markReplayed(replayed);
                    return new AZOAResult<T>
                    {
                        Result = replayed,
                        Message = replaySuccessMessage
                    };
                }
                return new AZOAResult<T> { IsError = true, Message = replayDeserializeFailedMessage };

            case IdempotencyState.Failed:
                return new AZOAResult<T>
                {
                    IsError = true,
                    Message = string.IsNullOrEmpty(record.Error) ? originalFailedMessage : record.Error!
                };

            default:
                // InProgress (or Completed with no payload): the original effect
                // is not yet known to have settled. Do NOT re-execute; surface a
                // retryable in-progress state.
                return new AZOAResult<T> { IsError = true, Message = inProgressMessage };
        }
    }
}

namespace AZOA.WebAPI.Sagas;

/// <summary>
/// Deterministic derivation of the stable per-step idempotency key from the
/// saga-instance correlation key + step name. Reusing the api-safety-hardening
/// key convention (a single opaque string the
/// <see cref="AZOA.WebAPI.Interfaces.IIdempotencyStore"/> treats as the
/// uniqueness primitive) — the saga layer does NOT invent a second idempotency
/// mechanism, it only derives a STABLE key so:
/// <list type="bullet">
/// <item>every retry / lease-reclaim of the SAME step yields the SAME key ⇒ a
/// re-run is an idempotent replay, never a duplicate irreversible effect;</item>
/// <item>sibling forward steps and the compensation step get DISTINCT keys ⇒
/// each step's exactly-once guarantee is independent.</item>
/// </list>
/// </summary>
public static class SagaKeys
{
    /// <summary>
    /// <c>saga:{correlationKey}:{stepName}</c> — stable for the lifetime of
    /// that step in that saga instance. The compensation step uses this with
    /// its own (compensation) step name, so it carries its own idempotency key
    /// exactly as the spec requires.
    /// </summary>
    public static string StepIdempotencyKey(string correlationKey, string stepName)
        => $"saga:{correlationKey}:{stepName}";
}

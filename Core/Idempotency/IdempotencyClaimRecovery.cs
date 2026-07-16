using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Core.Idempotency;

/// <summary>Preserves the original exception while best-effort settling an owned claim.</summary>
internal static class IdempotencyClaimRecovery
{
    public static async Task TryFailAsync(
        IIdempotencyStore store,
        string key,
        string safeMessage,
        Exception originalException)
    {
        try
        {
            await store.FailAsync(key, safeMessage, CancellationToken.None);
        }
        catch (Exception recoveryException)
        {
            originalException.Data["AZOA.ClaimRecoveryExceptionType"] =
                recoveryException.GetType().FullName ?? recoveryException.GetType().Name;
        }
    }
}

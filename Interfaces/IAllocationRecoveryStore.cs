namespace AZOA.WebAPI.Interfaces;

/// <summary>Structured pre-value-effect proof and atomic transfer to a refund reservation.</summary>
public interface IAllocationRecoveryStore
{
    /// <summary>Records a failed allocation only while its original claim is in progress and no value effect has begun.</summary>
    Task RecordNoEffectFailureAsync(string key, string safeError, CancellationToken ct);

    /// <summary>Transfers a proven no-effect failed allocation to the refund reservation; ambiguous claims never qualify.</summary>
    Task<bool> TryReserveRefundAfterNoEffectAsync(string key, CancellationToken ct);
}

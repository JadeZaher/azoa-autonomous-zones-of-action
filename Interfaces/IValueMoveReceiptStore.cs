namespace AZOA.WebAPI.Interfaces;

/// <summary>Retains broadcast references independently of an enclosing payout version.</summary>
public interface IValueMoveReceiptStore
{
    /// <summary>Stores prepared signed bytes before submission, retaining the first preparation on races.</summary>
    Task<bool> TryPrepareAsync(string key, string preparedJson, CancellationToken ct, long expectedGeneration = 0);
    /// <summary>Returns the generation advanced only by a proven no-effect transition.</summary>
    Task<long> GetPreparationGenerationAsync(string key, CancellationToken ct);
    /// <summary>Reads the immutable prepared transaction for exact-byte replay.</summary>
    Task<string?> GetPreparedAsync(string key, CancellationToken ct);
    /// <summary>Clears one preparation only after the caller proves its original transaction cannot take effect.</summary>
    Task<bool> TryClearPreparedAsync(string key, string expectedPreparedJson, CancellationToken ct);
}

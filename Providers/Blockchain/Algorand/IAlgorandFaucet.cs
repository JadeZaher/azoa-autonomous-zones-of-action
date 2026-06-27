namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>
/// Dispenses test ALGO from a configured faucet account to a recipient address.
/// Isolates Algorand2 SDK transaction building / signing / submission so the
/// wallet manager stays SDK-agnostic and unit-testable.
/// Only used on dev / test networks — callers must enforce the mainnet guard.
/// </summary>
public interface IAlgorandFaucet
{
    /// <summary>
    /// True when a faucet mnemonic is configured (Blockchain:Faucet:Algorand:Mnemonic).
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Send <paramref name="amountAlgo"/> ALGO from the configured faucet account
    /// to <paramref name="toAddress"/> on the active Algorand network.
    /// Returns the submitted transaction id.
    ///
    /// Idempotent: the implementation derives a deterministic content-addressed
    /// idempotency key from (chain, recipient, amount) so a retried/concurrent
    /// dispense of the same amount to the same address submits on-chain exactly
    /// once and replays the original txid.
    /// </summary>
    Task<string> DispenseAsync(string toAddress, decimal amountAlgo, CancellationToken ct = default);

    /// <summary>
    /// Send <paramref name="amountAlgo"/> ALGO to <paramref name="toAddress"/>,
    /// deduplicated on the caller-supplied <paramref name="idempotencyKey"/>
    /// (e.g. a client <c>Idempotency-Key</c> header). The on-chain payment is
    /// submitted EXACTLY ONCE per key even under concurrent/retried calls; a
    /// duplicate replays the original transaction id, an in-flight original
    /// surfaces a conflict (no re-submit), and a prior failure is replayed
    /// (no blind re-submit). Returns the submitted transaction id.
    /// </summary>
    Task<string> DispenseAsync(
        string toAddress, decimal amountAlgo, string idempotencyKey, CancellationToken ct = default);
}

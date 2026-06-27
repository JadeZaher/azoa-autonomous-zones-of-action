using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Reads the broadcast tx hash + chain type off an <see cref="IBlockchainOperation"/>
/// so a Tier-2 chain-action node handler can forward them on BOTH its success and
/// its FAILURE path. Forwarding on failure is the keystone of reconcile-before-retry:
/// a broadcast-then-confirmation-timeout surfaces as <c>IsError</c> while the tx
/// already landed; discarding the hash there is exactly the double-mint hole the
/// blockchain-recovery-and-portable-wallets track closes
/// (§1.3 — record the hash before confirmation resolves).
///
/// <para>The op exposes no typed tx-hash property — it surfaces via the
/// provider/manager-dependent <see cref="IBlockchainOperation.Parameters"/> bag.
/// "TxHash"/"ChainType" are the canonical keys the BlockchainOperation /
/// <c>ReconciliationService</c> path stamps; the lower-case variants are
/// provider/manager specific. Read candidates defensively, canonical first.</para>
/// </summary>
internal static class ChainOperationOutputs
{
    private static readonly string[] TxHashKeys = { "TxHash", "txHash", "tx_hash", "txId", "tx_id" };
    private static readonly string[] ChainTypeKeys = { "ChainType", "chainType", "chainId", "chain_id" };

    /// <summary>The broadcast tx hash, or null when none was recorded (⇒ the engine parks).</summary>
    public static string? ReadTxHash(IBlockchainOperation? op) => Read(op, TxHashKeys);

    /// <summary>The chain the tx was broadcast to, or null when not recorded.</summary>
    public static string? ReadChainType(IBlockchainOperation? op) => Read(op, ChainTypeKeys);

    private static string? Read(IBlockchainOperation? op, string[] keys)
    {
        if (op is null) return null;
        foreach (var key in keys)
            if (op.Parameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }
}

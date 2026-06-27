namespace AZOA.WebAPI.Models.Quest;

/// <summary>
/// Outcome of a reconciliation re-probe over a run parked in
/// <see cref="QuestRunStatus.AwaitingReconciliation"/> (the P7 re-probe entry point,
/// blockchain-recovery-and-portable-wallets §1.4 / contract §7). For each Failed
/// chain-action execution carrying a broadcast tx hash, the re-probe verifies chain
/// truth and feeds it into <c>ChainActionRecovery</c>:
///
/// <list type="bullet">
/// <item><b>Confirmed</b> → the tx landed: the execution is reconciled to Succeeded
/// (NO re-broadcast) and the parked saga step is un-parked so the DAG advances.</item>
/// <item><b>FailedOnChain</b> → provably failed: the parked step is un-parked so the
/// saga's retry/compensation budget owns the outcome (safe to re-broadcast).</item>
/// <item><b>Pending/Unknown</b> → still indeterminate: the run stays parked,
/// untouched, for the next sweep — NEVER auto-re-broadcast.</item>
/// </list>
/// </summary>
public sealed class QuestReconciliationResult
{
    /// <summary>The run that was re-probed.</summary>
    public Guid RunId { get; set; }

    /// <summary>The run's status AFTER the re-probe (still
    /// <see cref="QuestRunStatus.AwaitingReconciliation"/> when at least one node
    /// remained indeterminate, else <see cref="QuestRunStatus.Running"/> once the
    /// engine resumes the un-parked step).</summary>
    public QuestRunStatus Status { get; set; }

    /// <summary>Nodes whose tx was Confirmed and reconciled to Succeeded (no re-mint).</summary>
    public int ReconciledConfirmed { get; set; }

    /// <summary>Nodes whose tx was FailedOnChain and handed back to retry/compensation.</summary>
    public int ReleasedFailedOnChain { get; set; }

    /// <summary>Nodes still indeterminate (Pending/Unknown) — left parked for the next sweep.</summary>
    public int StillIndeterminate { get; set; }
}

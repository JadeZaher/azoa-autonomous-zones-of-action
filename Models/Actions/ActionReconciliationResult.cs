namespace AZOA.WebAPI.Models.Actions;

/// <summary>
/// Outcome of a reconciliation re-probe over a run parked in
/// <see cref="ActionRunStatus.AwaitingReconciliation"/> (the P7 re-probe entry point,
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
public sealed class ActionReconciliationResult
{
    /// <summary>The run that was re-probed.</summary>
    public Guid RunId { get; set; }

    /// <summary>The run's status AFTER the re-probe (still
    /// <see cref="ActionRunStatus.AwaitingReconciliation"/> when at least one node
    /// remained indeterminate, else <see cref="ActionRunStatus.Running"/> once the
    /// engine resumes the un-parked step).</summary>
    public ActionRunStatus Status { get; set; }

    /// <summary>Nodes whose tx was Confirmed and reconciled to Succeeded (no re-mint).</summary>
    public int ReconciledConfirmed { get; set; }

    /// <summary>Nodes whose tx was FailedOnChain and handed back to retry/compensation.</summary>
    public int ReleasedFailedOnChain { get; set; }

    /// <summary>Nodes still indeterminate (Pending/Unknown) — left parked for the next sweep.</summary>
    public int StillIndeterminate { get; set; }
}

/// <summary>Response of <c>POST /api/action/runs/reconcile-sweep</c>: <c>{ scanned, results }</c>
/// plus failure counts. See Managers/AGENTS.md §parked-run-sweep.</summary>
public sealed class ActionReconciliationSweepResult
{
    /// <summary>Runs parked in AwaitingReconciliation that this pass examined (after the batch cap).</summary>
    public int Scanned { get; set; }

    /// <summary>One re-probe outcome per run that re-probed without faulting, in sweep order.</summary>
    public IReadOnlyList<ActionReconciliationResult> Results { get; set; } = Array.Empty<ActionReconciliationResult>();

    /// <summary>Scanned runs whose re-probe faulted; they stay parked for the next pass.</summary>
    public int Errors { get; set; }

    /// <summary>Runs still parked after this pass and older than the hard-stuck threshold.</summary>
    public int HardStuck { get; set; }
}

// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Services.Lending;

/// <summary>
/// Serializes the read → check → append sequence on ONE loan tracker's ledger, so the
/// over-repayment guard is a decision and not a race. See <c>Services/Lending/AGENTS.md</c>
/// §over-repayment.
/// </summary>
/// <remarks>
/// <para>Without it, two branches of a run recording against the same tracker each read the
/// same outstanding balance, each pass the guard, and each append — the ledger then holds
/// more repayment than principal, and the projection's floor at zero makes it look settled.
/// Holding this gate across the whole sequence makes the check and the write one step.</para>
/// <para><b>Its reach is one process.</b> It cannot serialize two API instances, so it is the
/// first of two defences and never the only one: <see cref="LoanBalanceProjection"/> REPORTS
/// an over-repaid ledger as <see cref="LoanStatus.OverRepaid"/> with a non-zero
/// <see cref="LoanBalanceTotals.Excess"/> instead of flooring the overshoot away. A
/// cross-instance guarantee needs a conditional write on the holon store; until that exists,
/// an over-repaid tracker is at least VISIBLE rather than hidden.</para>
/// <para>Holders are reference-counted and dropped at zero, so a long-lived process does not
/// accumulate one semaphore per loan it has ever touched.</para>
/// </remarks>
public static class LoanLedgerWriteGate
{
    private static readonly Dictionary<Guid, Holder> Holders = new();

    /// <summary>
    /// Waits for exclusive use of <paramref name="loanHolonId"/>'s ledger. Dispose the
    /// returned lease — with <c>using</c> — to hand it to the next caller.
    /// </summary>
    public static async Task<IDisposable> EnterAsync(Guid loanHolonId, CancellationToken ct = default)
    {
        Holder holder;
        lock (Holders)
        {
            if (!Holders.TryGetValue(loanHolonId, out holder!))
                Holders[loanHolonId] = holder = new Holder();

            holder.Claims++;
        }

        try
        {
            await holder.Gate.WaitAsync(ct);
        }
        catch
        {
            Leave(loanHolonId);
            throw;
        }

        return new Lease(loanHolonId, holder);
    }

    /// <summary>Drops one claim, discarding the holder once nobody holds or awaits it.</summary>
    private static void Leave(Guid loanHolonId)
    {
        lock (Holders)
        {
            if (Holders.TryGetValue(loanHolonId, out var holder) && --holder.Claims <= 0)
                Holders.Remove(loanHolonId);
        }
    }

    /// <summary>One tracker's gate plus the number of callers holding or awaiting it.</summary>
    private sealed class Holder
    {
        public readonly SemaphoreSlim Gate = new(1, 1);

        public int Claims;
    }

    /// <summary>Exclusive use of one tracker's ledger, released on <see cref="Dispose"/>.</summary>
    private sealed class Lease : IDisposable
    {
        private readonly Guid _loanHolonId;
        private readonly Holder _holder;
        private bool _released;

        public Lease(Guid loanHolonId, Holder holder)
        {
            _loanHolonId = loanHolonId;
            _holder = holder;
        }

        public void Dispose()
        {
            if (_released)
                return;

            _released = true;
            _holder.Gate.Release();
            Leave(_loanHolonId);
        }
    }
}

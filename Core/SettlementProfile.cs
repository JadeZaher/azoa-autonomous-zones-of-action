// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Core;

/// <summary>
/// Whether — and for how long — a settled movement on a value rail can still be
/// taken back by someone other than its payee.
/// </summary>
public enum SettlementReversibility
{
    /// <summary>
    /// Once the rail reports settlement, no third party can claw the movement back.
    /// A confirmed chain transaction. <see cref="SettlementProfile.ReversalWindow"/>
    /// is <see cref="TimeSpan.Zero"/>.
    /// </summary>
    Irreversible = 0,

    /// <summary>
    /// A counterparty or the rail operator may reverse the movement, but only inside a
    /// bounded window (a card chargeback, an ACH return). Value held against it must
    /// not be released until the window closes.
    /// </summary>
    ReversibleWithinWindow = 1,

    /// <summary>
    /// The rail publishes no bound: a reversal can arrive at any later time. Nothing
    /// may be released on this rail's evidence alone — the release decision has to come
    /// from somewhere else (an indemnity, a manual sign-off).
    /// </summary>
    UnboundedReversal = 2,
}

/// <summary>
/// The settlement characteristics of one value rail (decision D9). Read by escrow /
/// release logic to decide WHEN held value may move on; see <c>Core/AGENTS.md</c>
/// §settlement-profile.
/// </summary>
/// <remarks>
/// Deliberately NOT a boolean <c>IsFinal</c>. "Final" is not a property a rail has —
/// it is a property of a moment in time relative to that rail's reversal window. A
/// chain is effectively irreversible the instant it confirms; a card rail carries a
/// chargeback tail measured in months, during which the money is in the payee's
/// account and is still not theirs. A boolean forces both into one lie, and the lie
/// that costs money is the one that says a card payment is final.
/// <para>Callers ask <see cref="TryGetReleaseTimeUtc"/> for a TIME, not a flag. A rail
/// with <see cref="SettlementReversibility.UnboundedReversal"/> answers "never", which
/// a boolean cannot express at all.</para>
/// </remarks>
public readonly record struct SettlementProfile
{
    private readonly string? _settlementEvidence;

    private SettlementProfile(
        SettlementReversibility reversibility, TimeSpan reversalWindow, string? settlementEvidence)
    {
        Reversibility = reversibility;
        ReversalWindow = reversalWindow;
        _settlementEvidence = settlementEvidence;
    }

    /// <summary>Whether a settled movement on this rail can be taken back, and on what terms.</summary>
    public SettlementReversibility Reversibility { get; }

    /// <summary>
    /// How long after settlement a reversal can still arrive.
    /// <see cref="TimeSpan.Zero"/> for <see cref="SettlementReversibility.Irreversible"/>;
    /// meaningless (and never read) for <see cref="SettlementReversibility.UnboundedReversal"/>.
    /// </summary>
    public TimeSpan ReversalWindow { get; }

    /// <summary>
    /// Human-readable description of what this rail treats as "settled" — the observation
    /// that starts <see cref="ReversalWindow"/>. Operator-facing only: never parsed, never
    /// a decision input.
    /// </summary>
    public string SettlementEvidence => _settlementEvidence ?? string.Empty;

    /// <summary>
    /// Settlement on this rail cannot be undone by a third party once
    /// <paramref name="settlementEvidence"/> is observed.
    /// </summary>
    public static SettlementProfile IrreversibleOnce(string settlementEvidence) =>
        new(SettlementReversibility.Irreversible, TimeSpan.Zero, Describe(settlementEvidence));

    /// <summary>
    /// Settlement can be reversed for <paramref name="reversalWindow"/> after
    /// <paramref name="settlementEvidence"/> is observed (e.g. a card rail's chargeback tail).
    /// </summary>
    public static SettlementProfile ReversibleFor(TimeSpan reversalWindow, string settlementEvidence)
    {
        if (reversalWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(reversalWindow), reversalWindow,
                "A reversible rail must declare a positive window; use IrreversibleOnce for a rail with none.");

        return new(SettlementReversibility.ReversibleWithinWindow, reversalWindow, Describe(settlementEvidence));
    }

    /// <summary>Reversal can arrive at any later time; nothing may be released on this rail's evidence alone.</summary>
    public static SettlementProfile ReversibleWithoutBound(string settlementEvidence) =>
        new(SettlementReversibility.UnboundedReversal, TimeSpan.Zero, Describe(settlementEvidence));

    /// <summary>
    /// The earliest UTC instant at which value held against a movement settled at
    /// <paramref name="settledAtUtc"/> may be released. Returns <c>false</c> — never a
    /// time — for <see cref="SettlementReversibility.UnboundedReversal"/>, which is the
    /// "there is no such instant" answer a boolean finality flag cannot give.
    /// </summary>
    public bool TryGetReleaseTimeUtc(DateTimeOffset settledAtUtc, out DateTimeOffset releaseAtUtc)
    {
        releaseAtUtc = default;
        if (Reversibility == SettlementReversibility.UnboundedReversal)
            return false;

        releaseAtUtc = settledAtUtc + ReversalWindow;
        return true;
    }

    private static string? Describe(string settlementEvidence) =>
        string.IsNullOrWhiteSpace(settlementEvidence) ? null : settlementEvidence.Trim();

    public override string ToString() => Reversibility switch
    {
        SettlementReversibility.Irreversible => $"irreversible once {SettlementEvidence}",
        SettlementReversibility.ReversibleWithinWindow =>
            $"reversible for {ReversalWindow} after {SettlementEvidence}",
        _ => $"reversible without bound after {SettlementEvidence}",
    };
}

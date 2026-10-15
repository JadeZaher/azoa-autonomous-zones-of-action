// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Fiat.Reversal;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>
/// Answers "can this actor RECEIVE settlement on this rail" for one rail. Registered
/// per rail; the gate prefers a registered directory over its default wallet lookup.
/// </summary>
/// <remarks>
/// This is the seam a fiat rail plugs into. A fiat payout destination is a bank account
/// or a card — not a wallet — and lives in its own store; when that store exists it
/// implements this and the gate needs no change. See <c>Services/Actions/AGENTS.md</c>
/// §rail-capability.
/// </remarks>
public interface IRailDestinationDirectory
{
    /// <summary>The rail id this directory answers for. Matched case-insensitively.</summary>
    string RailId { get; }

    /// <summary>
    /// True when <paramref name="userId"/> has a usable destination on this rail.
    /// MUST fail closed: a lookup error is <c>false</c>, never an optimistic <c>true</c>.
    /// </summary>
    Task<bool> HasUsableDestinationAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// The verdict of the pre-execution admission check for ONE value-moving node: allowed, or
/// refused with the exact sentence the run records and the consumer reads.
/// </summary>
/// <remarks>
/// Carrying the sentence rather than a bare <c>bool</c> is what lets one predicate answer two
/// different refusals — "your account is paused" and "you have no destination on this rail" —
/// without either dispatch seam restating a rule it does not own.
/// </remarks>
public readonly record struct ValueNodeAdmission(bool IsAllowed, string RefusalMessage)
{
    /// <summary>The node may run.</summary>
    public static ValueNodeAdmission Allowed { get; } = new(true, string.Empty);

    /// <summary>The node must NOT run; <paramref name="message"/> is recorded verbatim.</summary>
    public static ValueNodeAdmission Refused(string message) => new(false, message);
}

/// <summary>
/// The pre-execution value-rail capability gate (decisions D1 + D9).
///
/// <para>A node handler that declares
/// <see cref="AZOA.WebAPI.Interfaces.ActionExecution.IActionNodeHandler.RequiresChainCapability"/>
/// <c>== true</c> may only run when the run's actor has a usable destination ON THE RAIL
/// THAT NODE WILL SETTLE ON. Both dispatch seams — the legacy synchronous
/// <c>ActionManager</c> executor and the durable <c>ActionNodeStepHandler</c> saga step —
/// call this single gate BEFORE invoking <c>HandleAsync</c>, so the rule cannot be
/// bypassed via the durable path.</para>
/// </summary>
/// <remarks>
/// <para>It replaced a rail-blind check that asked only "does the actor have ANY wallet
/// bound", which was wrong in both directions the moment a second rail existed: an
/// Algorand-wallet holder PASSED a fiat payout they cannot receive, and a fiat-only user
/// — exactly the person who should never see a blockchain — FAILED a pure fiat payout.
/// Scoping the question to one rail fixes both with the same rule.</para>
/// <para><b>Fails closed.</b> A lookup error, an unnamed rail, and an empty result are
/// all "no capability", so a transient database fault can never let a value action
/// broadcast.</para>
/// <para><b>It also carries the account pause (OI-28).</b> The reversal risk flag was a
/// control on three paths only — checkout begin, the on-ramp credit and the bridge — because
/// those are the three that resolve <c>IRealValueKycGate</c>. Not one of the ~45 node handlers
/// did, so a flagged payer could move the disputed units through the workflow engine while the
/// compensation ladder was still walking its rungs. <see cref="AdmitValueNodeAsync"/> is the
/// single predicate BOTH seams now route through, so the pause cannot be honoured by half the
/// engine.</para>
/// <para><b>Why the ledger and not <c>IRealValueKycGate</c>.</b> The pause is only ONE of the two
/// conditions that gate composes: the wrapped inner gate additionally demands current KYC
/// provenance and an AML NO_HIT (<c>Services/Bridge/RealValueKycGate.cs</c>). Resolving the whole
/// gate here would silently make identity verification a precondition of every internal unit move
/// in the workflow engine — a policy nobody decided, and one that refuses the very people the
/// platform is for. So this reads the same flag through the same ledger, refuses with the same
/// sentences (<see cref="RiskHoldValueGate.RiskHoldMessage"/>,
/// <see cref="RiskHoldValueGate.UnreadableMessage"/>) and leaves the identity question to the
/// paths that already ask it.</para>
/// </remarks>
public sealed class ValueRailCapabilityGate
{
    private readonly IWalletManager _walletManager;
    private readonly IReadOnlyDictionary<string, IRailDestinationDirectory> _directories;
    private readonly string _defaultRailId;
    private readonly IFiatReversalLedger? _accountPauseLedger;

    public ValueRailCapabilityGate(
        IWalletManager walletManager,
        string? defaultRailId = null,
        IEnumerable<IRailDestinationDirectory>? destinationDirectories = null,
        // The read side of the account pause. Optional ONLY because a container without the
        // reversal pipeline cannot contain a flag to honour (see IsAccountPauseEnforced);
        // Program.cs registers IFiatReversalLedger unconditionally, so production always has it.
        IFiatReversalLedger? accountPauseLedger = null)
    {
        ArgumentNullException.ThrowIfNull(walletManager);

        _walletManager = walletManager;
        _accountPauseLedger = accountPauseLedger;
        _defaultRailId = string.IsNullOrWhiteSpace(defaultRailId)
            ? BlockchainConfig.DefaultChainFallback
            : defaultRailId.Trim();

        var byRail = new Dictionary<string, IRailDestinationDirectory>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in destinationDirectories ?? [])
        {
            if (directory is null || string.IsNullOrWhiteSpace(directory.RailId))
                continue;

            // Two directories for one rail is a wiring bug, and quietly keeping one of
            // them is how the wrong answer gets returned. Mirrors the provider factory.
            if (!byRail.TryAdd(directory.RailId.Trim(), directory))
                throw new InvalidOperationException(
                    $"Duplicate rail destination directory registered for rail '{directory.RailId}'.");
        }
        _directories = byRail;
    }

    /// <summary>
    /// The exact failure the engine records when a value-moving node is rejected
    /// pre-execution. Names the rail, because "no wallet bound" is not actionable for a
    /// user who was never supposed to have one.
    /// </summary>
    public static string NoDestinationMessage(string railId) =>
        $"value rail capability required: the run actor has no usable '{railId}' destination";

    /// <summary>The rail a node settles on: what its handler declares, else the platform default.</summary>
    public string ResolveRailId(string? declaredRailId) =>
        string.IsNullOrWhiteSpace(declaredRailId) ? _defaultRailId : declaredRailId.Trim();

    /// <summary>
    /// True when this gate can actually honour the account pause — i.e. a reversal ledger was
    /// supplied. False means no reversal pipeline is composed in this container, so no risk flag
    /// can ever have been written for it to read.
    /// </summary>
    public bool IsAccountPauseEnforced => _accountPauseLedger is not null;

    /// <summary>
    /// The ONE pre-execution predicate every value-moving node passes through, at BOTH dispatch
    /// seams. Order is deliberate: the account pause is asked FIRST, so a flagged payer is refused
    /// even where they do hold a usable destination.
    /// </summary>
    /// <remarks>
    /// Both seams call this instead of the two checks separately, because a gate wired at one seam
    /// is not wired: the durable saga path must refuse exactly what the in-process executor refuses.
    /// See <c>Services/Actions/AGENTS.md</c> §rail-capability.
    /// </remarks>
    public async Task<ValueNodeAdmission> AdmitValueNodeAsync(
        Guid actorUserId, string? declaredRailId, CancellationToken ct = default)
    {
        var standing = await CheckAccountStandingAsync(actorUserId, ct);
        if (!standing.IsAllowed)
            return standing;

        if (!await HasUsableDestinationAsync(actorUserId, declaredRailId, ct))
            return ValueNodeAdmission.Refused(NoDestinationMessage(ResolveRailId(declaredRailId)));

        return ValueNodeAdmission.Allowed;
    }

    /// <summary>
    /// The account pause: an account carrying an unresolved reversal flag moves no value, on any
    /// rail, through any node. Reuses <see cref="RiskHoldValueGate"/>'s sentences so the workflow
    /// engine, checkout, the on-ramp and the bridge all say the same thing to the same person.
    /// </summary>
    private async Task<ValueNodeAdmission> CheckAccountStandingAsync(Guid actorUserId, CancellationToken ct)
    {
        // The empty case is resolved HERE rather than left to a default. No ledger means no
        // reversal pipeline in this container, which means no flag has ever been written for it to
        // read — not "assume clear". Guid.Empty is not an account, and the ledger says so too.
        if (_accountPauseLedger is null || actorUserId == Guid.Empty)
            return ValueNodeAdmission.Allowed;

        try
        {
            var flagged = await _accountPauseLedger.HasActiveFlagAsync(actorUserId, ct);
            if (flagged.IsError)
                return ValueNodeAdmission.Refused(RiskHoldValueGate.UnreadableMessage);

            return flagged.Result
                ? ValueNodeAdmission.Refused(RiskHoldValueGate.RiskHoldMessage)
                : ValueNodeAdmission.Allowed;
        }
        catch (Exception)
        {
            // Fail closed, exactly as RiskHoldValueGate does: a ledger that cannot be read is a
            // refusal, never an assumed "no flag". That default is how a control becomes decorative.
            return ValueNodeAdmission.Refused(RiskHoldValueGate.UnreadableMessage);
        }
    }

    /// <summary>
    /// True when <paramref name="userId"/> has a usable destination on
    /// <paramref name="railId"/>.
    /// <para>Resolution order: a directory registered for that rail wins; otherwise the
    /// actor's wallet directory scoped TO THAT RAIL. The scoping is the whole fix — an
    /// unscoped wallet list answers a question nobody asked.</para>
    /// </summary>
    public async Task<bool> HasUsableDestinationAsync(
        Guid userId, string? railId, CancellationToken ct = default)
    {
        var rail = ResolveRailId(railId);

        try
        {
            if (_directories.TryGetValue(rail, out var directory))
                return await directory.HasUsableDestinationAsync(userId, ct);

            return await HasWalletOnRailAsync(userId, rail);
        }
        catch (Exception)
        {
            // Fail closed. A directory that throws is indistinguishable from one that
            // does not know, and neither is permission to move value.
            return false;
        }
    }

    /// <summary>
    /// The default directory: a platform-managed wallet whose chain IS the rail. Both the
    /// query filter and the post-filter are applied — the filter so a real store can scope
    /// server-side, the post-filter so a store that ignores it cannot widen the answer.
    /// </summary>
    private async Task<bool> HasWalletOnRailAsync(Guid userId, string rail)
    {
        // QueryAsync scopes by the authenticated user id argument; ChainType narrows it
        // to the rail this operation will actually settle on.
        var result = await _walletManager.QueryAsync(new WalletQueryRequest { ChainType = rail }, userId);

        if (result.IsError || result.Result is null)
            return false; // fail closed — a lookup failure is "no capability"

        return result.Result.Any(wallet =>
            string.Equals(wallet.ChainType, rail, StringComparison.OrdinalIgnoreCase));
    }
}

// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// Answers "can this actor RECEIVE settlement on this rail" for one rail. Registered
/// per rail; the gate prefers a registered directory over its default wallet lookup.
/// </summary>
/// <remarks>
/// This is the seam a fiat rail plugs into. A fiat payout destination is a bank account
/// or a card — not a wallet — and lives in its own store; when that store exists it
/// implements this and the gate needs no change. See <c>Services/Quest/AGENTS.md</c>
/// §rail-capability.
/// </remarks>
public interface IRailDestinationDirectory
{
    /// <summary>The rail id this directory answers for. Matched case-insensitively.</summary>
    string RailId { get; }

    /// <summary>
    /// True when <paramref name="avatarId"/> has a usable destination on this rail.
    /// MUST fail closed: a lookup error is <c>false</c>, never an optimistic <c>true</c>.
    /// </summary>
    Task<bool> HasUsableDestinationAsync(Guid avatarId, CancellationToken ct = default);
}

/// <summary>
/// The pre-execution value-rail capability gate (decisions D1 + D9).
///
/// <para>A node handler that declares
/// <see cref="AZOA.WebAPI.Interfaces.QuestExecution.IQuestNodeHandler.RequiresChainCapability"/>
/// <c>== true</c> may only run when the run's actor has a usable destination ON THE RAIL
/// THAT NODE WILL SETTLE ON. Both dispatch seams — the legacy synchronous
/// <c>QuestManager</c> executor and the durable <c>QuestNodeStepHandler</c> saga step —
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
/// </remarks>
public sealed class ValueRailCapabilityGate
{
    private readonly IWalletManager _walletManager;
    private readonly IReadOnlyDictionary<string, IRailDestinationDirectory> _directories;
    private readonly string _defaultRailId;

    public ValueRailCapabilityGate(
        IWalletManager walletManager,
        string? defaultRailId = null,
        IEnumerable<IRailDestinationDirectory>? destinationDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(walletManager);

        _walletManager = walletManager;
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
    /// True when <paramref name="avatarId"/> has a usable destination on
    /// <paramref name="railId"/>.
    /// <para>Resolution order: a directory registered for that rail wins; otherwise the
    /// actor's wallet directory scoped TO THAT RAIL. The scoping is the whole fix — an
    /// unscoped wallet list answers a question nobody asked.</para>
    /// </summary>
    public async Task<bool> HasUsableDestinationAsync(
        Guid avatarId, string? railId, CancellationToken ct = default)
    {
        var rail = ResolveRailId(railId);

        try
        {
            if (_directories.TryGetValue(rail, out var directory))
                return await directory.HasUsableDestinationAsync(avatarId, ct);

            return await HasWalletOnRailAsync(avatarId, rail);
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
    private async Task<bool> HasWalletOnRailAsync(Guid avatarId, string rail)
    {
        // QueryAsync scopes by the authenticated avatar id argument; ChainType narrows it
        // to the rail this operation will actually settle on.
        var result = await _walletManager.QueryAsync(new WalletQueryRequest { ChainType = rail }, avatarId);

        if (result.IsError || result.Result is null)
            return false; // fail closed — a lookup failure is "no capability"

        return result.Result.Any(wallet =>
            string.Equals(wallet.ChainType, rail, StringComparison.OrdinalIgnoreCase));
    }
}

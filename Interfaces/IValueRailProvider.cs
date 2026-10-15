// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// Everything AZOA needs from a thing that moves value, whether or not that thing is a
/// blockchain (decision D9). See <c>Interfaces/AGENTS.md</c> §value-rail.
/// </summary>
/// <remarks>
/// <para>The surface is deliberately small: move value, read a balance, ask what became
/// of an operation, and declare a <see cref="SettlementProfile"/>. Everything
/// chain-shaped — contract deploy/call, wrapped mint/burn, faucets, token metadata,
/// capability modules — stays on <see cref="IBlockchainProvider"/>, which extends this.
/// A fiat rail implements this interface and nothing else; every existing chain call
/// site keeps the richer interface it already had.</para>
/// <para>Two parameter names read chain-flavoured (<c>tokenId</c>, <c>txHash</c>) and
/// are kept verbatim so no named-argument call site breaks. Read them rail-generically:
/// <c>tokenId</c> is "which asset", null meaning the rail's own unit of account;
/// <c>txHash</c> is "the reference this rail gave us for the movement". Renaming them,
/// and <see cref="ChainConfirmation"/> with them, is a mechanical follow-up.</para>
/// </remarks>
public interface IValueRailProvider
{
    /// <summary>
    /// Stable id of the rail this provider settles on. For a chain provider this is its
    /// <c>ChainType</c>; a fiat rail names itself (e.g. <c>"Fiat.USD"</c>). Matched
    /// case-insensitively by the factory and by the capability gate.
    /// </summary>
    string RailId { get; }

    /// <summary>The environment this instance is bound to. One instance, one environment.</summary>
    RailEnvironment ActiveEnvironment { get; }

    /// <summary>
    /// How settlement on this rail can be undone, expressed as a window rather than a
    /// finality claim. Escrow/release logic reads this; see <see cref="SettlementProfile"/>.
    /// </summary>
    SettlementProfile SettlementProfile { get; }

    /// <summary>
    /// Binds one previously unbound instance to a single rail environment. The factory
    /// owns this call; a factory-bound provider must refuse external re-binding so a
    /// cached instance cannot be retargeted at another environment mid-flight.
    /// </summary>
    void InitializeRail(ValueRailBinding binding);

    // ─── Read ───

    /// <summary>
    /// The holding at <paramref name="address"/>, in base units, as an exact decimal
    /// string. <paramref name="tokenId"/> selects the asset; null means the rail's own
    /// unit of account. A rail that cannot be reached is an ERROR, never a zero balance.
    /// </summary>
    Task<AZOAResult<string>> GetBalanceAsync(string address, string? tokenId = null, CancellationToken ct = default);

    /// <summary>True when <paramref name="address"/> is a well-formed destination on this rail.</summary>
    Task<AZOAResult<bool>> ValidateAddressAsync(string address, CancellationToken ct = default);

    // ─── Move value ───

    /// <summary>
    /// Moves <paramref name="amount"/> of <paramref name="tokenId"/> from
    /// <paramref name="fromAddress"/> to <paramref name="toAddress"/> and returns the
    /// rail's reference for the movement.
    /// <para><paramref name="amount"/> carries its own <see cref="ValueDimension"/>; a
    /// provider MUST refuse an amount denominated in a unit it does not settle rather
    /// than reinterpret the count.</para>
    /// <para><paramref name="signingContext"/> carries identity only — never key material
    /// — so the provider signs with the right key (per-user custody vs platform authority).</para>
    /// </summary>
    Task<AZOAResult<string>> TransferAsync(
        string tokenId,
        string fromAddress,
        string toAddress,
        RailAmount amount,
        SigningContext? signingContext = null,
        CancellationToken ct = default);

    // ─── Operation status ───

    /// <summary>Rail-specific detail about a previously-submitted movement. Shape varies by rail.</summary>
    Task<AZOAResult<Dictionary<string, object>>> GetTransactionStatusAsync(
        string txHash,
        CancellationToken ct = default);

    /// <summary>
    /// The normalized landed / failed / in-flight / unknown verdict for a previously
    /// submitted movement, so a reconciler can decide advance-vs-retry-vs-wait WITHOUT
    /// re-submitting. A rail that cannot tell "dropped" from "not yet observed" must
    /// answer <see cref="ChainConfirmation.Unknown"/>.
    /// </summary>
    Task<AZOAResult<ChainConfirmation>> GetTransactionConfirmationAsync(
        string txHash,
        CancellationToken ct = default);
}

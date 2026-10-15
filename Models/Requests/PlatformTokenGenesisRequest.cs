// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Operator body for the one-time platform-unit genesis (W3.2, D49). Every field is
/// FROZEN by the first accepted call: the platform unit is an Algorand ASA, and an ASA's
/// total supply, decimals and unit name cannot be edited after creation -- changing one
/// means creating a SECOND asset, not amending the first. So the fields are explicit
/// rather than implicit defaults: an operator who cannot state the definition should not
/// be running genesis.
/// <para>
/// Omitted values fall back to <c>PlatformTokenGenesisDefaults</c>
/// (<c>Services/Reserve/PlatformTokenGenesis.cs</c>), which carries the N-X2 parameters.
/// </para>
/// </summary>
public sealed class PlatformTokenGenesisRequest
{
    /// <summary>Chain the platform unit is created on. Algorand-only today (the ASA module is Algorand-specific).</summary>
    public string? ChainType { get; set; }

    /// <summary>On-chain asset name (Algorand caps this at 32 bytes). Never shown to a sponsor.</summary>
    public string? Name { get; set; }

    /// <summary>On-chain unit name / ticker (Algorand caps this at 8 bytes).</summary>
    public string? UnitName { get; set; }

    /// <summary>
    /// Total supply in BASE units -- the ceiling on everything this node can ever issue.
    /// Not the circulating supply: circulating supply is reserve outflow and starts at 0.
    /// </summary>
    public ulong? Total { get; set; }

    /// <summary>Base-unit scale. 2 keeps every balance a whole number of cents.</summary>
    public int? Decimals { get; set; }

    /// <summary>
    /// The network the operator BELIEVES this node is pointed at ("Devnet" / "Testnet" /
    /// "Mainnet"). When supplied it must equal the node's active network or genesis is
    /// refused. This is the promotion guard: a mainnet definition typed against a node
    /// still wired to devnet fails loudly instead of minting the wrong asset.
    /// </summary>
    public string? ExpectedNetwork { get; set; }

    /// <summary>
    /// Must be <c>true</c>. The operator's explicit acknowledgement that the mint is
    /// irreversible and that supply / decimals / unit name cannot be changed afterwards.
    /// A missing acknowledgement is refused before anything is claimed or broadcast.
    /// </summary>
    public bool ConfirmIrreversible { get; set; }
}

// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Issuer-authorized request to revoke a soulbound credential (D19). The asset is
/// clawed back on chain with an <c>AssetClawback</c> transaction signed by the ASA's
/// clawback authority — the address this codebase already sets on every soulbound
/// mint (<c>AlgorandProvider.CreateSoulboundAsaAsync</c>).
///
/// <para>IDOR note: this DTO deliberately carries NEITHER the holder NOR the issuer.
/// The credential is named by the route asset id; the holder is read from the
/// ownership row (and re-proved against the chain), and the issuer is read from the
/// credential's recorded <c>IssuerUserId</c> and compared with the authenticated
/// caller. A caller-supplied issuer would be the exact authorization inversion D19
/// forbids, so there is no field for one.</para>
/// </summary>
public class CredentialRevokeRequest
{
    /// <summary>
    /// Chain the credential lives on (e.g. "Algorand"). Also the settlement rail the
    /// pre-execution capability gate asks about on the node path. Algorand-only in v1
    /// — clawback is an ASA capability.
    /// </summary>
    public string ChainType { get; set; } = "Algorand";

    /// <summary>
    /// Optional free-text reason recorded on the operation row for the D16 analytics
    /// surface. Never interpreted; AZOA derives no meaning from it.
    /// </summary>
    public string? Reason { get; set; }
}

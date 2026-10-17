// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Requests;

/// <summary>Issuer-authorized revocation request; issuer and holder identity come from confirmed provenance.</summary>
public class CredentialRevokeRequest
{
    [System.Text.Json.Serialization.JsonIgnore]
    public AZOA.WebAPI.Models.Signing.SigningCredentialWitness? AuthenticationWitness { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AdmissionOnly { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public AZOA.WebAPI.Models.Actions.ActionEffectAuthority? ActionAuthority { get; set; }
    /// <summary>Names the provider that owns this credential lifecycle.</summary>
    public string ChainType { get; set; } = "Algorand";
    /// <summary>Identifies the original asset network; retries retain their admitted network when omitted.</summary>
    public string? Network { get; set; }
    public string? ContractAddress { get; set; }
    public AZOA.WebAPI.Models.Blockchain.NftFeeAuthorization? FeeAuthorization { get; set; }

    /// <summary>Authenticated reason persisted with the immutable revocation request.</summary>
    public string? Reason { get; set; }
}

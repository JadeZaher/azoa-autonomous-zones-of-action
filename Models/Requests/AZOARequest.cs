using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Requests;

public class AZOARequest
{
    [System.Text.Json.Serialization.JsonIgnore]
    public AZOA.WebAPI.Models.Signing.SigningCredentialWitness? AuthenticationWitness { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NftAdmissionOnly { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public AZOA.WebAPI.Models.Actions.ActionEffectAuthority? ActionAuthority { get; set; }
    public ProviderType ProviderType { get; set; } = ProviderType.Default;
    public string? IdempotencyKey { get; set; }
    public string? ExpectedPublishedHash { get; set; }
    public AZOA.WebAPI.Models.Blockchain.NftFeeAuthorization? NftFeeAuthorization { get; set; }
    public bool SetGlobally { get; set; }
    public AutoLoadBalanceMode AutoLoadBalanceMode { get; set; } = AutoLoadBalanceMode.Off;
    public List<string> CustomProviderKeys { get; set; } = new();
}

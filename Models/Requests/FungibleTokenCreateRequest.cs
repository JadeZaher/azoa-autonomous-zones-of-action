using System.Text.Json.Serialization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Models.Requests;

/// <summary>Immutable participant-owned fungible creation descriptor.</summary>
public class FungibleTokenCreateRequest
{
    public string Name { get; set; } = string.Empty;
    public string UnitName { get; set; } = string.Empty;
    public string ChainType { get; set; } = "Algorand";
    public ChainNetwork? Network { get; set; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong Total { get; set; }
    public int Decimals { get; set; }
    public Guid? HolonId { get; set; }
    [JsonIgnore] public ActionEffectAuthority? ActionAuthority { get; set; }
    [JsonIgnore] public AZOA.WebAPI.Models.Signing.SigningCredentialWitness? AuthenticationWitness { get; set; }
    /// <summary>Server-set reserve-row owner from the principal (never the body); see Services/Fungible/AGENTS.md §reserve-owner.</summary>
    [JsonIgnore] public Guid? ReserveOwnerTenantId { get; set; }
}

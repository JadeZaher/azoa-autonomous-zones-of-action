// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Requests;

/// <summary>Immutable platform genesis definition and explicit irreversible-definition consent.</summary>
public sealed class PlatformTokenGenesisRequest
{
    [System.Text.Json.Serialization.JsonIgnore]
    public AZOA.WebAPI.Models.Signing.SigningCredentialWitness? AuthenticationWitness { get; set; }
    /// <summary>Chain the platform unit is created on. Algorand-only today (the ASA module is Algorand-specific).</summary>
    public string? ChainType { get; set; }

    /// <summary>On-chain asset name (Algorand caps this at 32 bytes). Never shown to a sponsor.</summary>
    public string? Name { get; set; }

    /// <summary>On-chain unit name / ticker (Algorand caps this at 8 bytes).</summary>
    public string? UnitName { get; set; }

    /// <summary>Exact positive supply in atomic units, serialized as a decimal string.</summary>
    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString | System.Text.Json.Serialization.JsonNumberHandling.WriteAsString)]
    public ulong? Total { get; set; }

    /// <summary>Base-unit scale. 2 keeps every balance a whole number of cents.</summary>
    public int? Decimals { get; set; }

    /// <summary>The network explicitly authorized for this immutable definition.</summary>
    public string? ExpectedNetwork { get; set; }

    /// <summary>Explicit consent to claim the immutable genesis definition.</summary>
    public bool ConfirmIrreversible { get; set; }
}

namespace AZOA.WebAPI.Models.Signing;

/// <summary>Actual node-operator consent restricted to one frozen platform-unit genesis definition.</summary>
public sealed record PlatformGenesisAuthority(int Version, SigningCredentialWitness Credential,
    string PlatformTokenId, string GenesisOperationKey, string DefinitionHash, string ProtocolIdentity);

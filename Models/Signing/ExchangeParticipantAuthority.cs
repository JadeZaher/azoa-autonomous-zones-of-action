namespace AZOA.WebAPI.Models.Signing;

/// <summary>Actual tenant credential and one separately consenting exchange participant.</summary>
public sealed record ExchangeParticipantAuthority(int Version, SigningConsentWitness Tenant,
    Guid ParticipantUserId, DateTimeOffset? ParticipantAuthNotBefore, SigningWalletWitness Wallet,
    SigningGrantWitness Grant);

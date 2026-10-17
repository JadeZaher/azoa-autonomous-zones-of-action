namespace AZOA.WebAPI.Models.Kyb;

/// <summary>Tenant-safe business verification status; provider correlation stays server-side.</summary>
public sealed record KybStatusResponse(
    Guid AttemptId,
    string Status,
    bool Verified,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ReviewedAt,
    int BeneficiaryCount);

/// <summary>A tenant-authorized hosted business verification redirect.</summary>
public sealed record KybSessionResponse(KybStatusResponse Verification, string VerificationUrl, bool Resumed);

/// <summary>Minimal authoritative company evidence and server-only beneficiary linkage.</summary>
public sealed record KybBusinessEvidence(
    string CompanyName,
    string RegistrationNumber,
    string RegistrationCountry,
    IReadOnlyList<KybBeneficiaryLink> Beneficiaries);

/// <summary>Vendor-confirmed participant linkage without personal attributes or bearer URLs.</summary>
public sealed record KybBeneficiaryLink(string ProviderId, string EntityType,
    IReadOnlyList<string> Positions, IReadOnlyList<string> SessionIds);

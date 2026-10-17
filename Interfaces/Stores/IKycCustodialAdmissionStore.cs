using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Commits tenant-driven KYC mutations with current child enrollment and credential authority.</summary>
public interface IKycCustodialAdmissionStore
{
    /// <summary>Creates the attempt and documents only while the captured custody authority is current.</summary>
    Task<AZOAResult<KycSubmission>> CreateCustodialSubmissionAsync(KycSubmission submission,
        IReadOnlyList<KycDocument> documents, TenantCustodialWitness witness, CancellationToken ct = default);

    /// <summary>Attaches documents once under the same current custody fence.</summary>
    Task<AZOAResult<KycSubmission>> AttachCustodialDocumentsAsync(KycSubmission submission,
        IReadOnlyList<KycDocument> documents, TenantCustodialWitness witness, CancellationToken ct = default);

    /// <summary>Retires the observed attempt only while the caller retains custody authority.</summary>
    Task<AZOAResult<KycSubmission>> RetireCustodialAttemptAsync(KycSubmission expected,
        DateTimeOffset now, TenantCustodialWitness witness, CancellationToken ct = default);
}

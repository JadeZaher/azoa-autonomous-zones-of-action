using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealKycStore
{
    /// <inheritdoc/>
    public Task<AZOAResult<KycSubmission>> CreateCustodialSubmissionAsync(KycSubmission submission,
        IReadOnlyList<KycDocument> documents, TenantCustodialWitness witness, CancellationToken ct = default)
        => CustodialMutationAsync(witness, () => CreateSubmissionCoreAsync(submission, documents, witness, ct));

    /// <inheritdoc/>
    public Task<AZOAResult<KycSubmission>> AttachCustodialDocumentsAsync(KycSubmission submission,
        IReadOnlyList<KycDocument> documents, TenantCustodialWitness witness, CancellationToken ct = default)
        => CustodialMutationAsync(witness, () => AttachDocumentsCoreAsync(submission, documents, witness, ct));

    /// <inheritdoc/>
    public Task<AZOAResult<KycSubmission>> RetireCustodialAttemptAsync(KycSubmission expected,
        DateTimeOffset now, TenantCustodialWitness witness, CancellationToken ct = default)
        => CustodialMutationAsync(witness, () => RetireAttemptCoreAsync(expected, now, witness, ct));

    private static IReadOnlyList<SurrealQuery>? CustodyGuards(KycSubmission submission, TenantCustodialWitness? witness)
        => witness is null ? []
            : SurrealRecordGuid.ParseOptional(submission.UserId) != witness.UserId
                || SurrealRecordGuid.ParseOptional(submission.TenantId) != witness.Authority.Credential.ActorUserId
                ? null : TenantCustodialAuthorityFence.Create(witness, AzoaScopes.KycSubmit);

    private static AZOAResult<KycSubmission> CustodyRefusal()
        => AZOAResult<KycSubmission>.FailureWithCode("Current tenant KYC authority changed.", AzoaErrorCodes.Forbidden);

    private static async Task<AZOAResult<KycSubmission>> CustodialMutationAsync(TenantCustodialWitness witness,
        Func<Task<AZOAResult<KycSubmission>>> mutation)
    {
        ArgumentNullException.ThrowIfNull(witness);
        try { return await mutation(); }
        catch (SurrealStatementException exception) when (
            exception.Message.Contains(TenantCustodialAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        { return CustodyRefusal(); }
    }
}

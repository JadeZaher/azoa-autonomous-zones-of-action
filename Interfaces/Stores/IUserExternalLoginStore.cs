// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Auth;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence for the federated <c>(provider, subject) -&gt; user</c> binding.
///
/// <para>Every lookup is scoped by <c>tenantScope</c>. There is deliberately NO
/// method that resolves a binding by email and none that resolves one without a
/// scope: the first is the account pre-hijacking vector, the second is the
/// cross-tenant collapse D1/D23 exist to prevent. Absence of the method is the
/// enforcement — a caller cannot reach for what the interface does not offer.</para>
/// </summary>
public interface IUserExternalLoginStore
{
    /// <summary>
    /// Resolves the user bound to a provider subject WITHIN a uniqueness scope.
    /// Returns a null result (not an error) when no binding exists.
    /// </summary>
    Task<AZOAResult<UserExternalLogin?>> GetBySubjectAsync(
        string tenantScope,
        ExternalAuthProvider provider,
        string subject,
        CancellationToken ct = default);

    /// <summary>Creates a binding, failing when one already exists for the scope+subject.</summary>
    Task<AZOAResult<UserExternalLogin>> CreateAsync(
        UserExternalLogin binding,
        CancellationToken ct = default);

    /// <summary>Every federated binding attached to an user, across providers.</summary>
    Task<AZOAResult<IReadOnlyList<UserExternalLogin>>> GetByUserAsync(
        string userId,
        CancellationToken ct = default);

    /// <summary>Stamps a successful sign-in; best-effort, never fails the login.</summary>
    Task<AZOAResult<bool>> TouchLastLoginAsync(
        string id,
        DateTimeOffset at,
        CancellationToken ct = default);

    /// <summary>
    /// Removes ONE binding, scoped by the owning user so a caller can never unlink
    /// another account's provider. Deletes the link row only; the user is untouched
    /// and stays reachable through its remaining credentials.
    /// </summary>
    Task<AZOAResult<bool>> DeleteAsync(
        string tenantScope,
        ExternalAuthProvider provider,
        string userId,
        CancellationToken ct = default);
}

// SPDX-License-Identifier: UNLICENSED

using System.Reflection;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Credentials;

/// <summary>Outcome of asking the production mint seam to issue a credential from A to B.</summary>
/// <param name="SeamExists">False when no D41 recipient seam exists to call at all.</param>
/// <param name="Outcome">The mint result, when a seam was found and called.</param>
internal sealed record IssuanceAttempt(bool SeamExists, AZOAResult<IUserNFT>? Outcome);

/// <summary>
/// Probes for the D41 two-party mint seam and projects a written ownership row the way
/// the store projects it for the revoke path. Red-phase scaffolding — see
/// <c>tests/AZOA.WebAPI.Tests/Credentials/AGENTS.md</c>.
/// </summary>
internal static class CredentialTrackSeams
{
    private const string MintMethodName = nameof(IUserNFTService.MintUserNFTAsync);

    /// <summary>Names accepted for "the party a credential is issued TO".</summary>
    private static readonly string[] RecipientNameFragments = { "recipient", "holder", "issueto", "issuedto" };

    /// <summary>Names accepted for "the party that ISSUED the credential".</summary>
    private static readonly string[] IssuerNameFragments = { "issuer", "issuedby" };

    internal const string MissingSeamMessage =
        "D41: the soulbound mint seam must let an issuer mint TO a recipient. No such seam exists: "
        + "IUserNFTService.MintUserNFTAsync has no recipient parameter and UserNFTMintModel has no "
        + "writable recipient property, so the minted row's user_id is always the caller and the "
        + "issuer stamped from it is always the holder. Add EITHER a MintUserNFTAsync overload with "
        + "an explicit recipient Guid parameter OR a writable recipient Guid on UserNFTMintModel.";

    internal const string MissingIssuerMessage =
        "D41: the ownership row the mint path writes must carry the ISSUER separately from the holder "
        + "(user_id). No issuer-shaped member exists on the row, so SurrealNftStore has nothing to "
        + "stamp into issuer_user_id except userNFT.UserId — which is the holder, and is exactly the "
        + "inversion D19 forbids.";

    /// <summary>
    /// Mints a soulbound credential as <paramref name="issuerUserId"/> for
    /// <paramref name="recipientUserId"/> through whichever D41 seam exists.
    /// </summary>
    internal static async Task<IssuanceAttempt> IssueSoulboundAsync(
        IUserNFTService service, Guid issuerUserId, Guid recipientUserId, UserNFTMintModel model)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(model);

        var explicitRecipientMint = typeof(IUserNFTService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == MintMethodName && HasRecipientParameter(m));

        if (explicitRecipientMint is not null)
            return new IssuanceAttempt(
                true, await InvokeMintAsync(service, explicitRecipientMint, issuerUserId, recipientUserId, model));

        if (RecipientPropertyOn(typeof(UserNFTMintModel)) is { } recipientProperty)
        {
            recipientProperty.SetValue(model, recipientUserId);
            return new IssuanceAttempt(true, await service.MintUserNFTAsync(issuerUserId, model));
        }

        return new IssuanceAttempt(false, null);
    }

    /// <summary>Reads the issuer recorded on a written ownership row; false when the row carries none.</summary>
    internal static bool TryReadIssuer(IUserNFT row, out Guid issuerUserId)
    {
        ArgumentNullException.ThrowIfNull(row);
        issuerUserId = Guid.Empty;

        var property = GuidProperties(row.GetType())
            .FirstOrDefault(p => NameMatches(p.Name, IssuerNameFragments));
        if (property is null)
            return false;

        issuerUserId = property.GetValue(row) switch
        {
            Guid value => value,
            _ => Guid.Empty,
        };
        return true;
    }

    /// <summary>The writable recipient property on a request model, or null when there is none.</summary>
    internal static PropertyInfo? RecipientPropertyOn(Type type) =>
        GuidProperties(type).FirstOrDefault(p => p.CanWrite && NameMatches(p.Name, RecipientNameFragments));

    /// <summary>
    /// The writable ISSUER property on a request model. Must stay null: a body-supplied issuer is
    /// an authorization bypass, because the issuer is the only input the revoke decision reads.
    /// </summary>
    internal static PropertyInfo? WritableIssuerPropertyOn(Type type) =>
        GuidProperties(type).FirstOrDefault(p => p.CanWrite && NameMatches(p.Name, IssuerNameFragments));

    /// <summary>
    /// Projects a written <c>nft_ownership</c> row onto the credential view the revoke path reads,
    /// mirroring <c>SurrealNftStore.ToCredentialRecord</c>: <c>user_id</c> is the HOLDER,
    /// <c>current_owner</c> is the holder's address, <c>issuer_user_id</c> is the issuer.
    /// </summary>
    internal static CredentialOwnershipRecord ProjectAsTheStoreWould(IUserNFT row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var hasIssuer = TryReadIssuer(row, out var issuerUserId);

        return new CredentialOwnershipRecord
        {
            Id = row.Id,
            HolderUserId = row.UserId,
            HolderAddress = row.CurrentOwner,
            IssuerUserId = hasIssuer && issuerUserId != Guid.Empty ? (Guid?)issuerUserId : null,
            IsSoulbound = row.IsSoulbound,
            ChainType = row.ChainType,
            TokenId = row.TokenId,
        };
    }

    // ── Probe mechanics ───────────────────────────────────────────────────────

    private static bool HasRecipientParameter(MethodInfo method) =>
        method.GetParameters().Any(p => p.ParameterType == typeof(Guid) && NameMatches(p.Name, RecipientNameFragments));

    private static async Task<AZOAResult<IUserNFT>> InvokeMintAsync(
        IUserNFTService service, MethodInfo method, Guid issuerUserId, Guid recipientUserId, UserNFTMintModel model)
    {
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            // Value types are filled with their own default rather than with
            // ParameterInfo.DefaultValue, which is null for `CancellationToken ct = default`
            // and would make Invoke throw on a struct parameter.
            arguments[i] = parameter switch
            {
                { ParameterType: var t } when t == typeof(Guid) =>
                    NameMatches(parameter.Name, RecipientNameFragments) ? recipientUserId : issuerUserId,
                { ParameterType: var t } when t == typeof(UserNFTMintModel) => model,
                { ParameterType: { IsValueType: true } t } => Activator.CreateInstance(t),
                { HasDefaultValue: true } => parameter.DefaultValue,
                _ => null,
            };
        }

        var invoked = method.Invoke(service, arguments)
            ?? throw new InvalidOperationException($"{MintMethodName} returned null instead of a task.");

        if (invoked is not Task<AZOAResult<IUserNFT>> task)
            throw new InvalidOperationException(
                $"{MintMethodName} must return Task<AZOAResult<IUserNFT>>; it returned {invoked.GetType().Name}.");

        return await task;
    }

    private static IEnumerable<PropertyInfo> GuidProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?));

    private static bool NameMatches(string? name, IReadOnlyList<string> fragments)
    {
        if (string.IsNullOrEmpty(name)) return false;
        for (var i = 0; i < fragments.Count; i++)
            if (name.Contains(fragments[i], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// Derives the pseudonymous person key that binds a real human to their users
/// without retaining the raw document identifiers. See
/// <c>Persistence/SurrealDb/Models/AGENTS.md</c> §kyc-identity-binding for the
/// rationale; this type is the only place the derivation recipe exists.
/// </summary>
public interface IKycPersonKeyDeriver
{
    /// <summary>True when a root secret of sufficient length is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Version stamp of the current derivation recipe.</summary>
    int RecipeVersion { get; }

    /// <summary>The node-global uniqueness scope sentinel.</summary>
    string GlobalScope { get; }

    /// <summary>Canonical uniqueness scope for a tenant, or the global sentinel when null.</summary>
    string ScopeFor(Guid? tenantId);

    /// <summary>
    /// Derives the scope-salted person key from a document number and issuing state.
    /// Returns null when the inputs cannot form a real-person identity or the root
    /// secret is unconfigured — callers must fail closed on null, never fall back to
    /// an unsalted or partially-keyed value.
    /// </summary>
    string? DerivePersonKey(string scope, string? documentNumber, string? issuingState);
}

/// <inheritdoc />
public sealed class KycPersonKeyDeriver : IKycPersonKeyDeriver
{
    /// <summary>Node-global sentinel. Not a valid Guid('N'), so it cannot collide with a tenant.</summary>
    public const string GlobalScopeSentinel = "__node_global__";

    /// <summary>Bumping this invalidates every previously derived key; record it on the row.</summary>
    public const int CurrentRecipeVersion = 1;

    /// <summary>Minimum root-secret length; shorter secrets fail closed rather than weaken the key.</summary>
    public const int MinimumSecretLength = 32;

    private static readonly byte[] HkdfSalt =
        System.Text.Encoding.UTF8.GetBytes("azoa.kyc.person-key.hkdf.v1");

    private static readonly byte[] FieldSeparator = [0x1F];

    private readonly KycIdentitySettings _settings;

    public KycPersonKeyDeriver(IOptions<KycSettings> options)
    {
        _settings = options.Value.Identity;
    }

    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(_settings.PersonKeySecret)
            && _settings.PersonKeySecret!.Trim().Length >= MinimumSecretLength;

    public int RecipeVersion => CurrentRecipeVersion;

    public string GlobalScope => GlobalScopeSentinel;

    public string ScopeFor(Guid? tenantId) => ScopeValue(tenantId);

    /// <summary>
    /// The canonical uniqueness scope for a tenant. Static because every table whose
    /// key must be per-tenant — <c>kyc_identity</c> and <c>kyc_screening_monitor</c>
    /// alike — has to compute the SAME value, and a second private copy of this rule
    /// is how two tables end up in different scopes.
    /// </summary>
    public static string ScopeValue(Guid? tenantId)
        => tenantId is null || tenantId.Value == Guid.Empty
            ? GlobalScopeSentinel
            : tenantId.Value.ToString("N");

    public string? DerivePersonKey(string scope, string? documentNumber, string? issuingState)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(scope))
            return null;

        var canonicalDocument = Canonicalize(documentNumber);
        var canonicalState = Canonicalize(issuingState);
        if (canonicalDocument.Length == 0 || canonicalState.Length == 0)
            return null;

        // Per-scope salt. One human verifying under two tenants yields two keys that
        // cannot be correlated without the root secret, so a tenant can never learn
        // that its subject also exists elsewhere on the node.
        var rootSecret = System.Text.Encoding.UTF8.GetBytes(_settings.PersonKeySecret!.Trim());
        var scopeSalt = new byte[32];
        try
        {
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm: rootSecret,
                output: scopeSalt,
                salt: HkdfSalt,
                info: System.Text.Encoding.UTF8.GetBytes(scope));

            // Length-prefixed preimage: a delimiter alone is ambiguous when the
            // delimiter byte can occur inside either field.
            var preimage = BuildPreimage(canonicalDocument, canonicalState);
            var mac = HMACSHA256.HashData(scopeSalt, preimage);
            return Base64UrlNoPadding(mac);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scopeSalt);
            CryptographicOperations.ZeroMemory(rootSecret);
        }
    }

    /// <summary>Deterministic record id for a scope/key pair so upserts converge.</summary>
    public static string RecordId(string scope, string personKey)
    {
        var digest = SHA256.HashData(BuildPreimage(scope, personKey));
        return Convert.ToHexString(digest).ToLowerInvariant()[..32];
    }

    private static byte[] BuildPreimage(string first, string second)
    {
        var firstBytes = System.Text.Encoding.UTF8.GetBytes(first);
        var secondBytes = System.Text.Encoding.UTF8.GetBytes(second);
        var buffer = new byte[4 + firstBytes.Length + FieldSeparator.Length + secondBytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer, firstBytes.Length);
        firstBytes.CopyTo(buffer, 4);
        FieldSeparator.CopyTo(buffer, 4 + firstBytes.Length);
        secondBytes.CopyTo(buffer, 4 + firstBytes.Length + FieldSeparator.Length);
        return buffer;
    }

    /// <summary>
    /// Strips formatting a vendor may or may not include so the same physical
    /// document always produces the same key: trim, upper-case invariantly, and drop
    /// every non-alphanumeric character (spaces, dashes, dots in document numbers).
    /// </summary>
    private static string Canonicalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToUpperInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
                builder.Append(character);
        }

        return builder.ToString();
    }

    private static string Base64UrlNoPadding(byte[] value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

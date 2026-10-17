using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Wtt;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Wormhole;

/// <inheritdoc/>
public sealed class WttStageKeyProtector : IWttStageKeyProtector, IDisposable
{
    private readonly Dictionary<string, byte[]> keys = new(StringComparer.Ordinal);
    private bool disposed;
    /// <inheritdoc/>
    public bool IsConfigured { get; }
    /// <inheritdoc/>
    public string? ConfigurationError { get; }
    public string ActiveKeyId { get; }
    public IReadOnlyCollection<string> AvailableKeyVersions => keys.Keys.ToArray();

    /// <summary>An absent or invalid keyring degrades WTT stage-key use instead of failing host startup.</summary>
    public WttStageKeyProtector(IOptions<WttStageKeyOptions> options)
    {
        var configuration = options.Value;
        ActiveKeyId = configuration.ActiveKeyId;
        try
        {
            if (!ValidVersion(ActiveKeyId) || configuration.Keys is null || configuration.Keys.Count == 0)
                throw new InvalidOperationException("WTT stage encryption requires an active versioned keyring.");
            foreach (var entry in configuration.Keys)
            {
                if (!ValidVersion(entry.Key)) throw new InvalidOperationException("Invalid WTT stage encryption key version.");
                var key = Convert.FromBase64String(entry.Value);
                if (key.Length != 32)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new InvalidOperationException("WTT stage encryption keys must contain exactly 32 bytes.");
                }
                keys.Add(entry.Key, key);
            }
            if (!keys.ContainsKey(ActiveKeyId)) throw new InvalidOperationException("WTT active encryption key version is unavailable.");
            IsConfigured = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            foreach (var key in keys.Values) CryptographicOperations.ZeroMemory(key);
            keys.Clear();
            ConfigurationError = exception.Message;
        }
    }

    /// <inheritdoc/>
    public WttProtectedStageKey Protect(WttStageKeyBinding binding, string publicKey, ReadOnlySpan<byte> seed)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RequireConfigured();
        if (seed.Length != 32) throw new ArgumentException("A protocol stage seed must contain 32 bytes.", nameof(seed));
        var aad = AssociatedData(binding, publicKey, ActiveKeyId);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[32];
        var tag = new byte[16];
        using var cipher = new AesGcm(keys[ActiveKeyId], 16);
        cipher.Encrypt(nonce, seed, ciphertext, tag, aad);
        return new(ActiveKeyId, Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext));
    }

    /// <inheritdoc/>
    public T WithSeed<T>(WttStageKeyBinding binding, string publicKey, WttProtectedStageKey envelope, Func<byte[], T> use)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RequireConfigured();
        if (!keys.TryGetValue(envelope.KeyVersion, out var key))
            throw new InvalidOperationException("A retained WTT stage encryption key version is unavailable; restore it before recovery.");
        var aad = AssociatedData(binding, publicKey, envelope.KeyVersion);
        var nonce = Convert.FromBase64String(envelope.NonceBase64);
        var tag = Convert.FromBase64String(envelope.TagBase64);
        var ciphertext = Convert.FromBase64String(envelope.CiphertextBase64);
        if (nonce.Length != 12 || tag.Length != 16 || ciphertext.Length != 32)
            throw new CryptographicException("Invalid WTT stage key envelope.");
        var plaintext = new byte[32];
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return use(plaintext);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <inheritdoc/>
    public WttProtectedStageKey Rewrap(WttStageKeyBinding binding, string publicKey, WttProtectedStageKey envelope)
        => WithSeed(binding, publicKey, envelope, seed => Protect(binding, publicKey, seed));

    public void Dispose()
    {
        if (disposed) return;
        foreach (var key in keys.Values) CryptographicOperations.ZeroMemory(key);
        keys.Clear();
        disposed = true;
    }

    private void RequireConfigured()
    {
        if (!IsConfigured) throw new InvalidOperationException("WTT stage encryption is unavailable: " + ConfigurationError);
    }

    private static byte[] AssociatedData(WttStageKeyBinding binding, string publicKey, string version)
    {
        if (binding.ActorUserId == Guid.Empty || binding.WalletId == Guid.Empty || binding.TenantId == Guid.Empty
            || binding.Generation < 0 || string.IsNullOrWhiteSpace(binding.ProtocolIdentity)
            || string.IsNullOrWhiteSpace(binding.OperationId) || string.IsNullOrWhiteSpace(binding.StageRole)
            || string.IsNullOrWhiteSpace(binding.SigningScope)
            || string.IsNullOrWhiteSpace(publicKey))
            throw new ArgumentException("Complete immutable stage key identity is required.");
        return System.Text.Encoding.UTF8.GetBytes(CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            domain = "azoa/wtt-stage-key/1", binding, publicKey, keyVersion = version,
        })));
    }

    private static bool ValidVersion(string? value)
        => value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

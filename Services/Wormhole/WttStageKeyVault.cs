using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Claims one protected stage key under live wallet custody, then reuses only the durable winner.</summary>
public sealed class WttStageKeyVault(IWttStageKeyStore store, IWttStageKeyProtector protector, IKeyCustodyService custody)
{
    public Task<AZOAResult<WttStageKeyEnvelope>> ClaimAsync(WttStageKeyBinding binding, SigningContext context,
        Func<byte[], string> derivePublicKey, CancellationToken ct = default)
    {
        if (!protector.IsConfigured) return Task.FromResult(Degraded<WttStageKeyEnvelope>());
        if (!Matches(binding, context)) return Task.FromResult(Denied<WttStageKeyEnvelope>());
        return custody.WithSigningKeyAsync(context, async _ =>
        {
            ct.ThrowIfCancellationRequested();
            var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding));
            var id = CanonicalJson.HashCanonical(json);
            var existing = await store.GetAsync(id, ct);
            if (existing is not null)
            {
                if (existing.BindingJson != json) throw new InvalidOperationException("WTT stage key binding changed.");
                ValidatePublicKey(binding, existing, derivePublicKey);
                return existing;
            }
            var seed = RandomNumberGenerator.GetBytes(32);
            try
            {
                var publicKey = derivePublicKey(seed);
                var encrypted = protector.Protect(binding, publicKey, seed);
                var now = DateTimeOffset.UtcNow;
                var winner = await store.ClaimAsync(new WttStageKeyEnvelope
                {
                    Id = id, BindingJson = json, PublicKey = publicKey, KeyVersion = encrypted.KeyVersion,
                    EnvelopeJson = JsonSerializer.Serialize(encrypted), CreatedAt = now, UpdatedAt = now,
                }, ct);
                ValidatePublicKey(binding, winner, derivePublicKey);
                return winner;
            }
            finally { CryptographicOperations.ZeroMemory(seed); }
        });
    }

    public Task<AZOAResult<T>> UseAsync<T>(WttStageKeyBinding binding, SigningContext context,
        Func<byte[], string> derivePublicKey, Func<byte[], T> use, CancellationToken ct = default)
        => UseCoreAsync(binding, context, derivePublicKey, use, Matches(binding, context), ct);

    /// <summary>Uses an original stage key only under fresh direct custody of the same wallet owner.</summary>
    public Task<AZOAResult<T>> UseCleanupAsync<T>(WttStageKeyBinding binding, SigningContext context,
        Func<byte[], string> derivePublicKey, Func<byte[], T> use, CancellationToken ct = default)
        => UseCoreAsync(binding, context, derivePublicKey, use,
            !context.IsPlatform && context.IsResolvableUserContext && context.UserId == binding.ActorUserId
            && context.WalletId == binding.WalletId && context.ActingTenantId == Guid.Empty && context.GrantorUserId == Guid.Empty
            && context.ChainType == "Solana" && binding.ProtocolIdentity.StartsWith("solana:", StringComparison.Ordinal)
            && binding.StageRole.EndsWith(":payer", StringComparison.Ordinal)
            && binding.StageRole.StartsWith(binding.SigningScope == AzoaScopes.WttSourceSign ? "Source:" : "Target:", StringComparison.Ordinal)
            && context.Scope == AzoaScopes.WttCleanupSign && binding.SigningScope is AzoaScopes.WttSourceSign or AzoaScopes.WttTargetSign, ct);

    private Task<AZOAResult<T>> UseCoreAsync<T>(WttStageKeyBinding binding, SigningContext context,
        Func<byte[], string> derivePublicKey, Func<byte[], T> use, bool authorized, CancellationToken ct)
    {
        if (!protector.IsConfigured) return Task.FromResult(Degraded<T>());
        if (!authorized) return Task.FromResult(Denied<T>());
        return custody.WithSigningKeyAsync(context, async _ =>
        {
            ct.ThrowIfCancellationRequested();
            var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding));
            var saved = await store.GetAsync(CanonicalJson.HashCanonical(json), ct)
                ?? throw new InvalidOperationException("WTT stage key must be durably claimed before signing.");
            if (saved.BindingJson != json) throw new InvalidOperationException("WTT stage key binding changed.");
            return protector.WithSeed(binding, saved.PublicKey, Envelope(saved), seed =>
            {
                if (derivePublicKey(seed) != saved.PublicKey) throw new CryptographicException("WTT stage public key does not match its protected seed.");
                return use(seed);
            });
        });
    }

    private void ValidatePublicKey(WttStageKeyBinding binding, WttStageKeyEnvelope row, Func<byte[], string> derivePublicKey)
        => protector.WithSeed(binding, row.PublicKey, Envelope(row), seed =>
            derivePublicKey(seed) == row.PublicKey ? true : throw new CryptographicException("WTT stage public key does not match its protected seed."));

    private static WttProtectedStageKey Envelope(WttStageKeyEnvelope row)
    {
        var envelope = JsonSerializer.Deserialize<WttProtectedStageKey>(row.EnvelopeJson)
            ?? throw new InvalidOperationException("WTT stage key envelope is missing.");
        if (envelope.KeyVersion != row.KeyVersion) throw new InvalidOperationException("WTT stage encryption key version is inconsistent.");
        return envelope;
    }

    private static bool Matches(WttStageKeyBinding binding, SigningContext context)
        => !context.IsPlatform && context.IsResolvableUserContext && context.UserId == binding.ActorUserId
            && context.WalletId == binding.WalletId && context.ActingTenantId == binding.TenantId.GetValueOrDefault()
            && context.Scope == binding.SigningScope
            && context.Scope is AzoaScopes.WttSourceSign or AzoaScopes.WttTargetSign;

    private AZOAResult<T> Degraded<T>() => new() { IsError = true, Message = "WTT stage encryption is degraded: " + protector.ConfigurationError };

    private static AZOAResult<T> Denied<T>() => new() { IsError = true, Message = "WTT stage key requires matching live source or target wallet custody." };
}

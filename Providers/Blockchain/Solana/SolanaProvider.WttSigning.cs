using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private async Task<AZOAResult<byte[]>> SignWttMessageAsync(byte[] expectedMessage, WttSigningAuthority payer,
        IReadOnlyDictionary<string, WttStageKeyBinding> protectedSigners, WttStageKeyVault vault, CancellationToken ct,
        bool cleanup = false)
    {
        var context = payer.Context;
        if (context.IsPlatform || !context.IsResolvableUserContext || context.WalletId == Guid.Empty
            || context.ChainType != ChainType || (cleanup
                ? context.Scope != AzoaScopes.WttCleanupSign || context.ActingTenantId != Guid.Empty || context.GrantorUserId != Guid.Empty
                    || protectedSigners.Count != 1
                : context.Scope is not (AzoaScopes.WttSourceSign or AzoaScopes.WttTargetSign))
            || protectedSigners.ContainsKey(payer.Address))
            return AZOAResult<byte[]>.Failure("WTT requires the exact participant custody and distinct protected stage signers.");
        var message = SolanaMessageEnvelope.Read(expectedMessage);
        var required = message.RequiredKeys.ToArray();
        if (required.Length != protectedSigners.Count + 1 || required[0].Key != payer.Address
            || required.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != required.Length
            || required.Skip(1).Any(value => !protectedSigners.ContainsKey(value.Key)))
            return AZOAResult<byte[]>.Failure("WTT compiled message signers differ from the consent-bound participant and durable stage keys.");
        foreach (var binding in protectedSigners.Values)
            if (binding.ActorUserId != context.UserId || binding.WalletId != context.WalletId
                || (cleanup ? binding.SigningScope is not (AzoaScopes.WttSourceSign or AzoaScopes.WttTargetSign)
                    : binding.TenantId.GetValueOrDefault() != context.ActingTenantId || binding.SigningScope != context.Scope))
                return AZOAResult<byte[]>.Failure("Protected stage key belongs to another WTT participant.");
        if (_signerFactory?.GetSigner(ChainType) is not IPartialTransactionSigner partial)
            return AZOAResult<byte[]>.Failure("WTT requires detached Solana participant signatures.");

        Task<AZOAResult<byte[]>> SignPayer(byte[] bytes)
        {
            using var material = new SigningKeyMaterial(bytes);
            return Task.FromResult(partial.SignPartial(expectedMessage, payer.Address, material));
        }
        AZOAResult<byte[]> payerSignature;
        if (_custodyService is not null)
            payerSignature = await SignWithCustodyAsync(_custodyService, "prepare WTT stage", context, SignPayer);
        else if (_custodyScopeFactory is not null)
        {
            using var scope = _custodyScopeFactory.CreateScope();
            payerSignature = await SignWithCustodyAsync(scope.ServiceProvider.GetRequiredService<IKeyCustodyService>(), "prepare WTT stage", context, SignPayer);
        }
        else return AZOAResult<byte[]>.Failure("WTT participant custody is unavailable.");
        if (payerSignature.IsError || payerSignature.Result is null) return payerSignature;
        if (!required[0].Verify(expectedMessage, payerSignature.Result))
            return AZOAResult<byte[]>.Failure("WTT payer signature does not authorize the exact compiled message.");
        var signatures = new List<byte[]> { payerSignature.Result };
        foreach (var key in required.Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            var signed = cleanup
                ? await vault.UseCleanupAsync(protectedSigners[key.Key], context, WttSolanaStageSignature.PublicKey,
                    seed => WttSolanaStageSignature.Sign(seed, key.Key, expectedMessage), ct)
                : await vault.UseAsync(protectedSigners[key.Key], context, WttSolanaStageSignature.PublicKey,
                    seed => WttSolanaStageSignature.Sign(seed, key.Key, expectedMessage), ct);
            if (signed.IsError || signed.Result is null) return signed;
            if (!key.Verify(expectedMessage, signed.Result)) return AZOAResult<byte[]>.Failure("WTT stage signature failed canonical message verification.");
            signatures.Add(signed.Result);
        }
        return AZOAResult<byte[]>.Success(SolanaMessageEnvelope.Populate(expectedMessage, signatures));
    }
}

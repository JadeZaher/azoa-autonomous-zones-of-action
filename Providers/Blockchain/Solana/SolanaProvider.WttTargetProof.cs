using System.Buffers.Binary;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private readonly WttProofVerifier? _wttProofVerifier;

    private async Task<WttSolanaTargetProof> ReadWttTargetProofAsync(WttOperationIntent intent, WttVaa vaa,
        string label, int verificationGeneration, CancellationToken ct)
    {
        WttSolanaCoreInstructions.RequireCanonicalVaa(vaa);
        if (label is not ("Transfer" or "Attestation") || verificationGeneration < 0)
            throw new InvalidDataException("Target proof requires an explicit body purpose and verification generation.");
        var deployment = ReadWttSolanaDeployment() ?? throw new InvalidDataException("Pinned WTT target deployment is unavailable.");
        var chain = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(chain, vaa.EmitterChain);
        var postedAddress = WttSolanaCoreInstructions.PostedVaa(deployment.Core, vaa);
        var endpoint = WttSolanaAccounts.Derive(deployment.TokenBridge, chain, Convert.FromHexString(vaa.EmitterAddress));
        var snapshot = await ReadWttSolanaSnapshotAsync(deployment, [postedAddress, endpoint], ct);
        using var scope = _wttProofVerifier is null ? _custodyScopeFactory?.CreateScope() : null;
        var verifier = _wttProofVerifier ?? scope?.ServiceProvider.GetRequiredService<WttProofVerifier>()
            ?? throw new InvalidOperationException("WTT target proof verification is unavailable.");
        if (WttDescriptor(snapshot) != intent.Target || !WttSolanaStateCodec.Endpoint(
                SolanaAccountData.ReadOwned(snapshot.Accounts[1], deployment.TokenBridge.Key, false), vaa.EmitterChain,
                Convert.FromHexString(vaa.EmitterAddress))
            || !verifier.HasFreshFinalizedSnapshot(intent.Target.ProtocolIdentity, snapshot.ChainTime, DateTimeOffset.UtcNow))
            throw new InvalidDataException("Current target deployment or registered emitter differs from the exact source body.");
        if (snapshot.Accounts[0].ValueKind != JsonValueKind.Null)
        {
            var posted = WttSolanaPostedEvidence.PostedVaa(SolanaAccountData.ReadOwned(snapshot.Accounts[0], deployment.Core.Key, false), vaa);
            if (posted.SubmissionTime > snapshot.ChainTime)
                throw new InvalidDataException("Posted-VAA submission time is ahead of its finalized snapshot.");
            return new(label, vaa, [], verificationGeneration, true, posted.SignatureSet);
        }
        var verified = await verifier.VerifyAsync(Convert.ToBase64String(Convert.FromHexString(vaa.EnvelopeHex)),
            new(intent.Target.ProtocolIdentity, intent.Target.CoreBridge, intent.Target.TokenBridge, vaa.BodyHex), this, ct);
        if (verified.IsError || verified.Result is null) throw new InvalidDataException(verified.Message);
        if (verified.Result.GuardianEvidence.GuardianAddresses.Count > 19)
            throw new InvalidDataException("Guardian membership exceeds the pinned Solana verification instruction capacity.");
        return new(label, vaa, verified.Result.GuardianEvidence.GuardianAddresses, verificationGeneration);
    }

    private bool ValidWttTargetIntent(WttOperationIntent intent)
        => intent is { Version: 1, Source: not null, Target: not null, Asset: not null, Amount: not null,
                TargetAuthority.Context: not null, TargetBudget: not null }
            && intent.ActorUserId != Guid.Empty && !string.IsNullOrWhiteSpace(intent.OperationId)
            && intent.Target.ChainType == ChainType && intent.Target.Network == ActiveNetwork && intent.Target.ProtocolChain == 1
            && intent.Source.ProtocolChain is not (0 or 1) && ValidateAddressFormat(intent.TargetAuthority.Address)
            && ValidateAddressFormat(intent.RecipientAddress) && intent.RecipientProtocolAddress is { Length: 64 }
            && intent.TargetAuthority.Context.IsResolvableUserContext && !intent.TargetAuthority.Context.IsPlatform
            && intent.TargetAuthority.Context.UserId == intent.ActorUserId && intent.TargetAuthority.Context.ChainType == ChainType
            && intent.TargetAuthority.Context.ActingTenantId == intent.TenantId.GetValueOrDefault()
            && (intent.TenantId is null || intent.TargetAuthority.Context.GrantorUserId == intent.ActorUserId)
            && intent.TargetAuthority.Context.Scope == AzoaScopes.WttTargetSign;
}

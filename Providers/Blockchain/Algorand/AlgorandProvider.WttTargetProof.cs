using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private sealed record WttAlgoTargetProof(WttVerifiedProof Proof, WttAlgoSnapshot Snapshot,
        string SequenceAddress, bool SequenceExists, bool Consumed);

    private async Task<WttAlgoTargetProof> ReadWttAlgoTargetProofAsync(WttOperationIntent intent, WttVerifiedProof supplied,
        string purpose, CancellationToken ct)
    {
        if (!ValidWttAlgoTargetIntent(intent) || purpose is not ("Attestation" or "Transfer"))
            throw new InvalidDataException("Algorand target proof requires an exact scoped target intent and body purpose.");
        var parsed = purpose == "Transfer" ? WttProofBinding.Transfer(intent, supplied) : WttProofBinding.Attestation(intent, supplied);
        using var scope = _custodyScopeFactory?.CreateScope()
            ?? throw new InvalidOperationException("WTT target verification services are required.");
        var verifier = scope.ServiceProvider.GetRequiredService<WttProofVerifier>();
        var verified = await verifier.VerifyAsync(Convert.ToBase64String(Convert.FromHexString(parsed.EnvelopeHex)),
            new(intent.Target.ProtocolIdentity, intent.Target.CoreBridge, intent.Target.TokenBridge, parsed.BodyHex), this, ct);
        if (verified.IsError || verified.Result is null) throw new InvalidDataException(verified.Message);
        var current = await ReadWttAlgoSnapshotAsync(parsed.GuardianSetIndex, parsed.EmitterChain, parsed.EmitterAddress, ct);
        if (WttAlgoDescriptor(current) != intent.Target || current.Core.Get("vphash"u8)?.Bytes is not { } program
            || !program.AsSpan().SequenceEqual(new global::Algorand.Address(WttAlgorandTargetWire.VerifierAddress).Bytes)
            || !verifier.HasFreshFinalizedSnapshot(intent.Target.ProtocolIdentity, current.Timestamp, DateTimeOffset.UtcNow)
            || !current.Guardians!.Addresses.SequenceEqual(verified.Result.GuardianEvidence.GuardianAddresses)
            || current.Guardians.ExpiresAt != verified.Result.GuardianEvidence.ExpirationUnixSeconds
            || current.CurrentGuardianSet != verified.Result.GuardianEvidence.CurrentGuardianSetIndex
            || current.Guardians.ExpiresAt != 0 && current.Guardians.ExpiresAt <= (ulong)Math.Max(current.Timestamp, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            throw new InvalidDataException("Current target verifier, guardians or physical deployment changed during verification.");
        var address = WttAlgorandTargetWire.SequenceAddress(current.Deployment.Token, parsed);
        var account = await ReadWttAlgorandJsonAsync("/v2/accounts/" + address, ct, allowMissing: true);
        WttAlgorandStorage.Blob? blob = null;
        if (account.ValueKind != JsonValueKind.Undefined)
        {
            if (WttAlgoText(account, "address") != address || WttAlgoUnsigned(account, "round") != current.Round)
                throw new InvalidDataException("Target replay account belongs to another finalized snapshot.");
            blob = WttAlgorandStorage.Read(account, current.Deployment.Token, address, current.Round);
            if (blob is null && !WttAlgorandStorage.IsUnallocated(account, address, current.Round))
                throw new InvalidDataException("Existing target replay account has invalid authority or local storage.");
        }
        var consumed = blob is not null && WttAlgorandReplayBitmap.IsConsumed(blob, UInt64Text.Parse(parsed.Sequence));
        await RequireWttAlgoRoundAsync(current.Round, ct);
        return new(verified.Result, current, address, blob is not null, consumed);
    }

    private bool ValidWttAlgoTargetIntent(WttOperationIntent intent)
        => intent is { Version: 1, Source: not null, Target: not null, Asset: not null, Amount: not null,
                TargetAuthority: not null, TargetBudget: not null }
            && intent.ActorUserId != Guid.Empty && !string.IsNullOrWhiteSpace(intent.OperationId)
            && intent.Target.ChainType == ChainType && intent.Target.Network == ActiveNetwork && intent.Target.ProtocolChain == 8
            && intent.Source.ProtocolChain is 1 or 2 && global::Algorand.Address.IsValid(intent.TargetAuthority.Address)
            && global::Algorand.Address.IsValid(intent.RecipientAddress)
            && intent.RecipientProtocolAddress == Convert.ToHexStringLower(new global::Algorand.Address(intent.RecipientAddress).Bytes)
            && intent.TargetAuthority.Context.IsResolvableUserContext && !intent.TargetAuthority.Context.IsPlatform
            && intent.TargetAuthority.Context.UserId == intent.ActorUserId && intent.TargetAuthority.Context.ChainType == ChainType
            && intent.TargetAuthority.Context.ActingTenantId == intent.TenantId.GetValueOrDefault()
            && (intent.TenantId is null || intent.TargetAuthority.Context.GrantorUserId == intent.ActorUserId)
            && intent.TargetAuthority.Context.Scope == AzoaScopes.WttTargetSign;
}

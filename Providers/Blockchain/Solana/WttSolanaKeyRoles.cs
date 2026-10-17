using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Exact protected key identities, including one shared signature set for a canonical proof generation.</summary>
internal static class WttSolanaKeyRoles
{
    public static IReadOnlyList<WttStageKeyBinding> Source(WttPreparationBinding binding, WttSigningAuthority authority)
    {
        if (binding.Phase != "Source" || binding.Stage is not ("Attest" or "Transfer") || binding.Generation < 0)
            throw new InvalidDataException("Protected source keys require an exact publication stage.");
        return [Key(binding, authority, "Source:" + binding.Stage + ":payer", binding.Generation, AzoaScopes.WttSourceSign),
            Key(binding, authority, "Source:" + binding.Stage + ":message", binding.Generation, AzoaScopes.WttSourceSign)];
    }

    public static IReadOnlyList<WttStageKeyBinding> Verification(WttPreparationBinding binding, WttSigningAuthority authority,
        WttVaa vaa, int verificationGeneration)
    {
        WttSolanaCoreInstructions.RequireCanonicalVaa(vaa);
        if (binding.Phase != "Target" || binding.Generation < 0 || verificationGeneration < 0)
            throw new InvalidDataException("Protected verification keys require an exact target proof generation.");
        var proofIdentity = CanonicalJson.Hash(new { Version = 1, vaa.BodyDigest, vaa.GuardianSetIndex });
        return [Key(binding, authority, "Target:" + binding.Stage + ":payer", binding.Generation, AzoaScopes.WttTargetSign),
            Key(binding, authority, "Target:Verification:" + proofIdentity + ":signature-set", verificationGeneration, AzoaScopes.WttTargetSign)];
    }

    public static IReadOnlyList<WttStageKeyBinding> TargetPayer(WttPreparationBinding binding, WttSigningAuthority authority)
    {
        if (binding.Phase != "Target" || binding.Generation < 0)
            throw new InvalidDataException("Protected target payer requires an exact admitted target stage.");
        return [Key(binding, authority, "Target:" + binding.Stage + ":payer", binding.Generation, AzoaScopes.WttTargetSign)];
    }

    public static bool Exact(IEnumerable<WttStageKeyBinding> actual, IReadOnlyList<WttStageKeyBinding> expected)
    {
        var values = actual.ToArray();
        return expected.Count > 0 && expected.Distinct().Count() == expected.Count && values.Length == expected.Count
            && values.Distinct().Count() == values.Length && values.All(expected.Contains);
    }

    private static WttStageKeyBinding Key(WttPreparationBinding binding, WttSigningAuthority authority, string role, int generation, string scope)
        => new(binding.ProtocolIdentity, binding.ActorUserId, binding.TenantId, authority.Context.WalletId,
            binding.OperationId, role, generation, scope);
}

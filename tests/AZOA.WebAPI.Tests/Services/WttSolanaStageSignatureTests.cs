using AZOA.WebAPI.Providers.Blockchain.Solana;
using Solnet.Wallet;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttSolanaStageSignatureTests
{
    [Fact]
    public void Rfc8032EmptyMessageVectorAndDifferentParticipantRejection()
    {
        var seed = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        var key = new PublicKey(Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a")).Key;
        Assert.Equal(key, WttSolanaStageSignature.PublicKey(seed));
        Assert.Equal(Convert.FromHexString("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"),
            WttSolanaStageSignature.Sign(seed, key, []));
        Assert.Throws<InvalidOperationException>(() => WttSolanaStageSignature.Sign(seed, new PublicKey(new byte[32]).Key, [1]));
        Assert.False(new PublicKey(key).Verify([1], WttSolanaStageSignature.Sign(seed, key, [])));
    }
}

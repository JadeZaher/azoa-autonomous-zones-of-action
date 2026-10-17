using System.Buffers.Binary;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class SolanaMetadataCreationPolicyTests
{
    [Fact]
    public void ReviewedCreationSeparatesPermanentLevyFromMetadataAccountRent()
    {
        var rent = new byte[17]; BinaryPrimitives.WriteUInt64LittleEndian(rent, 3480);
        BinaryPrimitives.WriteDoubleLittleEndian(rent.AsSpan(8), 2); rent[16] = 50;
        var policy = new SolanaMetadataCreationPolicy(new string('a', 64), SolanaMetadataCreationPolicy.SupportedRevision);
        var costs = policy.Costs(rent);
        Assert.Equal(5115600UL, costs.RetainedRent);
        Assert.Equal(10000000UL, costs.ProtocolLevy);
        BinaryPrimitives.WriteDoubleLittleEndian(rent.AsSpan(8), 3);
        Assert.Equal((7673400UL, 14997280UL), policy.Costs(rent));
    }

    [Fact]
    public void UnknownRevisionOrNoncanonicalExecutableCannotInheritCreationConstants()
    {
        var policy = new SolanaMetadataCreationPolicy(new string('a', 64), SolanaMetadataCreationPolicy.SupportedRevision);
        Assert.Throws<InvalidDataException>(() => (policy with { SourceRevision = "main" }).RequireSupported());
        Assert.Throws<InvalidDataException>(() => (policy with { CodeHash = new string('A', 64) }).RequireSupported());
        Assert.Throws<InvalidDataException>(() => (policy with { CodeHash = "" }).RequireSupported());
    }
}

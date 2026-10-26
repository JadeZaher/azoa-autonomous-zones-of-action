using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Providers.Blockchain.Simulated;

public sealed partial class SimulatedBlockchainProvider : IBlockchainProvider
{
    /// <summary>Re-maps module discovery through the D-7 activation pins (see <see cref="ChainModuleActivation"/>).</summary>
    bool IBlockchainProvider.TryGetModule<T>(out T? module) where T : class
        => ChainModuleActivation.TryGetGated(this, _config, out module);
}

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Base;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;

namespace AZOA.WebAPI.Services.Ethereum;

/// <summary>Replays explicitly authorized Ethereum wire after a caller crash.</summary>
public sealed class EthereumPreparationRecoveryWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<EthereumPreparationRecoveryWorker> logger) : BackgroundService
{
    /// <summary>Replays each configured Ethereum network with independent failure isolation.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var chain = new BlockchainConfigurationManager(configuration).GetAvailableChains()
            .SingleOrDefault(item => string.Equals(item.ChainType, "Ethereum", StringComparison.OrdinalIgnoreCase));
        if (chain is null) return;
        using var scope = scopes.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IBlockchainProviderFactory>();
        foreach (var network in Enum.GetValues<ChainNetwork>())
        {
            var enabled = network switch
            {
                ChainNetwork.Devnet => chain.Devnet.IsEnabled,
                ChainNetwork.Testnet => chain.Testnet.IsEnabled,
                ChainNetwork.Mainnet => chain.Mainnet.IsEnabled,
                _ => false,
            };
            if (!enabled) continue;
            ct.ThrowIfCancellationRequested();
            try
            {
                if (factory.GetProvider("Ethereum", network) is IEthereumRecoveryModule recovery)
                    await recovery.ReplayAuthorizedAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Ethereum authorized preparation recovery failed for {Network}", network);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { logger.LogError(exception, "Ethereum authorized preparation recovery failed."); }
        }
    }
}

using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Providers.Blockchain;

/// <summary>Scopes durable native plans independently of provider singleton lifetimes.</summary>
internal static class NativePreparationJournal
{
    public static string Identity(NativeOperationIntent intent, string stage, int generation)
        => DeploymentIdentity.Operation(Guid.Empty, null, $"native:{intent.ChainType}:{intent.Network}:{intent.OperationKey}:{stage}:{generation}");

    public static async Task<T> UseAsync<T>(IServiceScopeFactory? factory, Func<INativePreparationStore, Task<T>> operation)
    {
        if (factory is null) throw new InvalidOperationException("Native preparation requires durable storage.");
        using var scope = factory.CreateScope();
        return await operation(scope.ServiceProvider.GetRequiredService<INativePreparationStore>());
    }
}

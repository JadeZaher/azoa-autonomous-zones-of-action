using System.Diagnostics;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Core.Decorators;

/// <summary>
/// Decorator that automatically records provider health metrics (latency, success/failure)
/// on every storage operation. No provider implementation needs to change.
/// </summary>
public class HealthRecordingProviderDecorator : ProviderDecorator
{
    private readonly IProviderHealthMonitor _healthMonitor;

    public HealthRecordingProviderDecorator(IOASISStorageProvider inner, IProviderHealthMonitor healthMonitor)
        : base(inner)
    {
        _healthMonitor = healthMonitor;
    }

    private async Task<OASISResult<T>> TrackAsync<T>(Func<Task<OASISResult<T>>> operation)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await operation();
            sw.Stop();

            if (result.IsError)
                _healthMonitor.RecordFailure(Inner.ProviderName);
            else
                _healthMonitor.RecordSuccess(Inner.ProviderName, sw.Elapsed.TotalMilliseconds);

            return result;
        }
        catch
        {
            _healthMonitor.RecordFailure(Inner.ProviderName);
            throw;
        }
    }

    public override Task<OASISResult<IAvatar>> LoadAvatarAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadAvatarAsync(id, ct));

    public override Task<OASISResult<IAvatar>> SaveAvatarAsync(IAvatar avatar, CancellationToken ct = default)
        => TrackAsync(() => Inner.SaveAvatarAsync(avatar, ct));

    public override Task<OASISResult<bool>> DeleteAvatarAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.DeleteAvatarAsync(id, ct));

    public override Task<OASISResult<IEnumerable<IAvatar>>> LoadAllAvatarsAsync(CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadAllAvatarsAsync(ct));

    public override Task<OASISResult<IWallet>> LoadWalletAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadWalletAsync(id, ct));

    public override Task<OASISResult<IWallet>> SaveWalletAsync(IWallet wallet, CancellationToken ct = default)
        => TrackAsync(() => Inner.SaveWalletAsync(wallet, ct));

    public override Task<OASISResult<bool>> DeleteWalletAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.DeleteWalletAsync(id, ct));

    public override Task<OASISResult<IEnumerable<IWallet>>> LoadWalletsByAvatarAsync(Guid avatarId, CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadWalletsByAvatarAsync(avatarId, ct));

    public override Task<OASISResult<IEnumerable<IWallet>>> LoadAllWalletsAsync(CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadAllWalletsAsync(ct));

    public override Task<OASISResult<IHolon>> LoadHolonAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadHolonAsync(id, ct));

    public override Task<OASISResult<IHolon>> SaveHolonAsync(IHolon holon, CancellationToken ct = default)
        => TrackAsync(() => Inner.SaveHolonAsync(holon, ct));

    public override Task<OASISResult<bool>> DeleteHolonAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.DeleteHolonAsync(id, ct));

    public override Task<OASISResult<IEnumerable<IHolon>>> LoadAllHolonsAsync(HolonQueryRequest? query = null, CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadAllHolonsAsync(query, ct));

    public override Task<OASISResult<IBlockchainOperation>> LoadBlockchainOperationAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadBlockchainOperationAsync(id, ct));

    public override Task<OASISResult<IBlockchainOperation>> SaveBlockchainOperationAsync(IBlockchainOperation operation, CancellationToken ct = default)
        => TrackAsync(() => Inner.SaveBlockchainOperationAsync(operation, ct));

    public override Task<OASISResult<bool>> DeleteBlockchainOperationAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.DeleteBlockchainOperationAsync(id, ct));

    public override Task<OASISResult<IEnumerable<IBlockchainOperation>>> LoadBlockchainOperationsByAvatarAsync(Guid avatarId, CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadBlockchainOperationsByAvatarAsync(avatarId, ct));

    public override Task<OASISResult<ISTARODK>> LoadSTARODKAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadSTARODKAsync(id, ct));

    public override Task<OASISResult<ISTARODK>> SaveSTARODKAsync(ISTARODK odk, CancellationToken ct = default)
        => TrackAsync(() => Inner.SaveSTARODKAsync(odk, ct));

    public override Task<OASISResult<bool>> DeleteSTARODKAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.DeleteSTARODKAsync(id, ct));

    public override Task<OASISResult<IEnumerable<ISTARODK>>> LoadAllSTARODKsAsync(CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadAllSTARODKsAsync(ct));

    // Quest
    public override Task<OASISResult<Quest>> SaveQuestAsync(Quest quest, CancellationToken ct = default)
        => TrackAsync(() => Inner.SaveQuestAsync(quest, ct));

    public override Task<OASISResult<Quest>> LoadQuestAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadQuestAsync(id, ct));

    public override Task<OASISResult<IEnumerable<Quest>>> LoadQuestsByAvatarAsync(Guid avatarId, CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadQuestsByAvatarAsync(avatarId, ct));

    public override Task<OASISResult<bool>> DeleteQuestAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.DeleteQuestAsync(id, ct));

    public override Task<OASISResult<QuestTemplate>> SaveQuestTemplateAsync(QuestTemplate template, CancellationToken ct = default)
        => TrackAsync(() => Inner.SaveQuestTemplateAsync(template, ct));

    public override Task<OASISResult<QuestTemplate>> LoadQuestTemplateAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadQuestTemplateAsync(id, ct));

    public override Task<OASISResult<IEnumerable<QuestTemplate>>> LoadAllQuestTemplatesAsync(CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadAllQuestTemplatesAsync(ct));

    public override Task<OASISResult<bool>> DeleteQuestTemplateAsync(Guid id, CancellationToken ct = default)
        => TrackAsync(() => Inner.DeleteQuestTemplateAsync(id, ct));

    public override Task<OASISResult<QuestNodeTemplate>> SaveQuestNodeTemplateAsync(QuestNodeTemplate template, CancellationToken ct = default)
        => TrackAsync(() => Inner.SaveQuestNodeTemplateAsync(template, ct));

    public override Task<OASISResult<IEnumerable<QuestNodeTemplate>>> LoadAllQuestNodeTemplatesAsync(CancellationToken ct = default)
        => TrackAsync(() => Inner.LoadAllQuestNodeTemplatesAsync(ct));
}

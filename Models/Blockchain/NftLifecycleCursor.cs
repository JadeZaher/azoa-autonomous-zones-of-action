namespace AZOA.WebAPI.Models.Blockchain;

public sealed record NftLifecycleCursor(Guid Participant, DateTimeOffset Snapshot, int Offset);

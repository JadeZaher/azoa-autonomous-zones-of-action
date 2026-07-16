using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Requests;

public sealed class NodeTreasuryDestinationUpdateRequest
{
    public string Chain { get; set; } = string.Empty;

    public ChainNetwork? Network { get; set; }

    public string Address { get; set; } = string.Empty;

    public long? ExpectedVersion { get; set; }
}

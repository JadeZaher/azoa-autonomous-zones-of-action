using OASIS.WebAPI.Core;

namespace OASIS.WebAPI.Models.Requests;

public class OASISRequest
{
    public ProviderType ProviderType { get; set; } = ProviderType.Default;
    public bool SetGlobally { get; set; }
    public AutoFailOverMode AutoFailOverMode { get; set; } = AutoFailOverMode.Off;
    public AutoReplicationMode AutoReplicationMode { get; set; } = AutoReplicationMode.Off;
    public AutoLoadBalanceMode AutoLoadBalanceMode { get; set; } = AutoLoadBalanceMode.Off;
    public List<string> CustomProviderKeys { get; set; } = new();
}

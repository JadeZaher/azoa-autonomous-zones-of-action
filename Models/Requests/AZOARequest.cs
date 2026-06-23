using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Requests;

public class AZOARequest
{
    public ProviderType ProviderType { get; set; } = ProviderType.Default;
    public bool SetGlobally { get; set; }
    public AutoLoadBalanceMode AutoLoadBalanceMode { get; set; } = AutoLoadBalanceMode.Off;
    public List<string> CustomProviderKeys { get; set; } = new();
}

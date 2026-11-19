using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>
/// Runtime-configurable policy controlling whether publishing an <see cref="ActionDefinition"/>
/// requires explicit node operator approval before transitioning from <see cref="ActionStatus.Draft"/>
/// to <see cref="ActionStatus.Active"/>. Seeded from <c>ActionDefinition:RequireOperatorApprovalForPublish</c>.
/// </summary>
public sealed class ActionPublishApprovalPolicy
{
    private readonly Lock _gate = new();
    private bool _requireOperatorApprovalForPublish;
    private DateTimeOffset _updatedAt;
    private string? _updatedBy;

    public ActionPublishApprovalPolicy(IConfiguration? configuration = null)
    {
        _requireOperatorApprovalForPublish = configuration is not null
            && ActionRunQuotaOptions.FromConfiguration(configuration).RequireOperatorApprovalForPublish;
        _updatedAt = DateTimeOffset.UtcNow;
        _updatedBy = configuration is not null ? "configuration" : "default";
    }

    public bool RequireOperatorApprovalForPublish
    {
        get
        {
            lock (_gate) return _requireOperatorApprovalForPublish;
        }
    }

    public ActionPublishApprovalPolicyView GetView()
    {
        lock (_gate)
            return new ActionPublishApprovalPolicyView(_requireOperatorApprovalForPublish, _updatedAt, _updatedBy);
    }

    public ActionPublishApprovalPolicyView Update(bool requireApproval, string? actor = null)
    {
        lock (_gate)
        {
            _requireOperatorApprovalForPublish = requireApproval;
            _updatedAt = DateTimeOffset.UtcNow;
            _updatedBy = string.IsNullOrWhiteSpace(actor) ? "operator" : actor.Trim();
            return new ActionPublishApprovalPolicyView(_requireOperatorApprovalForPublish, _updatedAt, _updatedBy);
        }
    }
}

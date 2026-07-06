namespace AZOA.WebAPI.Models.Ecosystem;

/// <summary>Reference kind for an <see cref="EcosystemNodeModel"/> attachment.</summary>
public enum EcosystemRefKind
{
    DappSeries,
    StarOdk,
}

/// <summary>Domain view of an ecosystem (the tree root record), Guid-typed.</summary>
public class EcosystemModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid StarOdkId { get; set; }
    public Guid AvatarId { get; set; }
    public string? TargetChain { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate { get; set; }
}

/// <summary>Domain view of a single node in an ecosystem tree, Guid-typed.</summary>
public class EcosystemNodeModel
{
    public Guid Id { get; set; }
    public Guid EcosystemId { get; set; }
    public Guid? ParentNodeId { get; set; }
    public EcosystemRefKind RefKind { get; set; }
    public Guid RefId { get; set; }
    public string? Label { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}

/// <summary>Composed tree: an ecosystem plus its nodes assembled into a
/// parent/children hierarchy for return + rendering. <see cref="EcosystemTreeNode"/>
/// is the recursive node shape.</summary>
public class EcosystemTree
{
    public EcosystemModel Ecosystem { get; set; } = new();
    public List<EcosystemTreeNode> Roots { get; set; } = new();
}

/// <summary>Recursive tree node: a node plus its child nodes.</summary>
public class EcosystemTreeNode
{
    public EcosystemNodeModel Node { get; set; } = new();
    public List<EcosystemTreeNode> Children { get; set; } = new();
}

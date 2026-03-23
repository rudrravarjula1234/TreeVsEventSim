namespace TreeVsEventSim.Models;

/// <summary>
/// Represents a single node in the artifact tree projection.
/// Tree structure is encoded via parentId / childrenIds / descendantIds / ancestorIds,
/// giving O(1) lookups for cascade operations and ancestry queries.
/// </summary>
public sealed class ArtifactNode
{
    public string ArtifactId { get; set; } = string.Empty;
    public ArtifactType ArtifactType { get; set; }
    public string? ParentId { get; set; }
    public int Depth { get; set; }
    public List<string> ChildrenIds { get; set; } = [];
    public List<string> DescendantIds { get; set; } = [];
    public List<string> AncestorIds { get; set; } = [];
    public bool IsLeaf => ChildrenIds.Count == 0;
    public string Title { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string? EffortSaved { get; set; }
    public Dictionary<string, string> AdditionalStats { get; set; } = [];

    public ArtifactNode Clone()
    {
        return new ArtifactNode
        {
            ArtifactId = ArtifactId,
            ArtifactType = ArtifactType,
            ParentId = ParentId,
            Depth = Depth,
            ChildrenIds = [.. ChildrenIds],
            DescendantIds = [.. DescendantIds],
            AncestorIds = [.. AncestorIds],
            Title = Title,
            IsEnabled = IsEnabled,
            EffortSaved = EffortSaved,
            AdditionalStats = new Dictionary<string, string>(AdditionalStats),
        };
    }
}

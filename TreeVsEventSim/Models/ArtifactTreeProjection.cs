namespace TreeVsEventSim.Models;

/// <summary>
/// The fully-reconstructed tree state for a project.
/// The artifactIndex is a flat dictionary (artifactId → ArtifactNode).
/// </summary>
public sealed class ArtifactTreeProjection
{
    public string ProjectId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public Dictionary<string, ArtifactNode> ArtifactIndex { get; set; } = [];
    public int Version { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public TreeStats Stats { get; set; } = new();

    /// <summary>Compute and populate Stats from the current ArtifactIndex.</summary>
    public void RecalculateStats()
    {
        Stats = new TreeStats();
        Stats.TotalArtifacts = ArtifactIndex.Count;

        foreach (var node in ArtifactIndex.Values)
        {
            Stats.MaxDepth = Math.Max(Stats.MaxDepth, node.Depth);

            var typeKey = node.ArtifactType.ToString();
            Stats.ArtifactsByType.TryGetValue(typeKey, out var typeCount);
            Stats.ArtifactsByType[typeKey] = typeCount + 1;

            var depthKey = node.Depth.ToString();
            Stats.ArtifactsByDepth.TryGetValue(depthKey, out var depthCount);
            Stats.ArtifactsByDepth[depthKey] = depthCount + 1;

            if (node.EffortSaved != null
                && double.TryParse(node.EffortSaved, out var effort))
            {
                Stats.EffortSavedByType.TryGetValue(typeKey, out var existing);
                Stats.EffortSavedByType[typeKey] = existing + effort;
            }
        }
    }

    /// <summary>Deep-clone the projection (used by in-memory strategy).</summary>
    public ArtifactTreeProjection Clone()
    {
        var clone = new ArtifactTreeProjection
        {
            ProjectId = ProjectId,
            ClientId = ClientId,
            Version = Version,
            LastUpdated = LastUpdated,
        };
        foreach (var (k, v) in ArtifactIndex)
            clone.ArtifactIndex[k] = v.Clone();
        clone.Stats = Stats;
        return clone;
    }
}

public sealed class TreeStats
{
    public int TotalArtifacts { get; set; }
    public int MaxDepth { get; set; }
    public Dictionary<string, int> ArtifactsByType { get; set; } = [];
    public Dictionary<string, int> ArtifactsByDepth { get; set; } = [];
    public Dictionary<string, double> EffortSavedByType { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> AdditionalStatsByType { get; set; } = [];
}

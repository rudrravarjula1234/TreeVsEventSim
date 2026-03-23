using TreeVsEventSim.Models;

namespace TreeVsEventSim.Simulation;

/// <summary>
/// Generates realistic artifact trees of configurable sizes.
/// Small  ≈   50 nodes
/// Medium ≈  500 nodes
/// Large  ≈ 2000+ nodes
/// </summary>
public static class TreeSimulator
{
    private static readonly string[] Adjectives =
        ["Core", "Enhanced", "Advanced", "Primary", "Secondary", "Unified", "Dynamic", "Smart"];

    private static readonly string[] Nouns =
        ["Auth", "Search", "Reporting", "Dashboard", "Workflow", "Integration", "Portal", "API"];

    public static SimulatedTree Generate(TreeSize size, string clientId = "tenant-001")
    {
        var cfg = GetConfig(size);
        var projectId = Guid.NewGuid().ToString();
        var projection = new ArtifactTreeProjection
        {
            ProjectId = projectId,
            ClientId = clientId,
        };

        var events = new List<(ArtifactNode Node, string? ParentId)>();
        var random = new Random(42); // Fixed seed for reproducibility

        // ── Root ──────────────────────────────────────────────────────────
        var root = MakeNode("pu-root", ArtifactType.ProjectUnderstanding,
            null, "Project Understanding", 0);
        AddNode(projection, root, null, events);

        // ── Capabilities ──────────────────────────────────────────────────
        for (int c = 0; c < cfg.CapabilityCount; c++)
        {
            var cap = MakeNode($"cap-{c}", ArtifactType.Capability,
                root.ArtifactId, $"Capability {c + 1}", 1);
            AddNode(projection, cap, root.ArtifactId, events);
        }

        // ── Epics + Features + UserStories + TestCases ────────────────────
        for (int e = 0; e < cfg.EpicCount; e++)
        {
            var epicId = $"epic-{e}";
            var epic = MakeNode(epicId, ArtifactType.Epic,
                root.ArtifactId, $"Epic {e + 1}: {RandomTitle(random)}", 1);
            AddNode(projection, epic, root.ArtifactId, events);

            for (int f = 0; f < cfg.FeaturesPerEpic; f++)
            {
                var featureId = $"feat-{e}-{f}";
                var feature = MakeNode(featureId, ArtifactType.Feature,
                    epicId, $"Feature {f + 1}: {RandomTitle(random)}", 2);
                AddNode(projection, feature, epicId, events);

                for (int s = 0; s < cfg.StoriesPerFeature; s++)
                {
                    var storyId = $"story-{e}-{f}-{s}";
                    var story = MakeNode(storyId, ArtifactType.UserStory,
                        featureId, $"Story {s + 1}: {RandomTitle(random)}", 3);
                    AddNode(projection, story, featureId, events);

                    for (int t = 0; t < cfg.TestCasesPerStory; t++)
                    {
                        var tcId = $"tc-{e}-{f}-{s}-{t}";
                        var tc = MakeNode(tcId, ArtifactType.TestCase,
                            storyId, $"Test Case {t + 1}", 4);
                        AddNode(projection, tc, storyId, events);
                    }
                }
            }
        }

        projection.RecalculateStats();
        return new SimulatedTree(projectId, clientId, projection, events, size);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AddNode(
        ArtifactTreeProjection proj,
        ArtifactNode node,
        string? parentId,
        List<(ArtifactNode, string?)> events)
    {
        // Build ancestor chain from parent before inserting
        if (parentId != null && proj.ArtifactIndex.TryGetValue(parentId, out var parent))
        {
            node.AncestorIds.Add(parentId);
            node.AncestorIds.AddRange(parent.AncestorIds);

            parent.ChildrenIds.Add(node.ArtifactId);

            // Propagate this new node as a descendant up the ancestor chain
            foreach (var ancestorId in node.AncestorIds)
            {
                if (proj.ArtifactIndex.TryGetValue(ancestorId, out var ancestor))
                    ancestor.DescendantIds.Add(node.ArtifactId);
            }
        }

        proj.ArtifactIndex[node.ArtifactId] = node;
        events.Add((node, parentId));
        proj.Version++;
    }

    private static ArtifactNode MakeNode(
        string id,
        ArtifactType type,
        string? parentId,
        string title,
        int depth,
        bool enabled = true)
    {
        return new ArtifactNode
        {
            ArtifactId = id,
            ArtifactType = type,
            ParentId = parentId,
            Title = title,
            Depth = depth,
            IsEnabled = enabled,
            // AncestorIds and DescendantIds are populated by AddNode
        };
    }

    private static string RandomTitle(Random rng)
    {
        return $"{Adjectives[rng.Next(Adjectives.Length)]} {Nouns[rng.Next(Nouns.Length)]}";
    }

    private static SimulationConfig GetConfig(TreeSize size) => size switch
    {
        TreeSize.Small => new(
            CapabilityCount: 3,
            EpicCount: 5,
            FeaturesPerEpic: 3,
            StoriesPerFeature: 2,
            TestCasesPerStory: 0),

        TreeSize.Medium => new(
            CapabilityCount: 5,
            EpicCount: 10,
            FeaturesPerEpic: 5,
            StoriesPerFeature: 4,
            TestCasesPerStory: 2),

        TreeSize.Large => new(
            CapabilityCount: 8,
            EpicCount: 15,
            FeaturesPerEpic: 8,
            StoriesPerFeature: 5,
            TestCasesPerStory: 3),

        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    private record SimulationConfig(
        int CapabilityCount,
        int EpicCount,
        int FeaturesPerEpic,
        int StoriesPerFeature,
        int TestCasesPerStory);
}

public enum TreeSize { Small, Medium, Large }

/// <summary>
/// The generated tree together with the ordered event log that built it,
/// which is the input the event-sourcing strategy needs for replay.
/// </summary>
public sealed class SimulatedTree(
    string projectId,
    string clientId,
    ArtifactTreeProjection projection,
    List<(ArtifactNode Node, string? ParentId)> addEvents,
    TreeSize size)
{
    public string ProjectId { get; } = projectId;
    public string ClientId { get; } = clientId;
    public ArtifactTreeProjection Projection { get; } = projection;
    public IReadOnlyList<(ArtifactNode Node, string? ParentId)> AddEvents { get; } = addEvents;
    public TreeSize Size { get; } = size;
    public int NodeCount => Projection.ArtifactIndex.Count;

    /// <summary>Pick a random non-root leaf node from the projection.</summary>
    public ArtifactNode RandomLeaf(Random rng)
    {
        var leaves = Projection.ArtifactIndex.Values
            .Where(n => n.IsLeaf && n.ParentId != null)
            .ToList();
        return leaves[rng.Next(leaves.Count)];
    }

    /// <summary>Pick a random non-root internal node that has children.</summary>
    public ArtifactNode RandomInternal(Random rng)
    {
        var internals = Projection.ArtifactIndex.Values
            .Where(n => !n.IsLeaf && n.ParentId != null)
            .ToList();
        return internals[rng.Next(internals.Count)];
    }

    /// <summary>Pick any non-root node.</summary>
    public ArtifactNode RandomNode(Random rng)
    {
        var nodes = Projection.ArtifactIndex.Values
            .Where(n => n.ParentId != null)
            .ToList();
        return nodes[rng.Next(nodes.Count)];
    }
}

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
        return Generate(cfg, size, clientId);
    }

    public static SimulatedTree GenerateExactNodeCount(
        int targetNodeCount,
        string clientId = "tenant-001")
    {
        if (targetNodeCount != 10_000)
            throw new NotSupportedException(
                $"Exact node-count generation currently supports 10000 nodes only. Requested: {targetNodeCount}.");

        var cfg = new SimulationConfig(
            CapabilityCount: 2,
            BusinessProcessCount: 13,
            FlowsPerBusinessProcess: 3,
            EpicsPerBusinessProcess: 5,
            ArchitecturesPerEpic: 2,
            FeaturesPerEpic: 3,
            StoriesPerFeature: 7,
            TestCasesPerStory: 1,
            GitCommitsPerStory: 2);

        return Generate(cfg, TreeSize.Custom, clientId);
    }

    private static SimulatedTree Generate(
        SimulationConfig cfg,
        TreeSize size,
        string clientId)
    {
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

        // ── Level 1: Capabilities ──────────────────────────────────────────
        for (int c = 0; c < cfg.CapabilityCount; c++)
        {
            var cap = MakeNode(
                $"cap-{c}",
                ArtifactType.Capability,
                root.ArtifactId,
                $"Capability {c + 1}: {RandomTitle(random)}",
                1);
            AddNode(projection, cap, root.ArtifactId, events);
        }

        // ── Level 1+: Business Processes and descendants ───────────────────
        for (int bp = 0; bp < cfg.BusinessProcessCount; bp++)
        {
            var processId = $"bp-{bp}";
            var process = MakeNode(
                processId,
                ArtifactType.BusinessProcess,
                root.ArtifactId,
                $"Business Process {bp + 1}: {RandomTitle(random)}",
                1);
            AddNode(projection, process, root.ArtifactId, events);

            for (int flow = 0; flow < cfg.FlowsPerBusinessProcess; flow++)
            {
                var flowNode = MakeNode(
                    $"bpf-{bp}-{flow}",
                    ArtifactType.BusinessProcessFlow,
                    processId,
                    $"Business Process Flow {flow + 1}",
                    2);
                AddNode(projection, flowNode, processId, events);
            }

            for (int epic = 0; epic < cfg.EpicsPerBusinessProcess; epic++)
            {
                var epicId = $"epic-{bp}-{epic}";
                var epicNode = MakeNode(
                    epicId,
                    ArtifactType.Epic,
                    processId,
                    $"Epic {epic + 1}: {RandomTitle(random)}",
                    2);
                AddNode(projection, epicNode, processId, events);

                for (int architecture = 0; architecture < cfg.ArchitecturesPerEpic; architecture++)
                {
                    var architectureNode = MakeNode(
                        $"arch-{bp}-{epic}-{architecture}",
                        ArtifactType.Architecture,
                        epicId,
                        $"Architecture {architecture + 1}",
                        3);
                    AddNode(projection, architectureNode, epicId, events);
                }

                for (int feature = 0; feature < cfg.FeaturesPerEpic; feature++)
                {
                    var featureId = $"feat-{bp}-{epic}-{feature}";
                    var featureNode = MakeNode(
                        featureId,
                        ArtifactType.Feature,
                        epicId,
                        $"Feature {feature + 1}: {RandomTitle(random)}",
                        3);
                    AddNode(projection, featureNode, epicId, events);

                    for (int story = 0; story < cfg.StoriesPerFeature; story++)
                    {
                        var storyId = $"story-{bp}-{epic}-{feature}-{story}";
                        var storyNode = MakeNode(
                            storyId,
                            ArtifactType.UserStory,
                            featureId,
                            $"Story {story + 1}: {RandomTitle(random)}",
                            4);
                        AddNode(projection, storyNode, featureId, events);

                        for (int testCase = 0; testCase < cfg.TestCasesPerStory; testCase++)
                        {
                            var testCaseId = $"tc-{bp}-{epic}-{feature}-{story}-{testCase}";
                            var testCaseNode = MakeNode(
                                testCaseId,
                                ArtifactType.TestCase,
                                storyId,
                                $"Test Case {testCase + 1}",
                                5);
                            AddNode(projection, testCaseNode, storyId, events);

                            var generatedArtifactId =
                                $"gen-{bp}-{epic}-{feature}-{story}-{testCase}";
                            var generatedArtifactNode = MakeNode(
                                generatedArtifactId,
                                ArtifactType.GeneratedArtifact,
                                testCaseId,
                                $"Generated Artifact {testCase + 1}",
                                6);
                            AddNode(projection, generatedArtifactNode, testCaseId, events);
                        }

                        for (int commit = 0; commit < cfg.GitCommitsPerStory; commit++)
                        {
                            var commitId = $"git-{bp}-{epic}-{feature}-{story}-{commit}";
                            var commitNode = MakeNode(
                                commitId,
                                ArtifactType.GitCommit,
                                storyId,
                                $"Git Commit {commit + 1}",
                                5);
                            AddNode(projection, commitNode, storyId, events);

                            var adoPushId = $"ado-{bp}-{epic}-{feature}-{story}-{commit}";
                            var adoPushNode = MakeNode(
                                adoPushId,
                                ArtifactType.AdoPush,
                                commitId,
                                $"ADO Push {commit + 1}",
                                6);
                            AddNode(projection, adoPushNode, commitId, events);
                        }
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
            CapabilityCount: 2,
            BusinessProcessCount: 2,
            FlowsPerBusinessProcess: 1,
            EpicsPerBusinessProcess: 2,
            ArchitecturesPerEpic: 1,
            FeaturesPerEpic: 2,
            StoriesPerFeature: 2,
            TestCasesPerStory: 1,
            GitCommitsPerStory: 1),

        TreeSize.Medium => new(
            CapabilityCount: 4,
            BusinessProcessCount: 4,
            FlowsPerBusinessProcess: 2,
            EpicsPerBusinessProcess: 3,
            ArchitecturesPerEpic: 1,
            FeaturesPerEpic: 2,
            StoriesPerFeature: 3,
            TestCasesPerStory: 2,
            GitCommitsPerStory: 1),

        TreeSize.Large => new(
            CapabilityCount: 6,
            BusinessProcessCount: 6,
            FlowsPerBusinessProcess: 3,
            EpicsPerBusinessProcess: 4,
            ArchitecturesPerEpic: 1,
            FeaturesPerEpic: 3,
            StoriesPerFeature: 4,
            TestCasesPerStory: 2,
            GitCommitsPerStory: 2),

        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    private record SimulationConfig(
        int CapabilityCount,
        int BusinessProcessCount,
        int FlowsPerBusinessProcess,
        int EpicsPerBusinessProcess,
        int ArchitecturesPerEpic,
        int FeaturesPerEpic,
        int StoriesPerFeature,
        int TestCasesPerStory,
        int GitCommitsPerStory);
}

public enum TreeSize { Small, Medium, Large, Custom }

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

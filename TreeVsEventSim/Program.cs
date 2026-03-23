using Microsoft.Extensions.Configuration;
using Spectre.Console;
using TreeVsEventSim.Benchmarks;
using TreeVsEventSim.Models;
using TreeVsEventSim.Reporting;
using TreeVsEventSim.Simulation;
using TreeVsEventSim.Strategies;
using TreeVsEventSim.Strategies.EventSourcing;
using TreeVsEventSim.Strategies.MaterializedDocument;
using TreeVsEventSim.Strategies.Neo4j;
using TreeVsEventSim.Visualization;

// ── Configuration ─────────────────────────────────────────────────────────────

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("BENCH_")
    .AddCommandLine(args)
    .Build();

var mongoConnStr = config["MongoDB:ConnectionString"] ?? "mongodb://localhost:27017";
var neo4jUri     = config["Neo4j:Uri"]              ?? "bolt://localhost:7687";
var neo4jUser    = config["Neo4j:User"]             ?? "neo4j";
var neo4jPassword= config["Neo4j:Password"]         ?? "password";
var neo4jEncrypted = bool.TryParse(config["Neo4j:Encrypted"], out var enc) && enc;
var warmupRuns = int.TryParse(config["Benchmark:WarmupIterations"], out var warmups)
    ? warmups
    : 3;
var measureRuns = int.TryParse(config["Benchmark:MeasureIterations"], out var measures)
    ? measures
    : 20;
var verbose      = bool.Parse(config["Benchmark:Verbose"] ?? "true");
var visualizeOnly = bool.TryParse(config["Visualization:Enabled"], out var vis) && vis;
var onlySingleTreeSize = bool.TryParse(
    config["Visualization:OnlySingleTreeSize"], out var singleOnly) && singleOnly;
var singleTreeSizeRaw = config["Visualization:SingleTreeSize"] ?? "Small";
var snapshotMaxItems = int.TryParse(
    config["Visualization:SnapshotMaxItems"], out var snapMax) && snapMax > 0
    ? snapMax
    : 8;
var crossProjectEnabled = bool.TryParse(
    config["Benchmark:CrossProject:Enabled"], out var crossEnabled) && crossEnabled;
var crossProjectCount = int.TryParse(
    config["Benchmark:CrossProject:ProjectCount"], out var projectCount) && projectCount > 0
    ? projectCount
    : 3;
var crossProjectNodesPerProject = int.TryParse(
    config["Benchmark:CrossProject:NodesPerProject"], out var nodesPerProject) && nodesPerProject > 0
    ? nodesPerProject
    : 10_000;
var crossProjectClientId = config["Benchmark:CrossProject:ClientId"] ?? "tenant-001";
var crossProjectArtifactType = Enum.TryParse<ArtifactType>(
    config["Benchmark:CrossProject:SearchArtifactType"],
    ignoreCase: true,
    out var parsedArtifactType)
    ? parsedArtifactType
    : ArtifactType.UserStory;
var effectiveVisualizeOnly = visualizeOnly && !crossProjectEnabled;

// Which tree sizes to run (default: all three)
var rawSizes = config.GetSection("Benchmark:TreeSizes").Get<string[]>()
    ?? (string[])["Small", "Medium", "Large"];
var treeSizes = rawSizes
    .Select(s => Enum.Parse<TreeSize>(s, ignoreCase: true))
    .ToArray();

if (onlySingleTreeSize)
{
    var singleTreeSize = Enum.TryParse<TreeSize>(singleTreeSizeRaw, true, out var parsed)
        ? parsed
        : TreeSize.Small;
    treeSizes = [singleTreeSize];
}

// ── Banner ────────────────────────────────────────────────────────────────────

AnsiConsole.Write(new FigletText("TreeVsEventSim").Color(Color.Cyan1));
AnsiConsole.MarkupLine(
    "[bold]Artifact Tree Storage Benchmarking App[/]\n" +
    "[grey]Compares Event Sourcing (MongoDB), Materialized Document (MongoDB), " +
    "and Neo4j Graph Database[/]\n");

AnsiConsole.MarkupLine($"[grey]MongoDB:[/]  {mongoConnStr}");
AnsiConsole.MarkupLine($"[grey]Neo4j:  [/]  {neo4jUri}  (user: {neo4jUser})");
AnsiConsole.MarkupLine($"[grey]Sizes:  [/]  {string.Join(", ", treeSizes)}");
AnsiConsole.MarkupLine($"[grey]Mode:   [/]  {(crossProjectEnabled ? "Cross-project benchmark" : effectiveVisualizeOnly ? "Visualize-only" : "Benchmark")}");
if (crossProjectEnabled)
{
    AnsiConsole.MarkupLine(
        $"[grey]Cross-project:[/] {crossProjectCount} projects x {crossProjectNodesPerProject:N0} nodes, search {crossProjectArtifactType}");
}
AnsiConsole.WriteLine();

// ── Strategy initialization ───────────────────────────────────────────────────

var strategies = new List<IStorageStrategy>
{
    new EventSourcingStrategy(mongoConnStr),
    new MaterializedDocumentStrategy(mongoConnStr),
    new Neo4jStrategy(neo4jUri, neo4jUser, neo4jPassword, neo4jEncrypted),
};

await AnsiConsole.Status()
    .StartAsync("Initializing backends...", async ctx =>
    {
        ctx.Spinner(Spinner.Known.Dots);
        await Task.WhenAll(strategies.Select(s => s.InitializeAsync()));
    });

// Report Neo4j availability
var neo4jStrategy = (Neo4jStrategy)strategies[2];
if (!neo4jStrategy.IsAvailable)
{
    AnsiConsole.MarkupLine(
        "[yellow]⚠  Neo4j is not reachable — Neo4j results will be marked N/A.[/]");
    if (!string.IsNullOrWhiteSpace(neo4jStrategy.AvailabilityError))
    {
        AnsiConsole.MarkupLine(
            $"[grey]   Reason:[/] {Markup.Escape(neo4jStrategy.AvailabilityError)}");
    }
    AnsiConsole.MarkupLine(
        "[grey]   Start Neo4j locally: " +
        "docker run -p 7474:7474 -p 7687:7687 -e NEO4J_AUTH=neo4j/password neo4j:5[/]\n");
}
else
{
    AnsiConsole.MarkupLine("[green]✓  Neo4j connected.[/]\n");
}

if (effectiveVisualizeOnly)
{
    var visualizationService = new StorageVisualizationService();
    await visualizationService.RunAsync(strategies, treeSizes, snapshotMaxItems);

    foreach (var strategy in strategies)
        await strategy.DisposeAsync();

    AnsiConsole.MarkupLine("[bold green]✓  Visualization complete.[/]");
    return;
}

if (crossProjectEnabled)
{
    var crossProjectResults = await RunCrossProjectBenchmarkAsync(
        strategies,
        crossProjectClientId,
        crossProjectCount,
        crossProjectNodesPerProject,
        crossProjectArtifactType,
        warmupRuns,
        measureRuns);

    ResultsReporter.PrintSummary(crossProjectResults);
    ResultsReporter.PrintAnalysis(crossProjectResults);

    foreach (var strategy in strategies)
        await strategy.DisposeAsync();

    AnsiConsole.MarkupLine("[bold green]✓  Cross-project benchmark complete.[/]");
    return;
}

// ── Benchmark loop ────────────────────────────────────────────────────────────

var allResults = new List<OperationResult>();

foreach (var size in treeSizes)
{
    AnsiConsole.Write(new Rule($"[bold cyan]Generating {size} tree...[/]").RuleStyle("cyan"));

    var tree = TreeSimulator.Generate(size);
    AnsiConsole.MarkupLine(
        $"  Nodes: [white]{tree.NodeCount:N0}[/]  " +
        $"ProjectId: [grey]{tree.ProjectId}[/]");
    AnsiConsole.WriteLine();

    foreach (var strategy in strategies)
    {
        // Skip Neo4j benchmarks if backend is unavailable
        if (strategy is Neo4jStrategy { IsAvailable: false })
        {
            AnsiConsole.MarkupLine(
                $"[dim]  Skipped {Markup.Escape(strategy.Name)} (Neo4j not reachable)[/]");
            continue;
        }

        AnsiConsole.MarkupLine(
            $"[bold]Running {Markup.Escape(strategy.Name)}[/] — " +
            $"[grey]{Markup.Escape(strategy.Description)}[/]");

        var results = new List<OperationResult>();

        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(
                    $"[green]{Markup.Escape(strategy.Name)}[/] ({size})", maxValue: 13);

                await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);

                // 1. WriteTree ─────────────────────────────────────────────────
                results.Add(await MeasureOpAsync("WriteTree", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () =>
                    {
                        await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);
                        await strategy.WriteTreeAsync(tree);
                    },
                    measure: async () =>
                    {
                        await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);
                        await strategy.WriteTreeAsync(tree);
                    }));
                task.Increment(1);

                // Seed data for read / mutation benchmarks
                await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);
                await strategy.WriteTreeAsync(tree);
                var storageSize = await strategy.GetStorageSizeBytesAsync(
                    tree.ClientId, tree.ProjectId);

                var rng          = new Random(456);
                var targetNode   = tree.RandomNode(rng);
                var internalNode = tree.RandomInternal(rng);
                var leafNode     = tree.RandomLeaf(rng);

                // 2. ReadFullTree ───────────────────────────────────────────────
                results.Add(await MeasureOpAsync("ReadFullTree", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () =>
                        await strategy.ReadFullTreeAsync(tree.ClientId, tree.ProjectId),
                    measure: async () =>
                        await strategy.ReadFullTreeAsync(tree.ClientId, tree.ProjectId),
                    storageSize));
                task.Increment(1);

                // 3. GetSingleNode ──────────────────────────────────────────────
                results.Add(await MeasureOpAsync("GetSingleNode", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () => await strategy.GetSingleNodeAsync(
                        tree.ClientId, tree.ProjectId, targetNode.ArtifactId),
                    measure: async () => await strategy.GetSingleNodeAsync(
                        tree.ClientId, tree.ProjectId, targetNode.ArtifactId),
                    storageSize));
                task.Increment(1);

                // 4. GetChildren ───────────────────────────────────────────────
                results.Add(await MeasureOpAsync("GetChildren", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () => await strategy.GetChildrenAsync(
                        tree.ClientId, tree.ProjectId, internalNode.ArtifactId),
                    measure: async () => await strategy.GetChildrenAsync(
                        tree.ClientId, tree.ProjectId, internalNode.ArtifactId),
                    storageSize));
                task.Increment(1);

                // 5. GetChildrenByParentId ────────────────────────────────────
                var parentIdForChildrenLookup = internalNode.ArtifactId;
                results.Add(await MeasureOpAsync("GetChildrenByParentId", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () => await strategy.GetChildrenAsync(
                        tree.ClientId, tree.ProjectId, parentIdForChildrenLookup),
                    measure: async () => await strategy.GetChildrenAsync(
                        tree.ClientId, tree.ProjectId, parentIdForChildrenLookup),
                    storageSize));
                task.Increment(1);

                // 6. GetParent ─────────────────────────────────────────────────
                results.Add(await MeasureOpAsync("GetParent", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () => await strategy.GetParentAsync(
                        tree.ClientId, tree.ProjectId, targetNode.ArtifactId),
                    measure: async () => await strategy.GetParentAsync(
                        tree.ClientId, tree.ProjectId, targetNode.ArtifactId),
                    storageSize));
                task.Increment(1);

                // 7. GetParentsWithNoChildrenOfType ───────────────────────────
                const ArtifactType childTypeForParentFilter = ArtifactType.TestCase;
                results.Add(await MeasureOpAsync("GetParentsWithNoChildrenOfType", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () =>
                    {
                        await CountParentsWithNoChildrenOfTypeAsync(
                            strategy,
                            tree.ClientId,
                            tree.ProjectId,
                            childTypeForParentFilter);
                    },
                    measure: async () =>
                    {
                        await CountParentsWithNoChildrenOfTypeAsync(
                            strategy,
                            tree.ClientId,
                            tree.ProjectId,
                            childTypeForParentFilter);
                    },
                    storageSize));
                task.Increment(1);

                // 8. AddArtifact ───────────────────────────────────────────────
                int addSeq = 0;
                var addParentId = leafNode.ParentId!;
                results.Add(await MeasureOpAsync("AddArtifact", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () => await strategy.AddArtifactAsync(
                        tree.ClientId, tree.ProjectId,
                        $"add-w-{addSeq++}", ArtifactType.TestCase, addParentId, "Warmup"),
                    measure: async () => await strategy.AddArtifactAsync(
                        tree.ClientId, tree.ProjectId,
                        $"add-m-{addSeq++}", ArtifactType.TestCase, addParentId, "Measure"),
                    storageSize));
                task.Increment(1);

                // 9. AddChildrenOfType ────────────────────────────────────────
                const ArtifactType addChildType = ArtifactType.IntegrationTestCase;
                int addTypedSeq = 0;
                results.Add(await MeasureOpAsync("AddChildrenOfType", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () => await strategy.AddArtifactAsync(
                        tree.ClientId, tree.ProjectId,
                        $"add-type-w-{addTypedSeq++}", addChildType, addParentId, "Warmup Typed Child"),
                    measure: async () => await strategy.AddArtifactAsync(
                        tree.ClientId, tree.ProjectId,
                        $"add-type-m-{addTypedSeq++}", addChildType, addParentId, "Measure Typed Child"),
                    storageSize));
                task.Increment(1);

                // 10. DeleteArtifact ───────────────────────────────────────────
                int delSeq = 0;
                results.Add(await MeasureOpAsync("DeleteArtifact", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () =>
                    {
                        var id = $"del-w-{delSeq++}";
                        await strategy.AddArtifactAsync(
                            tree.ClientId, tree.ProjectId,
                            id, ArtifactType.TestCase, addParentId, "Del");
                        await strategy.DeleteArtifactAsync(
                            tree.ClientId, tree.ProjectId, id, []);
                    },
                    measure: async () =>
                    {
                        var id = $"del-m-{delSeq++}";
                        await strategy.AddArtifactAsync(
                            tree.ClientId, tree.ProjectId,
                            id, ArtifactType.TestCase, addParentId, "Del");
                        await strategy.DeleteArtifactAsync(
                            tree.ClientId, tree.ProjectId, id, []);
                    },
                    storageSize));
                task.Increment(1);

                // 11. MoveArtifact ─────────────────────────────────────────────
                var moveNodeId  = $"move-{Guid.NewGuid():N}";
                var moveParentA = leafNode.ParentId!;
                var moveParentB = tree.RandomLeaf(rng).ParentId!;
                while (moveParentB == moveParentA)
                    moveParentB = tree.RandomLeaf(rng).ParentId!;

                await strategy.AddArtifactAsync(
                    tree.ClientId, tree.ProjectId,
                    moveNodeId, ArtifactType.TestCase, moveParentA, "MoveNode");

                // MoveState tracks which parent the node currently lives under so
                // the closures can alternate moves between A and B correctly.
                var moveState = new MoveState(moveParentA);
                results.Add(await MeasureOpAsync("MoveArtifact", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () =>
                    {
                        var dest = moveState.CurrentParent == moveParentA
                            ? moveParentB : moveParentA;
                        await strategy.MoveArtifactAsync(
                            tree.ClientId, tree.ProjectId,
                            moveNodeId, moveState.CurrentParent, dest);
                        moveState.CurrentParent = dest;
                    },
                    measure: async () =>
                    {
                        var dest = moveState.CurrentParent == moveParentA
                            ? moveParentB : moveParentA;
                        await strategy.MoveArtifactAsync(
                            tree.ClientId, tree.ProjectId,
                            moveNodeId, moveState.CurrentParent, dest);
                        moveState.CurrentParent = dest;
                    },
                    storageSize));
                task.Increment(1);

                // 12. SetArtifactEnabled ───────────────────────────────────────
                bool toggleState = false;
                results.Add(await MeasureOpAsync("SetArtifactEnabled", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () => await strategy.SetArtifactEnabledAsync(
                        tree.ClientId, tree.ProjectId,
                        leafNode.ArtifactId, toggleState ^= true, []),
                    measure: async () => await strategy.SetArtifactEnabledAsync(
                        tree.ClientId, tree.ProjectId,
                        leafNode.ArtifactId, toggleState ^= true, []),
                    storageSize));
                task.Increment(1);

                // 13. ClearBranch ──────────────────────────────────────────────
                int clearSeq     = 0;
                var clearParentId = internalNode.ArtifactId;
                results.Add(await MeasureOpAsync("ClearBranch", tree, strategy,
                    warmupRuns, measureRuns,
                    warmup: async () =>
                    {
                        var id = $"clr-w-{clearSeq++}";
                        await strategy.AddArtifactAsync(
                            tree.ClientId, tree.ProjectId,
                            id, ArtifactType.TestCase, clearParentId, "Clear");
                        await strategy.ClearBranchAsync(
                            tree.ClientId, tree.ProjectId, clearParentId, [id]);
                    },
                    measure: async () =>
                    {
                        var id = $"clr-m-{clearSeq++}";
                        await strategy.AddArtifactAsync(
                            tree.ClientId, tree.ProjectId,
                            id, ArtifactType.TestCase, clearParentId, "Clear");
                        await strategy.ClearBranchAsync(
                            tree.ClientId, tree.ProjectId, clearParentId, [id]);
                    },
                    storageSize));
                task.Increment(1);

                task.StopTask();
            });

        allResults.AddRange(results);

        await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);
        AnsiConsole.WriteLine();
    }
}

// ── Print results ─────────────────────────────────────────────────────────────

ResultsReporter.PrintSummary(allResults);
ResultsReporter.PrintAnalysis(allResults);

// ── Cleanup ───────────────────────────────────────────────────────────────────

foreach (var s in strategies)
    await s.DisposeAsync();

AnsiConsole.MarkupLine("[bold green]✓  Benchmark complete.[/]");

// ── Local helper ──────────────────────────────────────────────────────────────

static async Task<OperationResult> MeasureOpAsync(
    string name,
    SimulatedTree tree,
    IStorageStrategy strategy,
    int warmupRuns,
    int measureRuns,
    Func<Task> warmup,
    Func<Task> measure,
    long storageSize = -1)
{
    try
    {
        for (int i = 0; i < warmupRuns; i++)
            await warmup();

        var sw        = new System.Diagnostics.Stopwatch();
        var latencies = new double[measureRuns];
        long memBefore = GC.GetTotalMemory(false);

        for (int i = 0; i < measureRuns; i++)
        {
            sw.Restart();
            await measure();
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMilliseconds;
        }

        long memAfter = GC.GetTotalMemory(false);
        long storageSz = storageSize >= 0
            ? storageSize
            : await strategy.GetStorageSizeBytesAsync(tree.ClientId, tree.ProjectId);

        return new OperationResult
        {
            StrategyName         = strategy.Name,
            OperationName        = name,
            TreeSize             = tree.Size,
            NodeCount            = tree.NodeCount,
            Iterations           = measureRuns,
            LatenciesMs          = latencies,
            StorageSizeBytes     = storageSz,
            PeakMemoryDeltaBytes = Math.Max(0, memAfter - memBefore),
            IsSuccess            = true,
        };
    }
    catch (Exception ex)
    {
        return new OperationResult
        {
            StrategyName  = strategy.Name,
            OperationName = name,
            TreeSize      = tree.Size,
            NodeCount     = tree.NodeCount,
            Iterations    = 0,
            LatenciesMs   = [],
            IsSuccess     = false,
            ErrorMessage  = ex.Message,
        };
    }
}

static async Task<int> CountParentsWithNoChildrenOfTypeAsync(
    IStorageStrategy strategy,
    string clientId,
    string projectId,
    ArtifactType childType)
{
    var projection = await strategy.ReadFullTreeAsync(clientId, projectId);
    if (projection == null) return 0;

    var count = 0;
    foreach (var parent in projection.ArtifactIndex.Values)
    {
        var hasChildOfType = parent.ChildrenIds
            .Select(id => projection.ArtifactIndex.GetValueOrDefault(id))
            .OfType<ArtifactNode>()
            .Any(child => child.ArtifactType == childType);

        if (!hasChildOfType)
            count++;
    }

    return count;
}

static async Task<List<OperationResult>> RunCrossProjectBenchmarkAsync(
    IReadOnlyList<IStorageStrategy> strategies,
    string clientId,
    int projectCount,
    int nodesPerProject,
    ArtifactType artifactType,
    int warmupRuns,
    int measureRuns)
{
    var trees = Enumerable.Range(0, projectCount)
        .Select(_ => TreeSimulator.GenerateExactNodeCount(nodesPerProject, clientId))
        .ToList();
    var totalNodeCount = trees.Sum(t => t.NodeCount);
    var projectIds = trees.Select(t => t.ProjectId).ToArray();
    var results = new List<OperationResult>();

    AnsiConsole.Write(new Rule("[bold cyan]Cross-Project Search Benchmark[/]").RuleStyle("cyan"));
    AnsiConsole.MarkupLine(
        $"  Projects: [white]{projectCount}[/]  " +
        $"Nodes / project: [white]{nodesPerProject:N0}[/]  " +
        $"Total nodes: [white]{totalNodeCount:N0}[/]  " +
        $"Artifact type: [white]{artifactType}[/]\n");

    foreach (var strategy in strategies)
    {
        if (strategy is Neo4jStrategy { IsAvailable: false })
        {
            AnsiConsole.MarkupLine(
                $"[dim]  Skipped {Markup.Escape(strategy.Name)} (Neo4j not reachable)[/]");
            continue;
        }

        AnsiConsole.MarkupLine(
            $"[bold]Running {Markup.Escape(strategy.Name)}[/] — indexed search across {projectCount} projects");

        await AnsiConsole.Status().StartAsync(
            $"Seeding {strategy.Name}...",
            async _ =>
            {
                foreach (var tree in trees)
                {
                    await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);
                    await strategy.WriteTreeAsync(tree);
                }
            });

        long storageSize = 0;
        foreach (var tree in trees)
            storageSize += await strategy.GetStorageSizeBytesAsync(tree.ClientId, tree.ProjectId);

        results.Add(await MeasureCustomOpAsync(
            "SearchAcrossProjects",
            TreeSize.Custom,
            totalNodeCount,
            strategy,
            warmupRuns,
            measureRuns,
            warmup: async () =>
            {
                await strategy.SearchAcrossProjectsByTypeAsync(clientId, projectIds, artifactType);
            },
            measure: async () =>
            {
                await strategy.SearchAcrossProjectsByTypeAsync(clientId, projectIds, artifactType);
            },
            storageSize));

        foreach (var tree in trees)
            await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);

        AnsiConsole.WriteLine();
    }

    return results;
}

static async Task<OperationResult> MeasureCustomOpAsync(
    string name,
    TreeSize treeSize,
    int nodeCount,
    IStorageStrategy strategy,
    int warmupRuns,
    int measureRuns,
    Func<Task> warmup,
    Func<Task> measure,
    long storageSize = -1)
{
    try
    {
        for (int i = 0; i < warmupRuns; i++)
            await warmup();

        var sw = new System.Diagnostics.Stopwatch();
        var latencies = new double[measureRuns];
        long memBefore = GC.GetTotalMemory(false);

        for (int i = 0; i < measureRuns; i++)
        {
            sw.Restart();
            await measure();
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMilliseconds;
        }

        long memAfter = GC.GetTotalMemory(false);

        return new OperationResult
        {
            StrategyName = strategy.Name,
            OperationName = name,
            TreeSize = treeSize,
            NodeCount = nodeCount,
            Iterations = measureRuns,
            LatenciesMs = latencies,
            StorageSizeBytes = storageSize,
            PeakMemoryDeltaBytes = Math.Max(0, memAfter - memBefore),
            IsSuccess = true,
        };
    }
    catch (Exception ex)
    {
        return new OperationResult
        {
            StrategyName = strategy.Name,
            OperationName = name,
            TreeSize = treeSize,
            NodeCount = nodeCount,
            Iterations = 0,
            LatenciesMs = [],
            IsSuccess = false,
            ErrorMessage = ex.Message,
        };
    }
}

// ── Support types ─────────────────────────────────────────────────────────────

/// <summary>
/// Tracks the current parent of the node being moved back and forth during the
/// MoveArtifact benchmark so both the warmup and measure closures share state.
/// </summary>
sealed class MoveState(string initialParent)
{
    public string CurrentParent { get; set; } = initialParent;
}

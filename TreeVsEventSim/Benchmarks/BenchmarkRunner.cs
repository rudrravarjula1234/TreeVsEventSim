using System.Diagnostics;
using TreeVsEventSim.Models;
using TreeVsEventSim.Simulation;
using TreeVsEventSim.Strategies;

namespace TreeVsEventSim.Benchmarks;

/// <summary>
/// Runs all benchmark operations against a strategy and returns the results.
/// </summary>
public sealed class BenchmarkRunner
{
    private readonly int _warmupIterations;
    private readonly int _measureIterations;
    private readonly Random _rng = new(123);

    public BenchmarkRunner(int warmupIterations = 3, int measureIterations = 20)
    {
        _warmupIterations = warmupIterations;
        _measureIterations = measureIterations;
    }

    public async Task<List<OperationResult>> RunAllAsync(
        IStorageStrategy strategy,
        SimulatedTree tree,
        bool verbose = false)
    {
        var results = new List<OperationResult>();

        Log(verbose, $"  [{strategy.Name}] Cleaning up old data...");
        await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);

        // ── 1. WriteTree (bulk initial write) ────────────────────────────────
        results.Add(await MeasureAsync(
            strategy, "WriteTree", tree,
            warmup: async () =>
            {
                await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);
                await strategy.WriteTreeAsync(tree);
            },
            measure: async () =>
            {
                await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);
                await strategy.WriteTreeAsync(tree);
            },
            verbose));

        // Ensure data is present for read benchmarks
        await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);
        await strategy.WriteTreeAsync(tree);
        var storageSize = await strategy.GetStorageSizeBytesAsync(tree.ClientId, tree.ProjectId);

        // ── 2. ReadFullTree ──────────────────────────────────────────────────
        results.Add(await MeasureAsync(
            strategy, "ReadFullTree", tree,
            warmup: async () => await strategy.ReadFullTreeAsync(tree.ClientId, tree.ProjectId),
            measure: async () => await strategy.ReadFullTreeAsync(tree.ClientId, tree.ProjectId),
            verbose,
            storageSizeOverride: storageSize));

        // ── 3. GetSingleNode ─────────────────────────────────────────────────
        var targetNode = tree.RandomNode(_rng);
        results.Add(await MeasureAsync(
            strategy, "GetSingleNode", tree,
            warmup: async () => await strategy.GetSingleNodeAsync(
                tree.ClientId, tree.ProjectId, targetNode.ArtifactId),
            measure: async () => await strategy.GetSingleNodeAsync(
                tree.ClientId, tree.ProjectId, targetNode.ArtifactId),
            verbose,
            storageSizeOverride: storageSize));

        // ── 4. GetChildren ───────────────────────────────────────────────────
        var internalNode = tree.RandomInternal(_rng);
        results.Add(await MeasureAsync(
            strategy, "GetChildren", tree,
            warmup: async () => await strategy.GetChildrenAsync(
                tree.ClientId, tree.ProjectId, internalNode.ArtifactId),
            measure: async () => await strategy.GetChildrenAsync(
                tree.ClientId, tree.ProjectId, internalNode.ArtifactId),
            verbose,
            storageSizeOverride: storageSize));

        // ── 5. GetParent ─────────────────────────────────────────────────────
        results.Add(await MeasureAsync(
            strategy, "GetParent", tree,
            warmup: async () => await strategy.GetParentAsync(
                tree.ClientId, tree.ProjectId, targetNode.ArtifactId),
            measure: async () => await strategy.GetParentAsync(
                tree.ClientId, tree.ProjectId, targetNode.ArtifactId),
            verbose,
            storageSizeOverride: storageSize));

        // ── 6. AddArtifact ───────────────────────────────────────────────────
        // Reset to baseline and add the same leaf repeatedly, cleaning between each iteration
        var addParentId = tree.RandomLeaf(_rng).ParentId!;
        int addSeq = 0;
        results.Add(await MeasureAsync(
            strategy, "AddArtifact", tree,
            warmup: async () =>
            {
                var id = $"bench-add-warmup-{addSeq++}";
                await strategy.AddArtifactAsync(
                    tree.ClientId, tree.ProjectId,
                    id, ArtifactType.TestCase, addParentId, "Benchmark Add Warmup");
            },
            measure: async () =>
            {
                var id = $"bench-add-{addSeq++}";
                await strategy.AddArtifactAsync(
                    tree.ClientId, tree.ProjectId,
                    id, ArtifactType.TestCase, addParentId, "Benchmark Add");
            },
            verbose,
            storageSizeOverride: storageSize));

        // ── 7. DeleteArtifact ────────────────────────────────────────────────
        // We add a temporary subtree then delete it; repeat N times
        int delSeq = 0;
        results.Add(await MeasureAsync(
            strategy, "DeleteArtifact", tree,
            warmup: async () =>
            {
                var parentId2 = tree.RandomLeaf(_rng).ParentId!;
                var delId = $"bench-del-w-{delSeq++}";
                await strategy.AddArtifactAsync(
                    tree.ClientId, tree.ProjectId,
                    delId, ArtifactType.TestCase, parentId2, "Del Warmup");
                await strategy.DeleteArtifactAsync(
                    tree.ClientId, tree.ProjectId, delId, []);
            },
            measure: async () =>
            {
                var parentId2 = tree.RandomLeaf(_rng).ParentId!;
                var delId = $"bench-del-{delSeq++}";
                await strategy.AddArtifactAsync(
                    tree.ClientId, tree.ProjectId,
                    delId, ArtifactType.TestCase, parentId2, "Del Measure");
                await strategy.DeleteArtifactAsync(
                    tree.ClientId, tree.ProjectId, delId, []);
            },
            verbose,
            storageSizeOverride: storageSize));

        // ── 8. MoveArtifact ──────────────────────────────────────────────────
        // Add a temporary node and move it back and forth between two parents
        var moveParentA = tree.RandomLeaf(_rng).ParentId!;
        var moveParentB = tree.RandomLeaf(_rng).ParentId!;
        // Ensure they differ
        while (moveParentB == moveParentA)
            moveParentB = tree.RandomLeaf(_rng).ParentId!;

        const string MoveNodeId = "bench-move-node";
        await strategy.AddArtifactAsync(
            tree.ClientId, tree.ProjectId,
            MoveNodeId, ArtifactType.TestCase, moveParentA, "Move Bench Node");

        var moveCurrent = moveParentA;
        results.Add(await MeasureAsync(
            strategy, "MoveArtifact", tree,
            warmup: async () =>
            {
                var dest = moveCurrent == moveParentA ? moveParentB : moveParentA;
                await strategy.MoveArtifactAsync(
                    tree.ClientId, tree.ProjectId,
                    MoveNodeId, moveCurrent, dest);
            },
            measure: async () =>
            {
                var dest = moveCurrent == moveParentA ? moveParentB : moveParentA;
                await strategy.MoveArtifactAsync(
                    tree.ClientId, tree.ProjectId,
                    MoveNodeId, moveCurrent, dest);
            },
            verbose,
            storageSizeOverride: storageSize));

        // ── 9. SetArtifactEnabled ────────────────────────────────────────────
        var toggleNode = tree.RandomLeaf(_rng);
        bool toggleState = false;
        results.Add(await MeasureAsync(
            strategy, "SetArtifactEnabled", tree,
            warmup: async () =>
            {
                await strategy.SetArtifactEnabledAsync(
                    tree.ClientId, tree.ProjectId,
                    toggleNode.ArtifactId, toggleState ^= true, []);
            },
            measure: async () =>
            {
                await strategy.SetArtifactEnabledAsync(
                    tree.ClientId, tree.ProjectId,
                    toggleNode.ArtifactId, toggleState ^= true, []);
            },
            verbose,
            storageSizeOverride: storageSize));

        // ── 10. ClearBranch ──────────────────────────────────────────────────
        // Add a small temporary subtree under a chosen parent, then clear it
        var clearParentId = tree.RandomInternal(_rng).ArtifactId;
        int clearSeq = 0;
        results.Add(await MeasureAsync(
            strategy, "ClearBranch", tree,
            warmup: async () =>
            {
                var childId = $"bench-clear-w-{clearSeq++}";
                await strategy.AddArtifactAsync(
                    tree.ClientId, tree.ProjectId,
                    childId, ArtifactType.TestCase, clearParentId, "Clear Warmup");
                await strategy.ClearBranchAsync(
                    tree.ClientId, tree.ProjectId, clearParentId, [childId]);
            },
            measure: async () =>
            {
                var childId = $"bench-clear-{clearSeq++}";
                await strategy.AddArtifactAsync(
                    tree.ClientId, tree.ProjectId,
                    childId, ArtifactType.TestCase, clearParentId, "Clear Measure");
                await strategy.ClearBranchAsync(
                    tree.ClientId, tree.ProjectId, clearParentId, [childId]);
            },
            verbose,
            storageSizeOverride: storageSize));

        return results;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private async Task<OperationResult> MeasureAsync(
        IStorageStrategy strategy,
        string operationName,
        SimulatedTree tree,
        Func<Task> warmup,
        Func<Task> measure,
        bool verbose,
        long storageSizeOverride = -1)
    {
        Log(verbose, $"  [{strategy.Name}] {operationName} — warming up...");

        try
        {
            // Warmup
            for (int i = 0; i < _warmupIterations; i++)
                await warmup();

            // Measure
            var latencies = new double[_measureIterations];
            var sw = new Stopwatch();
            long memBefore = GC.GetTotalMemory(false);

            for (int i = 0; i < _measureIterations; i++)
            {
                sw.Restart();
                await measure();
                sw.Stop();
                latencies[i] = sw.Elapsed.TotalMilliseconds;
            }

            long memAfter = GC.GetTotalMemory(false);
            long storageSz = storageSizeOverride >= 0
                ? storageSizeOverride
                : await strategy.GetStorageSizeBytesAsync(tree.ClientId, tree.ProjectId);

            Log(verbose, $"  [{strategy.Name}] {operationName} — p50={Pct(latencies, 50):F2}ms");

            return new OperationResult
            {
                StrategyName = strategy.Name,
                OperationName = operationName,
                TreeSize = tree.Size,
                NodeCount = tree.NodeCount,
                Iterations = _measureIterations,
                LatenciesMs = latencies,
                StorageSizeBytes = storageSz,
                PeakMemoryDeltaBytes = Math.Max(0, memAfter - memBefore),
                IsSuccess = true,
            };
        }
        catch (Exception ex)
        {
            Log(verbose, $"  [{strategy.Name}] {operationName} — FAILED: {ex.Message}");
            return new OperationResult
            {
                StrategyName = strategy.Name,
                OperationName = operationName,
                TreeSize = tree.Size,
                NodeCount = tree.NodeCount,
                Iterations = 0,
                LatenciesMs = [],
                IsSuccess = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    private static double Pct(double[] values, double p)
    {
        if (values.Length == 0) return 0;
        var sorted = values.OrderBy(x => x).ToArray();
        var idx = (p / 100.0) * (sorted.Length - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        return lo == hi ? sorted[lo] : sorted[lo] + (idx - lo) * (sorted[hi] - sorted[lo]);
    }

    private static void Log(bool verbose, string msg)
    {
        if (verbose) Console.WriteLine(msg);
    }
}

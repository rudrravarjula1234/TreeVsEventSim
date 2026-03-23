using TreeVsEventSim.Simulation;

namespace TreeVsEventSim.Benchmarks;

/// <summary>Recorded latencies and derived statistics for one benchmark cell.</summary>
public sealed class OperationResult
{
    public string StrategyName { get; init; } = string.Empty;
    public string OperationName { get; init; } = string.Empty;
    public TreeSize TreeSize { get; init; }
    public int NodeCount { get; init; }
    public int Iterations { get; init; }

    // Raw latency samples in milliseconds
    public double[] LatenciesMs { get; init; } = [];

    // Derived percentiles
    public double P50Ms => Percentile(50);
    public double P95Ms => Percentile(95);
    public double P99Ms => Percentile(99);
    public double MinMs => LatenciesMs.Length > 0 ? LatenciesMs.Min() : 0;
    public double MaxMs => LatenciesMs.Length > 0 ? LatenciesMs.Max() : 0;
    public double MeanMs => LatenciesMs.Length > 0 ? LatenciesMs.Average() : 0;

    /// <summary>Operations per second based on mean latency.</summary>
    public double ThroughputOpsPerSec =>
        MeanMs > 0 ? 1_000.0 / MeanMs : 0;

    /// <summary>Storage size reported by the strategy at the time of measurement.</summary>
    public long StorageSizeBytes { get; init; }

    /// <summary>Peak managed memory delta (bytes) during the operation.</summary>
    public long PeakMemoryDeltaBytes { get; init; }

    /// <summary>False when the strategy threw an exception (backend unavailable etc.).</summary>
    public bool IsSuccess { get; init; } = true;

    /// <summary>Error message when IsSuccess == false.</summary>
    public string? ErrorMessage { get; init; }

    private double Percentile(double pct)
    {
        if (LatenciesMs.Length == 0) return 0;
        var sorted = LatenciesMs.OrderBy(x => x).ToArray();
        var idx = (pct / 100.0) * (sorted.Length - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (idx - lo) * (sorted[hi] - sorted[lo]);
    }
}

using Spectre.Console;
using TreeVsEventSim.Benchmarks;
using TreeVsEventSim.Simulation;

namespace TreeVsEventSim.Reporting;

/// <summary>
/// Renders benchmark results as formatted tables using Spectre.Console.
/// </summary>
public static class ResultsReporter
{
    private static readonly string[] Operations =
    [
        "WriteTree",
        "ReadFullTree",
        "GetSingleNode",
        "GetChildren",
        "GetParent",
        "AddArtifact",
        "DeleteArtifact",
        "MoveArtifact",
        "SetArtifactEnabled",
        "ClearBranch",
    ];

    private static readonly string[] StrategyNames =
        ["EventSourcing", "MaterializedDoc", "Neo4j"];

    /// <summary>Print a per-tree-size summary table for a given metric.</summary>
    public static void PrintSummary(IReadOnlyList<OperationResult> allResults)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold yellow]Benchmark Results[/]").RuleStyle("yellow"));
        AnsiConsole.WriteLine();

        foreach (var size in Enum.GetValues<TreeSize>())
        {
            var sizeResults = allResults.Where(r => r.TreeSize == size).ToList();
            if (sizeResults.Count == 0) continue;

            var nodeCount = sizeResults.FirstOrDefault()?.NodeCount ?? 0;
            AnsiConsole.Write(new Rule(
                $"[bold cyan]Tree Size: {size}  ({nodeCount} nodes)[/]")
                .RuleStyle("cyan"));
            AnsiConsole.WriteLine();

            // ── Latency table (p50 / p95 / p99 in ms) ─────────────────────
            PrintLatencyTable(sizeResults);
            AnsiConsole.WriteLine();

            // ── Throughput table (ops/sec) ─────────────────────────────────
            PrintThroughputTable(sizeResults);
            AnsiConsole.WriteLine();

            // ── Storage & memory table ─────────────────────────────────────
            PrintStorageTable(sizeResults);
            AnsiConsole.WriteLine();
        }

        // ── Cross-size comparison for ReadFullTree ─────────────────────────
        PrintScalingComparison(allResults);
    }

    private static void PrintLatencyTable(List<OperationResult> results)
    {
        var table = new Table();
        table.Title = new TableTitle("[bold]Latency (milliseconds)[/]");
        table.AddColumn(new TableColumn("[grey]Operation[/]").LeftAligned());

        foreach (var strat in StrategyNames)
        {
            table.AddColumn(new TableColumn($"[green]{strat}[/]\n[grey]p50[/]").RightAligned());
            table.AddColumn(new TableColumn($"[green]{strat}[/]\n[grey]p95[/]").RightAligned());
            table.AddColumn(new TableColumn($"[green]{strat}[/]\n[grey]p99[/]").RightAligned());
        }

        table.Border(TableBorder.Rounded);

        foreach (var op in Operations)
        {
            var cells = new List<string> { op };
            foreach (var strat in StrategyNames)
            {
                var result = results.FirstOrDefault(
                    r => r.OperationName == op && r.StrategyName == strat);

                if (result == null || !result.IsSuccess)
                {
                    cells.Add("[dim]N/A[/]");
                    cells.Add("[dim]N/A[/]");
                    cells.Add("[dim]N/A[/]");
                }
                else
                {
                    cells.Add(FormatMs(result.P50Ms));
                    cells.Add(FormatMs(result.P95Ms));
                    cells.Add(FormatMs(result.P99Ms));
                }
            }

            table.AddRow(cells.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static void PrintThroughputTable(List<OperationResult> results)
    {
        var table = new Table();
        table.Title = new TableTitle("[bold]Throughput (ops/second)[/]");
        table.AddColumn(new TableColumn("[grey]Operation[/]").LeftAligned());

        foreach (var strat in StrategyNames)
            table.AddColumn(new TableColumn($"[green]{strat}[/]").RightAligned());

        table.Border(TableBorder.Rounded);

        foreach (var op in Operations)
        {
            var cells = new List<string> { op };
            foreach (var strat in StrategyNames)
            {
                var result = results.FirstOrDefault(
                    r => r.OperationName == op && r.StrategyName == strat);

                cells.Add(result == null || !result.IsSuccess
                    ? "[dim]N/A[/]"
                    : $"[white]{result.ThroughputOpsPerSec:F0}[/]");
            }

            table.AddRow(cells.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private static void PrintStorageTable(List<OperationResult> results)
    {
        var table = new Table();
        table.Title = new TableTitle("[bold]Storage Size & Memory[/]");
        table.AddColumn(new TableColumn("[grey]Strategy[/]").LeftAligned());
        table.AddColumn(new TableColumn("[grey]Storage Size[/]").RightAligned());
        table.AddColumn(new TableColumn("[grey]Peak Mem Δ (ReadFullTree)[/]").RightAligned());

        table.Border(TableBorder.Rounded);

        foreach (var strat in StrategyNames)
        {
            // Take the storage size from WriteTree (most representative)
            var writeResult = results.FirstOrDefault(
                r => r.OperationName == "WriteTree" && r.StrategyName == strat);
            var readResult = results.FirstOrDefault(
                r => r.OperationName == "ReadFullTree" && r.StrategyName == strat);

            var storageTxt = writeResult?.IsSuccess == true && writeResult.StorageSizeBytes > 0
                ? FormatBytes(writeResult.StorageSizeBytes)
                : "[dim]N/A[/]";

            var memTxt = readResult?.IsSuccess == true
                ? FormatBytes(readResult.PeakMemoryDeltaBytes)
                : "[dim]N/A[/]";

            table.AddRow(strat, storageTxt, memTxt);
        }

        AnsiConsole.Write(table);
    }

    private static void PrintScalingComparison(IReadOnlyList<OperationResult> allResults)
    {
        AnsiConsole.Write(new Rule(
            "[bold yellow]ReadFullTree Scaling Across Tree Sizes[/]").RuleStyle("yellow"));

        var table = new Table();
        table.AddColumn(new TableColumn("[grey]Tree Size[/]").LeftAligned());
        table.AddColumn(new TableColumn("[grey]Nodes[/]").RightAligned());

        foreach (var strat in StrategyNames)
            table.AddColumn(new TableColumn($"[green]{strat}[/]\n[grey]p50 ms[/]").RightAligned());

        table.Border(TableBorder.Rounded);

        foreach (var size in Enum.GetValues<TreeSize>())
        {
            var row = allResults
                .Where(r => r.TreeSize == size && r.OperationName == "ReadFullTree")
                .ToList();

            var nodeCount = row.FirstOrDefault()?.NodeCount ?? 0;
            var cells = new List<string> { size.ToString(), nodeCount.ToString("N0") };

            foreach (var strat in StrategyNames)
            {
                var r = row.FirstOrDefault(x => x.StrategyName == strat);
                cells.Add(r?.IsSuccess == true ? FormatMs(r.P50Ms) : "[dim]N/A[/]");
            }

            table.AddRow(cells.ToArray());
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    // ── Analysis ──────────────────────────────────────────────────────────────

    /// <summary>Print a textual analysis of the benchmark results.</summary>
    public static void PrintAnalysis(IReadOnlyList<OperationResult> allResults)
    {
        AnsiConsole.Write(new Rule("[bold magenta]Analysis & Recommendations[/]")
            .RuleStyle("magenta"));
        AnsiConsole.WriteLine();

        var panel = new Panel(
            new Markup(BuildAnalysisText(allResults)))
        {
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 1),
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private static string BuildAnalysisText(IReadOnlyList<OperationResult> allResults)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("[bold]Strategy Overview[/]");
        sb.AppendLine();
        sb.AppendLine("[green]1. Event Sourcing (MongoDB EventStore)[/]");
        sb.AppendLine("   [grey]+ Complete audit trail — every change is immutable and replayable[/]");
        sb.AppendLine("   [grey]+ Simple write path — append-only, no read-modify-write[/]");
        sb.AppendLine("   [grey]− Read cost grows linearly with event count (full replay)[/]");
        sb.AppendLine("   [grey]− Single-node lookups pay full replay cost — O(events)[/]");
        sb.AppendLine();
        sb.AppendLine("[green]2. Materialized Document (MongoDB)[/]");
        sb.AppendLine("   [grey]+ Reads are O(1) — single document fetch[/]");
        sb.AppendLine("   [grey]+ Individual node fields updated in-place (dot-notation)[/]");
        sb.AppendLine("   [grey]+ Single-node fetch via field projection avoids full document read[/]");
        sb.AppendLine("   [grey]− No audit trail; point-in-time queries not supported[/]");
        sb.AppendLine("   [grey]− 16 MB MongoDB document limit may constrain very large trees[/]");
        sb.AppendLine();
        sb.AppendLine("[green]3. Neo4j Graph Database[/]");
        sb.AppendLine("   [grey]+ Native graph traversal — subtree and ancestor queries are natural[/]");
        sb.AppendLine("   [grey]+ Cascade delete / enable-disable via PARENT*0.. traversal[/]");
        sb.AppendLine("   [grey]+ No pre-computed ancestor/descendant lists needed[/]");
        sb.AppendLine("   [grey]− Higher write latency for bulk inserts (driver round-trips)[/]");
        sb.AppendLine("   [grey]− Requires separate infrastructure component[/]");
        sb.AppendLine();

        // Best operation winners — use largest available tree size
        sb.AppendLine("[bold]Key Findings[/]");
        var availableSizes = allResults.Select(r => r.TreeSize).Distinct()
            .OrderByDescending(s => s).ToList();
        var reportSize = availableSizes.FirstOrDefault();

        foreach (var op in new[] { "ReadFullTree", "GetSingleNode", "GetChildren", "WriteTree" })
        {
            var sizeResults = allResults
                .Where(r => r.TreeSize == reportSize && r.OperationName == op && r.IsSuccess)
                .OrderBy(r => r.P50Ms)
                .ToList();

            if (sizeResults.Count > 0)
            {
                var winner = sizeResults.First();
                sb.AppendLine($"   [white]{op}[/] fastest ([grey]{reportSize}[/]): " +
                    $"[yellow]{winner.StrategyName}[/] " +
                    $"([grey]p50={winner.P50Ms:F2}ms[/])");
            }
        }

        return sb.ToString();
    }

    // ── Formatters ────────────────────────────────────────────────────────────

    private static string FormatMs(double ms)
    {
        if (ms < 1) return $"[white]{ms:F3}[/]";
        if (ms < 10) return $"[white]{ms:F2}[/]";
        if (ms < 100) return $"[yellow]{ms:F1}[/]";
        return $"[red]{ms:F0}[/]";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "[dim]N/A[/]";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

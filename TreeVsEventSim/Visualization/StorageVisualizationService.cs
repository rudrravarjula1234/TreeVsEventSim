using Spectre.Console;
using TreeVsEventSim.Models;
using TreeVsEventSim.Simulation;
using TreeVsEventSim.Strategies;
using TreeVsEventSim.Strategies.Neo4j;

namespace TreeVsEventSim.Visualization;

/// <summary>
/// Writes one generated tree to each backend and prints a compact snapshot of
/// how data was persisted.
/// </summary>
public sealed class StorageVisualizationService
{
    public async Task RunAsync(
        IReadOnlyList<IStorageStrategy> strategies,
        IReadOnlyList<TreeSize> treeSizes,
        int snapshotMaxItems)
    {
        foreach (var size in treeSizes)
        {
            var tree = TreeSimulator.Generate(size);

            AnsiConsole.Write(new Rule($"[bold cyan]Visualization Mode — {size}[/]").RuleStyle("cyan"));
            AnsiConsole.MarkupLine(
                $"  Nodes: [white]{tree.NodeCount:N0}[/]  " +
                $"ProjectId: [grey]{tree.ProjectId}[/]\n");

            foreach (var strategy in strategies)
            {
                if (strategy is Neo4jStrategy { IsAvailable: false })
                {
                    AnsiConsole.MarkupLine($"[dim]Skipped {Markup.Escape(strategy.Name)} (Neo4j not reachable)[/]");
                    continue;
                }

                await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);
                await strategy.WriteTreeAsync(tree);

                var projection = await strategy.ReadFullTreeAsync(tree.ClientId, tree.ProjectId);

                AnsiConsole.MarkupLine(
                    $"[bold]{Markup.Escape(strategy.Name)}[/] [grey]({Markup.Escape(strategy.Description)})[/]");

                if (projection != null)
                {
                    var roots = projection.ArtifactIndex.Values.Count(n => n.ParentId == null);
                    var sampleRoots = projection.ArtifactIndex.Values
                        .Where(n => n.ParentId == null)
                        .Take(3)
                        .Select(n => $"{n.ArtifactId}:{n.ArtifactType}")
                        .ToArray();

                    var summary =
                        $"Projection nodes: {projection.ArtifactIndex.Count:N0}\n" +
                        $"Roots: {roots}\n" +
                        $"Max depth: {projection.Stats.MaxDepth}\n" +
                        $"Sample roots: {(sampleRoots.Length > 0 ? string.Join(", ", sampleRoots) : "(none)")}";

                    AnsiConsole.Write(new Panel(summary)
                        .Header("Projection Summary")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Grey));

                    AnsiConsole.Write(CreateDepthTable(projection));
                    AnsiConsole.Write(CreateTypeTable(projection));
                }

                if (strategy is IStorageSnapshotProvider snapshotProvider)
                {
                    var lines = await snapshotProvider.GetStorageSnapshotLinesAsync(
                        tree.ClientId,
                        tree.ProjectId,
                        snapshotMaxItems);

                    var content = lines.Count == 0
                        ? "No snapshot data available."
                        : string.Join("\n", lines);

                    AnsiConsole.Write(new Panel(new Text(content))
                        .Header("Storage Snapshot")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.SteelBlue));

                    var jsonEntries = await snapshotProvider.GetSampleJsonEntriesAsync(
                        tree.ClientId,
                        tree.ProjectId,
                        3);

                    var jsonContent = jsonEntries.Count == 0
                        ? "No JSON entries available."
                        : string.Join("\n\n", jsonEntries);

                    AnsiConsole.Write(new Panel(new Text(jsonContent))
                        .Header("3 Exact JSON Entries")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Green));
                }

                await strategy.CleanupAsync(tree.ClientId, tree.ProjectId);
                AnsiConsole.WriteLine();
            }
        }
    }

    private static Table CreateDepthTable(ArtifactTreeProjection projection)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Nodes By Depth[/]");

        table.AddColumn("Depth");
        table.AddColumn("Meaning");
        table.AddColumn("Count");

        foreach (var entry in projection.Stats.ArtifactsByDepth
                 .Select(kvp => new { Depth = int.Parse(kvp.Key), Count = kvp.Value })
                 .OrderBy(x => x.Depth))
        {
            table.AddRow(
            entry.Depth.ToString(),
            DescribeDepth(entry.Depth),
            entry.Count.ToString("N0"));
        }

        return table;
    }

    private static Table CreateTypeTable(ArtifactTreeProjection projection)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Nodes By Artifact Type[/]");

        table.AddColumn("Artifact Type");
        table.AddColumn("Count");

        foreach (var (type, count) in projection.Stats.ArtifactsByType
                     .OrderBy(kvp => kvp.Key))
        {
            table.AddRow(type, count.ToString("N0"));
        }

        return table;
    }

    private static string DescribeDepth(int depth)
    {
        return depth switch
        {
            0 => "Root",
            1 => "Capability, Business Process",
            2 => "Business Process Flow, Epic",
            3 => "Feature, Architecture",
            4 => "User Story",
            5 => "Test Case, Git Commit",
            6 => "ADO Push, Generated Artifact",
            _ => "Other",
        };
    }
}

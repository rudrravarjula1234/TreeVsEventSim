namespace TreeVsEventSim.Strategies;

/// <summary>
/// Optional diagnostics interface for strategies that can emit raw storage
/// snapshots to help visualize backend persistence shape.
/// </summary>
public interface IStorageSnapshotProvider
{
    Task<IReadOnlyList<string>> GetStorageSnapshotLinesAsync(
        string clientId,
        string projectId,
        int maxItems = 8);

    Task<IReadOnlyList<string>> GetSampleJsonEntriesAsync(
        string clientId,
        string projectId,
        int maxItems = 3);
}

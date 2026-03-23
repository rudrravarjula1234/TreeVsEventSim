using TreeVsEventSim.Models;
using TreeVsEventSim.Simulation;

namespace TreeVsEventSim.Strategies;

/// <summary>
/// Common interface all three storage strategies must implement so the
/// benchmark runner can exercise them uniformly.
/// </summary>
public interface IStorageStrategy : IAsyncDisposable
{
    string Name { get; }
    string Description { get; }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>Connect to the backend and create indexes / schema.</summary>
    Task InitializeAsync();

    /// <summary>Drop all data for the given project (clean-slate between runs).</summary>
    Task CleanupAsync(string clientId, string projectId);

    // ── Bulk write ───────────────────────────────────────────────────────────

    /// <summary>
    /// Persist the entire simulated tree from scratch.
    /// Returns storage size in bytes after write (-1 if unavailable).
    /// </summary>
    Task<long> WriteTreeAsync(SimulatedTree tree);

    // ── Read operations ──────────────────────────────────────────────────────

    /// <summary>Reconstruct / fetch the full tree projection.</summary>
    Task<ArtifactTreeProjection?> ReadFullTreeAsync(string clientId, string projectId);

    /// <summary>Fetch a single artifact node by ID.</summary>
    Task<ArtifactNode?> GetSingleNodeAsync(string clientId, string projectId, string artifactId);

    /// <summary>Get immediate children of the given artifact.</summary>
    Task<IReadOnlyList<ArtifactNode>> GetChildrenAsync(
        string clientId, string projectId, string artifactId);

    /// <summary>Get the direct parent of the given artifact.</summary>
    Task<ArtifactNode?> GetParentAsync(string clientId, string projectId, string artifactId);

    // ── Mutation operations ───────────────────────────────────────────────────

    /// <summary>Append a new leaf artifact to the tree.</summary>
    Task AddArtifactAsync(
        string clientId, string projectId,
        string artifactId, ArtifactType type,
        string? parentId, string title);

    /// <summary>Delete an artifact and all its descendants.</summary>
    Task DeleteArtifactAsync(
        string clientId, string projectId,
        string artifactId, IReadOnlyList<string> descendantIds);

    /// <summary>Move an artifact to a different parent.</summary>
    Task MoveArtifactAsync(
        string clientId, string projectId,
        string artifactId, string oldParentId, string newParentId);

    /// <summary>Enable or disable an artifact and its descendants.</summary>
    Task SetArtifactEnabledAsync(
        string clientId, string projectId,
        string artifactId, bool enabled,
        IReadOnlyList<string> affectedDescendantIds);

    /// <summary>Remove all children of a parent artifact.</summary>
    Task ClearBranchAsync(
        string clientId, string projectId,
        string parentId, IReadOnlyList<string> clearedIds);

    // ── Storage metrics ───────────────────────────────────────────────────────

    /// <summary>
    /// Return current storage footprint in bytes for this project's data.
    /// Returns -1 if the backend does not support this metric.
    /// </summary>
    Task<long> GetStorageSizeBytesAsync(string clientId, string projectId);
}

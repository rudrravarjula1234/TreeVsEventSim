using Neo4j.Driver;
using System.Text.Json;
using TreeVsEventSim.Models;
using TreeVsEventSim.Simulation;
using TreeVsEventSim.Strategies;

namespace TreeVsEventSim.Strategies.Neo4j;

/// <summary>
/// Strategy 3 — Neo4j Graph Database.
///
/// Each artifact is stored as a labeled node (:Artifact) with properties.
/// Parent-child relationships are stored as directed edges: (child)-[:PARENT]->(parent).
/// This lets us use native graph traversal for ancestry, subtree, and cascade queries.
///
/// Connection: bolt://localhost:7687 (default Neo4j port).
/// If Neo4j is unavailable the strategy gracefully returns null / empty results
/// and IsAvailable == false so the benchmark runner can report N/A.
/// </summary>
public sealed class Neo4jStrategy : IStorageStrategy, IStorageSnapshotProvider
{
    private readonly IDriver _driver;

    public string Name => "Neo4j";
    public string Description =>
        "Neo4j graph database — nodes as :Artifact, edges as [:PARENT] relationships";

    public bool IsAvailable { get; private set; }
    public string? AvailabilityError { get; private set; }

    public Neo4jStrategy(string uri, string user, string password, bool encrypted = false)
    {
        _driver = GraphDatabase.Driver(
            uri,
            AuthTokens.Basic(user, password),
            config =>
            {
                config.WithMaxConnectionPoolSize(10);
                config.WithEncryptionLevel(
                    encrypted ? EncryptionLevel.Encrypted : EncryptionLevel.None);
            });
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        try
        {
            await _driver.VerifyConnectivityAsync();
            IsAvailable = true;
            AvailabilityError = null;

            // Create index on (artifactId, projectId) for O(1) node lookups
            await using var session = _driver.AsyncSession();
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    "CREATE INDEX artifact_id IF NOT EXISTS " +
                    "FOR (n:Artifact) ON (n.artifactId, n.projectId)");
            });
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            AvailabilityError = ex.Message;
        }
    }

    public async Task CleanupAsync(string clientId, string projectId)
    {
        if (!IsAvailable) return;
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (n:Artifact {projectId: $projectId, clientId: $clientId}) DETACH DELETE n",
                new { projectId, clientId });
        });
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task<long> WriteTreeAsync(SimulatedTree tree)
    {
        if (!IsAvailable) return -1;

        await using var session = _driver.AsyncSession();

        // Batch-create all nodes first
        const int batchSize = 500;
        var nodes = tree.Projection.ArtifactIndex.Values.ToList();

        for (int i = 0; i < nodes.Count; i += batchSize)
        {
            var batch = nodes.Skip(i).Take(batchSize)
                .Select(n => new
                {
                    artifactId = n.ArtifactId,
                    artifactType = n.ArtifactType.ToString(),
                    title = n.Title,
                    isEnabled = n.IsEnabled,
                    depth = n.Depth,
                    projectId = tree.ProjectId,
                    clientId = tree.ClientId,
                    parentId = n.ParentId ?? string.Empty,
                })
                .ToList();

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    "UNWIND $batch AS node " +
                    "CREATE (n:Artifact { " +
                    "  artifactId: node.artifactId, " +
                    "  artifactType: node.artifactType, " +
                    "  title: node.title, " +
                    "  isEnabled: node.isEnabled, " +
                    "  depth: node.depth, " +
                    "  projectId: node.projectId, " +
                    "  clientId: node.clientId, " +
                    "  parentId: node.parentId " +
                    "})",
                    new { batch });
            });
        }

        // Create PARENT relationships in batches
        var edges = tree.Projection.ArtifactIndex.Values
            .Where(n => n.ParentId != null)
            .Select(n => new { childId = n.ArtifactId, parentId = n.ParentId! })
            .ToList();

        for (int i = 0; i < edges.Count; i += batchSize)
        {
            var batch = edges.Skip(i).Take(batchSize).ToList();
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    "UNWIND $batch AS edge " +
                    "MATCH (child:Artifact {artifactId: edge.childId, projectId: $projectId}) " +
                    "MATCH (parent:Artifact {artifactId: edge.parentId, projectId: $projectId}) " +
                    "CREATE (child)-[:PARENT]->(parent)",
                    new { batch, projectId = tree.ProjectId });
            });
        }

        return await GetStorageSizeBytesAsync(tree.ClientId, tree.ProjectId);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<ArtifactTreeProjection?> ReadFullTreeAsync(
        string clientId, string projectId)
    {
        if (!IsAvailable) return null;

        await using var session = _driver.AsyncSession();

        // Fetch all nodes with their direct parent in one query
        var records = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:Artifact {projectId: $projectId, clientId: $clientId}) " +
                "OPTIONAL MATCH (n)-[:PARENT]->(p) " +
                "RETURN n.artifactId AS id, " +
                "       n.artifactType AS type, " +
                "       n.title AS title, " +
                "       n.isEnabled AS isEnabled, " +
                "       n.depth AS depth, " +
                "       p.artifactId AS parentId",
                new { projectId, clientId });
            return await cursor.ToListAsync();
        });

        if (records.Count == 0) return null;

        // Build projection from flat record set
        var proj = new ArtifactTreeProjection
        {
            ProjectId = projectId,
            ClientId = clientId,
        };

        // First pass: create all nodes
        foreach (var rec in records)
        {
            var id = rec["id"].As<string>();
            var parentId = rec["parentId"].As<string?>();

            proj.ArtifactIndex[id] = new ArtifactNode
            {
                ArtifactId = id,
                ArtifactType = Enum.Parse<ArtifactType>(rec["type"].As<string>()),
                ParentId = parentId,
                Title = rec["title"].As<string>(),
                IsEnabled = rec["isEnabled"].As<bool>(),
                Depth = (int)rec["depth"].As<long>(),
            };
        }

        // Second pass: wire children / ancestors
        foreach (var node in proj.ArtifactIndex.Values.Where(n => n.ParentId != null))
        {
            if (proj.ArtifactIndex.TryGetValue(node.ParentId!, out var parent))
            {
                parent.ChildrenIds.Add(node.ArtifactId);
                // Build ancestor list
                var anc = parent;
                while (anc != null)
                {
                    node.AncestorIds.Add(anc.ArtifactId);
                    anc.DescendantIds.Add(node.ArtifactId);
                    anc = anc.ParentId != null
                        ? proj.ArtifactIndex.GetValueOrDefault(anc.ParentId)
                        : null;
                }
            }
        }

        proj.RecalculateStats();
        return proj;
    }

    public async Task<ArtifactNode?> GetSingleNodeAsync(
        string clientId, string projectId, string artifactId)
    {
        if (!IsAvailable) return null;

        await using var session = _driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:Artifact {artifactId: $artifactId, projectId: $projectId, clientId: $clientId}) " +
                "OPTIONAL MATCH (n)-[:PARENT]->(p) " +
                "RETURN n.artifactId AS id, n.artifactType AS type, " +
                "       n.title AS title, n.isEnabled AS isEnabled, n.depth AS depth, " +
                "       p.artifactId AS parentId",
                new { artifactId, projectId, clientId });

            var rec = await cursor.SingleAsync();
            if (rec == null) return null;

            return new ArtifactNode
            {
                ArtifactId = rec["id"].As<string>(),
                ArtifactType = Enum.Parse<ArtifactType>(rec["type"].As<string>()),
                ParentId = rec["parentId"].As<string?>(),
                Title = rec["title"].As<string>(),
                IsEnabled = rec["isEnabled"].As<bool>(),
                Depth = (int)rec["depth"].As<long>(),
            };
        });
    }

    public async Task<IReadOnlyList<ArtifactNode>> GetChildrenAsync(
        string clientId, string projectId, string artifactId)
    {
        if (!IsAvailable) return [];

        await using var session = _driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (parent:Artifact {artifactId: $artifactId, projectId: $projectId})" +
                "<-[:PARENT]-(child:Artifact) " +
                "RETURN child.artifactId AS id, child.artifactType AS type, " +
                "       child.title AS title, child.isEnabled AS isEnabled, " +
                "       child.depth AS depth",
                new { artifactId, projectId });

            var recs = await cursor.ToListAsync();
            return (IReadOnlyList<ArtifactNode>)recs.Select(r => new ArtifactNode
            {
                ArtifactId = r["id"].As<string>(),
                ArtifactType = Enum.Parse<ArtifactType>(r["type"].As<string>()),
                ParentId = artifactId,
                Title = r["title"].As<string>(),
                IsEnabled = r["isEnabled"].As<bool>(),
                Depth = (int)r["depth"].As<long>(),
            }).ToList();
        });
    }

    public async Task<ArtifactNode?> GetParentAsync(
        string clientId, string projectId, string artifactId)
    {
        if (!IsAvailable) return null;

        await using var session = _driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (child:Artifact {artifactId: $artifactId, projectId: $projectId})" +
                "-[:PARENT]->(parent:Artifact) " +
                "RETURN parent.artifactId AS id, parent.artifactType AS type, " +
                "       parent.title AS title, parent.isEnabled AS isEnabled, " +
                "       parent.depth AS depth",
                new { artifactId, projectId });

            if (!await cursor.FetchAsync()) return null;
            var rec = cursor.Current;
            return new ArtifactNode
            {
                ArtifactId = rec["id"].As<string>(),
                ArtifactType = Enum.Parse<ArtifactType>(rec["type"].As<string>()),
                Title = rec["title"].As<string>(),
                IsEnabled = rec["isEnabled"].As<bool>(),
                Depth = (int)rec["depth"].As<long>(),
            };
        });
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    public async Task AddArtifactAsync(
        string clientId, string projectId,
        string artifactId, ArtifactType type,
        string? parentId, string title)
    {
        if (!IsAvailable) return;

        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Get parent depth
            int depth = 0;
            if (parentId != null)
            {
                var cursor = await tx.RunAsync(
                    "MATCH (p:Artifact {artifactId: $parentId, projectId: $projectId}) " +
                    "RETURN p.depth AS depth",
                    new { parentId, projectId });
                if (await cursor.FetchAsync())
                    depth = (int)cursor.Current["depth"].As<long>() + 1;
            }

            // Create node
            await tx.RunAsync(
                "CREATE (n:Artifact {" +
                "  artifactId: $artifactId, artifactType: $type, " +
                "  title: $title, isEnabled: true, depth: $depth, " +
                "  projectId: $projectId, clientId: $clientId, " +
                "  parentId: $pId " +
                "})",
                new
                {
                    artifactId, type = type.ToString(), title, depth,
                    projectId, clientId, pId = parentId ?? string.Empty,
                });

            // Create PARENT relationship
            if (parentId != null)
            {
                await tx.RunAsync(
                    "MATCH (child:Artifact {artifactId: $artifactId, projectId: $projectId}) " +
                    "MATCH (parent:Artifact {artifactId: $parentId, projectId: $projectId}) " +
                    "CREATE (child)-[:PARENT]->(parent)",
                    new { artifactId, parentId, projectId });
            }
        });
    }

    public async Task DeleteArtifactAsync(
        string clientId, string projectId,
        string artifactId, IReadOnlyList<string> descendantIds)
    {
        if (!IsAvailable) return;

        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Delete the artifact and all its descendants via graph traversal
            await tx.RunAsync(
                "MATCH (root:Artifact {artifactId: $artifactId, projectId: $projectId}) " +
                "OPTIONAL MATCH (root)<-[:PARENT*0..]-(descendant:Artifact) " +
                "DETACH DELETE root, descendant",
                new { artifactId, projectId });
        });
    }

    public async Task MoveArtifactAsync(
        string clientId, string projectId,
        string artifactId, string oldParentId, string newParentId)
    {
        if (!IsAvailable) return;

        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Remove old PARENT edge
            await tx.RunAsync(
                "MATCH (n:Artifact {artifactId: $artifactId, projectId: $projectId})" +
                "-[r:PARENT]->(old:Artifact {artifactId: $oldParentId}) " +
                "DELETE r",
                new { artifactId, oldParentId, projectId });

            // Get new parent depth to update depth
            var cursor = await tx.RunAsync(
                "MATCH (p:Artifact {artifactId: $newParentId, projectId: $projectId}) " +
                "RETURN p.depth AS depth",
                new { newParentId, projectId });
            int newDepth = 0;
            if (await cursor.FetchAsync())
                newDepth = (int)cursor.Current["depth"].As<long>() + 1;

            // Create new PARENT edge and update depth / parentId
            await tx.RunAsync(
                "MATCH (child:Artifact {artifactId: $artifactId, projectId: $projectId}) " +
                "MATCH (parent:Artifact {artifactId: $newParentId, projectId: $projectId}) " +
                "CREATE (child)-[:PARENT]->(parent) " +
                "SET child.depth = $depth, child.parentId = $newParentId",
                new { artifactId, newParentId, projectId, depth = newDepth });
        });
    }

    public async Task SetArtifactEnabledAsync(
        string clientId, string projectId,
        string artifactId, bool enabled,
        IReadOnlyList<string> affectedDescendantIds)
    {
        if (!IsAvailable) return;

        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Update target and all descendants via graph traversal
            await tx.RunAsync(
                "MATCH (n:Artifact {artifactId: $artifactId, projectId: $projectId}) " +
                "OPTIONAL MATCH (n)<-[:PARENT*0..]-(descendant:Artifact {projectId: $projectId}) " +
                "SET n.isEnabled = $enabled, descendant.isEnabled = $enabled",
                new { artifactId, projectId, enabled });
        });
    }

    public async Task ClearBranchAsync(
        string clientId, string projectId,
        string parentId, IReadOnlyList<string> clearedIds)
    {
        if (!IsAvailable) return;

        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Delete all direct children and their subtrees
            await tx.RunAsync(
                "MATCH (parent:Artifact {artifactId: $parentId, projectId: $projectId})" +
                "<-[:PARENT]-(child:Artifact) " +
                "OPTIONAL MATCH (child)<-[:PARENT*0..]-(descendant:Artifact) " +
                "DETACH DELETE child, descendant",
                new { parentId, projectId });
        });
    }

    // ── Storage size ──────────────────────────────────────────────────────────

    public async Task<long> GetStorageSizeBytesAsync(string clientId, string projectId)
    {
        // Rough storage estimates per graph element (bytes).
        const int AvgNodeBytes = 300;
        const int AvgRelationshipBytes = 50;

        if (!IsAvailable) return -1;

        await using var session = _driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (n:Artifact {projectId: $projectId, clientId: $clientId}) " +
                "OPTIONAL MATCH (n)-[r:PARENT]->() " +
                "RETURN count(DISTINCT n) AS nodeCount, count(r) AS relCount",
                new { projectId, clientId });

            if (!await cursor.FetchAsync()) return -1L;
            var rec = cursor.Current;
            var nodeCount = rec["nodeCount"].As<long>();
            var relCount = rec["relCount"].As<long>();

            return nodeCount * AvgNodeBytes + relCount * AvgRelationshipBytes;
        });
    }

    public async Task<IReadOnlyList<string>> GetStorageSnapshotLinesAsync(
        string clientId,
        string projectId,
        int maxItems = 8)
    {
        if (!IsAvailable) return ["Neo4j unavailable."];

        try
        {
            await using var session = _driver.AsyncSession();

            var counts = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    "MATCH (n:Artifact {projectId: $projectId, clientId: $clientId}) " +
                    "OPTIONAL MATCH (n)-[r:PARENT]->() " +
                    "RETURN count(DISTINCT n) AS nodeCount, count(r) AS relCount",
                    new { projectId, clientId });
                await cursor.FetchAsync();
                return cursor.Current;
            });

            var sample = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    "MATCH (n:Artifact {projectId: $projectId, clientId: $clientId}) " +
                    "RETURN n.artifactId AS id, n.artifactType AS type, n.parentId AS parentId, n.depth AS depth " +
                    "ORDER BY n.depth ASC, n.artifactId ASC " +
                    "LIMIT $limit",
                    new { projectId, clientId, limit = Math.Max(1, maxItems) });
                return await cursor.ToListAsync();
            });

            var lines = new List<string>
            {
                "Graph: :Artifact nodes and [:PARENT] edges",
                $"Nodes: {counts["nodeCount"].As<long>():N0}",
                $"Relationships: {counts["relCount"].As<long>():N0}",
                "Sample nodes:",
            };

            lines.AddRange(sample.Select(r =>
                $"{r["id"].As<string>()}: type={r["type"].As<string>()} parentId={r["parentId"].As<string>()} depth={r["depth"].As<long>()}"));

            return lines;
        }
        catch (Exception ex)
        {
            return [$"Failed to fetch Neo4j snapshot: {ex.Message}"];
        }
    }

    public async Task<IReadOnlyList<string>> GetSampleJsonEntriesAsync(
        string clientId,
        string projectId,
        int maxItems = 3)
    {
        if (!IsAvailable) return ["{ \"error\": \"Neo4j unavailable\" }"];

        try
        {
            await using var session = _driver.AsyncSession();
            var sample = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    "MATCH (n:Artifact {projectId: $projectId, clientId: $clientId}) " +
                    "RETURN n " +
                    "ORDER BY n.depth ASC, n.artifactId ASC " +
                    "LIMIT $limit",
                    new { projectId, clientId, limit = Math.Max(1, maxItems) });
                return await cursor.ToListAsync();
            });

            return sample.Select(record =>
            {
                var node = record["n"].As<INode>();
                var payload = node.Properties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            }).ToList();
        }
        catch (Exception ex)
        {
            return [$"{{ \"error\": \"{ex.Message.Replace("\"", "\\\"")}\" }}"];
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await _driver.DisposeAsync();
    }
}

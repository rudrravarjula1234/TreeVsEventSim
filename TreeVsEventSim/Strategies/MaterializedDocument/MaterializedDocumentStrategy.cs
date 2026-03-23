using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using TreeVsEventSim.Models;
using TreeVsEventSim.Simulation;
using TreeVsEventSim.Strategies;

namespace TreeVsEventSim.Strategies.MaterializedDocument;

// ── MongoDB document model ────────────────────────────────────────────────────

internal sealed class MaterializedTreeDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [BsonElement("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [BsonElement("version")]
    public int Version { get; set; }

    [BsonElement("lastUpdated")]
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// The artifact index serialized as a BSON document (artifactId → node data).
    /// Stored as a flat dictionary so individual nodes can be updated in-place via
    /// MongoDB's dot-notation field paths.
    /// </summary>
    [BsonElement("artifactIndex")]
    public Dictionary<string, ArtifactNodeDto> ArtifactIndex { get; set; } = [];
}

internal sealed class ArtifactNodeDto
{
    [BsonElement("artifactId")]
    public string ArtifactId { get; set; } = string.Empty;

    [BsonElement("artifactType")]
    public string ArtifactType { get; set; } = string.Empty;

    [BsonElement("parentId")]
    public string? ParentId { get; set; }

    [BsonElement("depth")]
    public int Depth { get; set; }

    [BsonElement("childrenIds")]
    public List<string> ChildrenIds { get; set; } = [];

    [BsonElement("descendantIds")]
    public List<string> DescendantIds { get; set; } = [];

    [BsonElement("ancestorIds")]
    public List<string> AncestorIds { get; set; } = [];

    [BsonElement("title")]
    public string Title { get; set; } = string.Empty;

    [BsonElement("isEnabled")]
    public bool IsEnabled { get; set; } = true;
}

// ── Strategy ──────────────────────────────────────────────────────────────────

/// <summary>
/// Strategy 2 — Materialized Tree Document (MongoDB single collection).
///
/// The complete ArtifactTreeProjection is stored as ONE MongoDB document per project.
/// Mutations update individual fields in-place using MongoDB's update operators —
/// no event log, no replay overhead.
/// </summary>
public sealed class MaterializedDocumentStrategy : IStorageStrategy
{
    private readonly IMongoClient _client;
    private IMongoCollection<MaterializedTreeDocument> _collection = null!;

    public string Name => "MaterializedDoc";
    public string Description =>
        "MongoDB — full tree stored as a single document; in-place updates";

    public MaterializedDocumentStrategy(string connectionString)
    {
        _client = new MongoClient(connectionString);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        var db = _client.GetDatabase("TreeBenchmark");
        _collection = db.GetCollection<MaterializedTreeDocument>("MaterializedTrees");

        await _collection.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<MaterializedTreeDocument>(
                Builders<MaterializedTreeDocument>.IndexKeys
                    .Ascending(d => d.ClientId)
                    .Ascending(d => d.ProjectId),
                new CreateIndexOptions { Unique = true, Name = "idx_client_project" }),
        ]);
    }

    public async Task CleanupAsync(string clientId, string projectId)
    {
        var filter = DocFilter(clientId, projectId);
        await _collection.DeleteManyAsync(filter);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task<long> WriteTreeAsync(SimulatedTree tree)
    {
        var index = new Dictionary<string, ArtifactNodeDto>(
            tree.Projection.ArtifactIndex.Count);

        foreach (var (id, node) in tree.Projection.ArtifactIndex)
        {
            index[EscapeKey(id)] = ToDto(node);
        }

        var doc = new MaterializedTreeDocument
        {
            ClientId = tree.ClientId,
            ProjectId = tree.ProjectId,
            Version = tree.Projection.Version,
            LastUpdated = DateTime.UtcNow,
            ArtifactIndex = index,
        };

        await _collection.ReplaceOneAsync(
            DocFilter(tree.ClientId, tree.ProjectId),
            doc,
            new ReplaceOptions { IsUpsert = true });

        return await GetStorageSizeBytesAsync(tree.ClientId, tree.ProjectId);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<ArtifactTreeProjection?> ReadFullTreeAsync(
        string clientId, string projectId)
    {
        var doc = await _collection
            .Find(DocFilter(clientId, projectId))
            .FirstOrDefaultAsync();
        return doc == null ? null : ToProjection(doc);
    }

    public async Task<ArtifactNode?> GetSingleNodeAsync(
        string clientId, string projectId, string artifactId)
    {
        // Use a projection to fetch only the specific node field
        var fieldPath = $"artifactIndex.{EscapeKey(artifactId)}";
        var proj = Builders<MaterializedTreeDocument>.Projection.Include(fieldPath);

        var doc = await _collection
            .Find(DocFilter(clientId, projectId))
            .Project<MaterializedTreeDocument>(proj)
            .FirstOrDefaultAsync();

        if (doc?.ArtifactIndex == null) return null;
        var key = EscapeKey(artifactId);
        return doc.ArtifactIndex.TryGetValue(key, out var dto) ? ToNode(dto) : null;
    }

    public async Task<IReadOnlyList<ArtifactNode>> GetChildrenAsync(
        string clientId, string projectId, string artifactId)
    {
        var parent = await GetSingleNodeAsync(clientId, projectId, artifactId);
        if (parent == null) return [];

        // Fetch only the child node fields
        var projBuilder = Builders<MaterializedTreeDocument>.Projection;
        var proj = projBuilder.Include($"artifactIndex.{EscapeKey(artifactId)}");
        foreach (var childId in parent.ChildrenIds)
            proj = proj.Include($"artifactIndex.{EscapeKey(childId)}");

        var doc = await _collection
            .Find(DocFilter(clientId, projectId))
            .Project<MaterializedTreeDocument>(proj)
            .FirstOrDefaultAsync();

        if (doc?.ArtifactIndex == null) return [];

        return parent.ChildrenIds
            .Select(id =>
            {
                var dto = doc.ArtifactIndex.GetValueOrDefault(EscapeKey(id));
                return dto != null ? ToNode(dto) : null;
            })
            .OfType<ArtifactNode>()
            .ToList();
    }

    public async Task<ArtifactNode?> GetParentAsync(
        string clientId, string projectId, string artifactId)
    {
        var node = await GetSingleNodeAsync(clientId, projectId, artifactId);
        if (node?.ParentId == null) return null;
        return await GetSingleNodeAsync(clientId, projectId, node.ParentId);
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    public async Task AddArtifactAsync(
        string clientId, string projectId,
        string artifactId, ArtifactType type,
        string? parentId, string title)
    {
        int depth = 0;
        var ancestors = new List<string>();

        if (parentId != null)
        {
            var parent = await GetSingleNodeAsync(clientId, projectId, parentId);
            if (parent != null)
            {
                depth = parent.Depth + 1;
                ancestors.Add(parentId);
                ancestors.AddRange(parent.AncestorIds);
            }
        }

        var dto = new ArtifactNodeDto
        {
            ArtifactId = artifactId,
            ArtifactType = type.ToString(),
            ParentId = parentId,
            Depth = depth,
            AncestorIds = ancestors,
            Title = title,
            IsEnabled = true,
        };

        var key = EscapeKey(artifactId);
        var filter = DocFilter(clientId, projectId);
        var update = Builders<MaterializedTreeDocument>.Update
            .Set($"artifactIndex.{key}", dto)
            .CurrentDate("lastUpdated")
            .Inc(d => d.Version, 1);

        // Add to parent's children and propagate descendant up ancestor chain
        if (parentId != null)
        {
            var parentKey = EscapeKey(parentId);
            update = update.Push($"artifactIndex.{parentKey}.childrenIds", artifactId);
            foreach (var ancId in ancestors)
                update = update.Push(
                    $"artifactIndex.{EscapeKey(ancId)}.descendantIds", artifactId);
        }

        await _collection.UpdateOneAsync(filter, update);
    }

    public async Task DeleteArtifactAsync(
        string clientId, string projectId,
        string artifactId, IReadOnlyList<string> descendantIds)
    {
        var node = await GetSingleNodeAsync(clientId, projectId, artifactId);
        if (node == null) return;

        var allIds = new List<string> { artifactId };
        allIds.AddRange(descendantIds);

        var filter = DocFilter(clientId, projectId);
        var update = Builders<MaterializedTreeDocument>.Update
            .CurrentDate("lastUpdated")
            .Inc(d => d.Version, 1);

        // Remove from parent's childrenIds
        if (node.ParentId != null)
        {
            var pKey = EscapeKey(node.ParentId);
            update = update.Pull($"artifactIndex.{pKey}.childrenIds", artifactId);
        }

        // Remove all deleted IDs from each ancestor's descendantIds in one PullAll call
        foreach (var ancId in node.AncestorIds)
        {
            var aKey = EscapeKey(ancId);
            update = update.PullAll($"artifactIndex.{aKey}.descendantIds", allIds);
        }

        // Unset all deleted nodes
        foreach (var id in allIds)
            update = update.Unset($"artifactIndex.{EscapeKey(id)}");

        await _collection.UpdateOneAsync(filter, update);
    }

    public async Task MoveArtifactAsync(
        string clientId, string projectId,
        string artifactId, string oldParentId, string newParentId)
    {
        var doc = await ReadFullTreeAsync(clientId, projectId);
        if (doc == null || !doc.ArtifactIndex.TryGetValue(artifactId, out var moved)) return;

        var movedAndDesc = new List<string> { artifactId };
        movedAndDesc.AddRange(moved.DescendantIds);

        var filter = DocFilter(clientId, projectId);

        // ── Step 1: remove from old parent / ancestors ─────────────────────
        var removeUpdate = Builders<MaterializedTreeDocument>.Update
            .CurrentDate("lastUpdated")
            .Inc(d => d.Version, 1);

        var oldKey = EscapeKey(oldParentId);
        removeUpdate = removeUpdate.Pull(
            $"artifactIndex.{oldKey}.childrenIds", artifactId);

        foreach (var ancId in moved.AncestorIds)
        {
            var aKey = EscapeKey(ancId);
            removeUpdate = removeUpdate.PullAll(
                $"artifactIndex.{aKey}.descendantIds", movedAndDesc);
        }

        await _collection.UpdateOneAsync(filter, removeUpdate);

        // ── Step 2: add to new parent / ancestors + update node metadata ────
        var newParent = doc.ArtifactIndex.GetValueOrDefault(newParentId);
        var newAncestors = new List<string> { newParentId };
        if (newParent != null) newAncestors.AddRange(newParent.AncestorIds);

        var addUpdate = Builders<MaterializedTreeDocument>.Update
            .CurrentDate("lastUpdated");

        var newKey = EscapeKey(newParentId);
        addUpdate = addUpdate.Push($"artifactIndex.{newKey}.childrenIds", artifactId);

        // Use PushEach to push all movedAndDesc IDs in a single $push per ancestor
        foreach (var ancId in newAncestors)
        {
            var aKey = EscapeKey(ancId);
            addUpdate = addUpdate.PushEach(
                $"artifactIndex.{aKey}.descendantIds", movedAndDesc);
        }

        var mKey = EscapeKey(artifactId);
        addUpdate = addUpdate
            .Set($"artifactIndex.{mKey}.parentId", newParentId)
            .Set($"artifactIndex.{mKey}.ancestorIds", newAncestors)
            .Set($"artifactIndex.{mKey}.depth",
                 newParent != null ? newParent.Depth + 1 : 0);

        await _collection.UpdateOneAsync(filter, addUpdate);
    }

    public async Task SetArtifactEnabledAsync(
        string clientId, string projectId,
        string artifactId, bool enabled,
        IReadOnlyList<string> affectedDescendantIds)
    {
        var filter = DocFilter(clientId, projectId);
        var update = Builders<MaterializedTreeDocument>.Update
            .Set($"artifactIndex.{EscapeKey(artifactId)}.isEnabled", enabled)
            .CurrentDate("lastUpdated")
            .Inc(d => d.Version, 1);

        foreach (var id in affectedDescendantIds)
            update = update.Set($"artifactIndex.{EscapeKey(id)}.isEnabled", enabled);

        await _collection.UpdateOneAsync(filter, update);
    }

    public async Task ClearBranchAsync(
        string clientId, string projectId,
        string parentId, IReadOnlyList<string> clearedIds)
    {
        var parent = await GetSingleNodeAsync(clientId, projectId, parentId);
        if (parent == null) return;

        var filter = DocFilter(clientId, projectId);
        var update = Builders<MaterializedTreeDocument>.Update
            .CurrentDate("lastUpdated")
            .Inc(d => d.Version, 1);

        // Clear parent's children/descendants
        var pKey = EscapeKey(parentId);
        update = update
            .Set($"artifactIndex.{pKey}.childrenIds", new List<string>())
            .Set($"artifactIndex.{pKey}.descendantIds", new List<string>());

        // Remove cleared nodes from ancestor descendant lists using PullAll
        if (clearedIds.Count > 0)
        {
            foreach (var ancId in parent.AncestorIds)
            {
                var aKey = EscapeKey(ancId);
                update = update.PullAll(
                    $"artifactIndex.{aKey}.descendantIds", clearedIds);
            }
        }

        // Unset cleared nodes
        foreach (var id in clearedIds)
            update = update.Unset($"artifactIndex.{EscapeKey(id)}");

        await _collection.UpdateOneAsync(filter, update);
    }

    // ── Storage size ──────────────────────────────────────────────────────────

    public async Task<long> GetStorageSizeBytesAsync(string clientId, string projectId)
    {
        try
        {
            // Fetch only the BSON size by counting documents and estimating
            var pipeline = new[]
            {
                new BsonDocument("$match", new BsonDocument
                {
                    ["clientId"] = clientId,
                    ["projectId"] = projectId,
                }),
                new BsonDocument("$project", new BsonDocument
                {
                    ["size"] = new BsonDocument("$bsonSize", "$$ROOT"),
                }),
            };

            var db = _client.GetDatabase("TreeBenchmark");
            var result = await db
                .GetCollection<BsonDocument>("MaterializedTrees")
                .Aggregate<BsonDocument>(pipeline)
                .FirstOrDefaultAsync();

            return result?["size"].ToInt64() ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        (_client as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static FilterDefinition<MaterializedTreeDocument> DocFilter(
        string clientId, string projectId) =>
        Builders<MaterializedTreeDocument>.Filter.And(
            Builders<MaterializedTreeDocument>.Filter.Eq(d => d.ClientId, clientId),
            Builders<MaterializedTreeDocument>.Filter.Eq(d => d.ProjectId, projectId));

    /// <summary>
    /// MongoDB field names cannot contain '.' or start with '$'.
    /// This method replaces those characters with '_'.
    /// <para>
    /// Note: artifact IDs in this application are either GUIDs (e.g. "abc-123") or
    /// structured strings (e.g. "epic-0", "feat-0-0") which do not contain '.' or '$',
    /// so no key collisions arise in practice. If IDs could contain underscores alongside
    /// dots/dollars a more robust encoding (e.g. percent-encoding) would be required.
    /// </para>
    /// </summary>
    private static string EscapeKey(string key) => key.Replace('.', '_').Replace('$', '_');

    private static ArtifactNodeDto ToDto(ArtifactNode n) => new()
    {
        ArtifactId = n.ArtifactId,
        ArtifactType = n.ArtifactType.ToString(),
        ParentId = n.ParentId,
        Depth = n.Depth,
        ChildrenIds = [.. n.ChildrenIds],
        DescendantIds = [.. n.DescendantIds],
        AncestorIds = [.. n.AncestorIds],
        Title = n.Title,
        IsEnabled = n.IsEnabled,
    };

    private static ArtifactNode ToNode(ArtifactNodeDto dto) => new()
    {
        ArtifactId = dto.ArtifactId,
        ArtifactType = Enum.Parse<ArtifactType>(dto.ArtifactType),
        ParentId = dto.ParentId,
        Depth = dto.Depth,
        ChildrenIds = [.. dto.ChildrenIds],
        DescendantIds = [.. dto.DescendantIds],
        AncestorIds = [.. dto.AncestorIds],
        Title = dto.Title,
        IsEnabled = dto.IsEnabled,
    };

    private static ArtifactTreeProjection ToProjection(MaterializedTreeDocument doc)
    {
        var proj = new ArtifactTreeProjection
        {
            ProjectId = doc.ProjectId,
            ClientId = doc.ClientId,
            Version = doc.Version,
            LastUpdated = doc.LastUpdated,
        };

        foreach (var (_, dto) in doc.ArtifactIndex)
        {
            var node = ToNode(dto);
            proj.ArtifactIndex[node.ArtifactId] = node;
        }

        proj.RecalculateStats();
        return proj;
    }
}

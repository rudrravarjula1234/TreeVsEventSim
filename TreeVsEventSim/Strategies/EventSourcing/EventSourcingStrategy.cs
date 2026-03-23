using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using TreeVsEventSim.Models;
using TreeVsEventSim.Simulation;
using TreeVsEventSim.Strategies;

namespace TreeVsEventSim.Strategies.EventSourcing;

/// <summary>
/// Strategy 1 — Event Sourcing (MongoDB EventStore collection).
///
/// Write path: each mutation is appended as an immutable event document.
/// Read path:  query all events for a project, replay in order → projection.
/// This mirrors the production Anvian design described in the problem statement.
/// </summary>
public sealed class EventSourcingStrategy : IStorageStrategy, IStorageSnapshotProvider
{
    private readonly IMongoClient _client;
    private IMongoCollection<EventStoreDocument> _collection = null!;

    public string Name => "EventSourcing";
    public string Description =>
        "MongoDB — append-only event log; full replay on every read";

    public EventSourcingStrategy(string connectionString)
    {
        _client = new MongoClient(connectionString);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        var db = _client.GetDatabase("TreeBenchmark");
        _collection = db.GetCollection<EventStoreDocument>("EventStore");

        // Create the five indexes described in the problem statement
        var idxOptions = new CreateIndexOptions { Background = true };

        await _collection.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<EventStoreDocument>(
                Builders<EventStoreDocument>.IndexKeys
                    .Ascending(e => e.ClientId)
                    .Ascending(e => e.AggregateId),
                new CreateIndexOptions { Background = true, Name = "idx_client_agg" }),

            new CreateIndexModel<EventStoreDocument>(
                Builders<EventStoreDocument>.IndexKeys
                    .Ascending(e => e.ClientId)
                    .Ascending(e => e.AggregateType),
                new CreateIndexOptions { Background = true, Name = "idx_client_type" }),

            new CreateIndexModel<EventStoreDocument>(
                Builders<EventStoreDocument>.IndexKeys
                    .Ascending(e => e.ClientId)
                    .Ascending(e => e.AggregateId)
                    .Ascending(e => e.AggregateType),
                new CreateIndexOptions { Background = true, Name = "idx_client_agg_type" }),

            new CreateIndexModel<EventStoreDocument>(
                Builders<EventStoreDocument>.IndexKeys
                    .Ascending(e => e.ClientId)
                    .Ascending(e => e.AggregateId)
                    .Ascending(e => e.AggregateType)
                    .Ascending(e => e.EventType),
                new CreateIndexOptions { Background = true, Name = "idx_client_agg_type_evt" }),

            new CreateIndexModel<EventStoreDocument>(
                Builders<EventStoreDocument>.IndexKeys
                    .Ascending(e => e.ClientId)
                    .Ascending(e => e.EventType)
                    .Ascending(e => e.AggregateId),
                new CreateIndexOptions { Background = true, Name = "idx_client_evt_agg" }),
        ]);
    }

    public async Task CleanupAsync(string clientId, string projectId)
    {
        var filter = Builders<EventStoreDocument>.Filter.And(
            Builders<EventStoreDocument>.Filter.Eq(e => e.ClientId, clientId),
            Builders<EventStoreDocument>.Filter.Eq(e => e.AggregateId, projectId));
        await _collection.DeleteManyAsync(filter);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task<long> WriteTreeAsync(SimulatedTree tree)
    {
        int version = 0;
        var events = new List<EventStoreDocument>(tree.AddEvents.Count);

        foreach (var (node, parentId) in tree.AddEvents)
        {
            var payload = new BsonDocument
            {
                ["artifactId"] = node.ArtifactId,
                ["artifactType"] = node.ArtifactType.ToString(),
                ["parentId"] = parentId != null ? (BsonValue)parentId : BsonNull.Value,
                ["title"] = node.Title,
                ["isEnabled"] = node.IsEnabled,
                ["occurredOn"] = DateTime.UtcNow,
            };

            events.Add(new EventStoreDocument
            {
                AggregateId = tree.ProjectId,
                AggregateType = "ArtifactTree",
                EventType = "ArtifactAdded",
                EventData = payload,
                Version = ++version,
                OccurredOn = DateTime.UtcNow,
                ClientId = tree.ClientId,
            });
        }

        await _collection.InsertManyAsync(events);
        return await GetStorageSizeBytesAsync(tree.ClientId, tree.ProjectId);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<ArtifactTreeProjection?> ReadFullTreeAsync(
        string clientId, string projectId)
    {
        var rawEvents = await FetchEventsAsync(clientId, projectId);
        if (rawEvents.Count == 0) return null;
        return ReplayEvents(clientId, projectId, rawEvents);
    }

    public async Task<ArtifactNode?> GetSingleNodeAsync(
        string clientId, string projectId, string artifactId)
    {
        // Current implementation replays full tree then extracts — same as production TODO
        var proj = await ReadFullTreeAsync(clientId, projectId);
        return proj?.ArtifactIndex.GetValueOrDefault(artifactId);
    }

    public async Task<IReadOnlyList<ArtifactNode>> GetChildrenAsync(
        string clientId, string projectId, string artifactId)
    {
        var proj = await ReadFullTreeAsync(clientId, projectId);
        if (proj == null) return [];
        if (!proj.ArtifactIndex.TryGetValue(artifactId, out var node)) return [];
        return node.ChildrenIds
            .Select(id => proj.ArtifactIndex.GetValueOrDefault(id))
            .OfType<ArtifactNode>()
            .ToList();
    }

    public async Task<ArtifactNode?> GetParentAsync(
        string clientId, string projectId, string artifactId)
    {
        var proj = await ReadFullTreeAsync(clientId, projectId);
        if (proj == null) return null;
        if (!proj.ArtifactIndex.TryGetValue(artifactId, out var node)) return null;
        if (node.ParentId == null) return null;
        return proj.ArtifactIndex.GetValueOrDefault(node.ParentId);
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    public async Task AddArtifactAsync(
        string clientId, string projectId,
        string artifactId, ArtifactType type,
        string? parentId, string title)
    {
        var version = await NextVersionAsync(clientId, projectId);
        var payload = new BsonDocument
        {
            ["artifactId"] = artifactId,
            ["artifactType"] = type.ToString(),
            ["parentId"] = parentId != null ? (BsonValue)parentId : BsonNull.Value,
            ["title"] = title,
            ["isEnabled"] = true,
            ["occurredOn"] = DateTime.UtcNow,
        };
        await AppendEventAsync(clientId, projectId, "ArtifactAdded", payload, version);
    }

    public async Task DeleteArtifactAsync(
        string clientId, string projectId,
        string artifactId, IReadOnlyList<string> descendantIds)
    {
        var version = await NextVersionAsync(clientId, projectId);
        var deletedArr = new BsonArray(descendantIds.Select(id => (BsonValue)id));
        var payload = new BsonDocument
        {
            ["artifactId"] = artifactId,
            ["deletedDescendantIds"] = deletedArr,
            ["occurredOn"] = DateTime.UtcNow,
        };
        await AppendEventAsync(clientId, projectId, "ArtifactDeleted", payload, version);
    }

    public async Task MoveArtifactAsync(
        string clientId, string projectId,
        string artifactId, string oldParentId, string newParentId)
    {
        var version = await NextVersionAsync(clientId, projectId);
        var payload = new BsonDocument
        {
            ["artifactId"] = artifactId,
            ["oldParentId"] = oldParentId,
            ["newParentId"] = newParentId,
            ["occurredOn"] = DateTime.UtcNow,
        };
        await AppendEventAsync(clientId, projectId, "ArtifactMoved", payload, version);
    }

    public async Task SetArtifactEnabledAsync(
        string clientId, string projectId,
        string artifactId, bool enabled,
        IReadOnlyList<string> affectedDescendantIds)
    {
        var version = await NextVersionAsync(clientId, projectId);
        var affectedArr = new BsonArray(affectedDescendantIds.Select(id => (BsonValue)id));
        var payload = new BsonDocument
        {
            ["artifactId"] = artifactId,
            ["enabled"] = enabled,
            ["affectedDescendantIds"] = affectedArr,
            ["occurredOn"] = DateTime.UtcNow,
        };
        var evtType = enabled ? "ArtifactEnabled" : "ArtifactDisabled";
        await AppendEventAsync(clientId, projectId, evtType, payload, version);
    }

    public async Task ClearBranchAsync(
        string clientId, string projectId,
        string parentId, IReadOnlyList<string> clearedIds)
    {
        var version = await NextVersionAsync(clientId, projectId);
        var clearedArr = new BsonArray(clearedIds.Select(id => (BsonValue)id));
        var payload = new BsonDocument
        {
            ["parentId"] = parentId,
            ["clearedArtifactIds"] = clearedArr,
            ["occurredOn"] = DateTime.UtcNow,
        };
        await AppendEventAsync(clientId, projectId, "BranchCleared", payload, version);
    }

    // ── Storage size ──────────────────────────────────────────────────────────

    public async Task<long> GetStorageSizeBytesAsync(string clientId, string projectId)
    {
        // Estimated average event document size in bytes (payload + BSON overhead).
        const int AvgEventDocumentBytes = 400;

        try
        {
            var filter = Builders<EventStoreDocument>.Filter.And(
                Builders<EventStoreDocument>.Filter.Eq(e => e.ClientId, clientId),
                Builders<EventStoreDocument>.Filter.Eq(e => e.AggregateId, projectId));

            var count = await _collection.CountDocumentsAsync(filter);
            return count * AvgEventDocumentBytes;
        }
        catch
        {
            return -1;
        }
    }

    public async Task<IReadOnlyList<string>> GetStorageSnapshotLinesAsync(
        string clientId,
        string projectId,
        int maxItems = 8)
    {
        try
        {
            var filter = Builders<EventStoreDocument>.Filter.And(
                Builders<EventStoreDocument>.Filter.Eq(e => e.ClientId, clientId),
                Builders<EventStoreDocument>.Filter.Eq(e => e.AggregateId, projectId),
                Builders<EventStoreDocument>.Filter.Eq(e => e.AggregateType, "ArtifactTree"));

            var total = await _collection.CountDocumentsAsync(filter);
            var docs = await _collection.Find(filter)
                .SortBy(e => e.Version)
                .Limit(Math.Max(1, maxItems))
                .ToListAsync();

            var lines = new List<string>
            {
                $"Collection: EventStore",
                $"Total events: {total:N0}",
                "Sample events:",
            };

            foreach (var doc in docs)
            {
                var artifactId = doc.EventData.TryGetValue("artifactId", out var idVal)
                    ? idVal.ToString()
                    : "-";
                lines.Add(
                    $"v{doc.Version}: {doc.EventType} artifactId={artifactId} occurredOn={doc.OccurredOn:O}");
            }

            return lines;
        }
        catch (Exception ex)
        {
            return [$"Failed to fetch EventStore snapshot: {ex.Message}"];
        }
    }

    public async Task<IReadOnlyList<string>> GetSampleJsonEntriesAsync(
        string clientId,
        string projectId,
        int maxItems = 3)
    {
        try
        {
            var filter = Builders<EventStoreDocument>.Filter.And(
                Builders<EventStoreDocument>.Filter.Eq(e => e.ClientId, clientId),
                Builders<EventStoreDocument>.Filter.Eq(e => e.AggregateId, projectId),
                Builders<EventStoreDocument>.Filter.Eq(e => e.AggregateType, "ArtifactTree"));

            var docs = await _collection.Find(filter)
                .SortBy(e => e.Version)
                .Limit(Math.Max(1, maxItems))
                .ToListAsync();

            return docs
                .Select(doc => doc.ToBsonDocument().ToJson(new JsonWriterSettings { Indent = true }))
                .ToList();
        }
        catch (Exception ex)
        {
            return [$"{{ \"error\": \"{ex.Message.Replace("\"", "\\\"")}\" }}"];
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        (_client as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<List<EventStoreDocument>> FetchEventsAsync(
        string clientId, string projectId)
    {
        var filter = Builders<EventStoreDocument>.Filter.And(
            Builders<EventStoreDocument>.Filter.Eq(e => e.ClientId, clientId),
            Builders<EventStoreDocument>.Filter.Eq(e => e.AggregateId, projectId),
            Builders<EventStoreDocument>.Filter.Eq(e => e.AggregateType, "ArtifactTree"));

        var sort = Builders<EventStoreDocument>.Sort
            .Ascending(e => e.OccurredOn)
            .Ascending(e => e.Version);

        return await _collection.Find(filter).Sort(sort).ToListAsync();
    }

    private static ArtifactTreeProjection ReplayEvents(
        string clientId,
        string projectId,
        List<EventStoreDocument> events)
    {
        var proj = new ArtifactTreeProjection
        {
            ProjectId = projectId,
            ClientId = clientId,
        };

        foreach (var evt in events)
        {
            var data = evt.EventData;

            switch (evt.EventType)
            {
                case "ArtifactAdded":
                {
                    var artifactId = data["artifactId"].AsString;
                    var parentId = data["parentId"] == BsonNull.Value
                        ? null
                        : data["parentId"].AsString;

                    int depth = 0;
                    var ancestors = new List<string>();
                    if (parentId != null && proj.ArtifactIndex.TryGetValue(parentId, out var parent))
                    {
                        depth = parent.Depth + 1;
                        ancestors.Add(parentId);
                        ancestors.AddRange(parent.AncestorIds);

                        parent.ChildrenIds.Add(artifactId);
                        foreach (var ancId in ancestors)
                        {
                            if (proj.ArtifactIndex.TryGetValue(ancId, out var anc))
                                anc.DescendantIds.Add(artifactId);
                        }
                    }

                    proj.ArtifactIndex[artifactId] = new ArtifactNode
                    {
                        ArtifactId = artifactId,
                        ArtifactType = Enum.Parse<ArtifactType>(data["artifactType"].AsString),
                        ParentId = parentId,
                        Depth = depth,
                        AncestorIds = ancestors,
                        Title = data["title"].AsString,
                        IsEnabled = data["isEnabled"].AsBoolean,
                    };
                    break;
                }

                case "ArtifactDeleted":
                {
                    var artifactId = data["artifactId"].AsString;
                    var descendantIds = data["deletedDescendantIds"]
                        .AsBsonArray.Select(v => v.AsString).ToList();
                    var allIds = new List<string> { artifactId };
                    allIds.AddRange(descendantIds);

                    if (proj.ArtifactIndex.TryGetValue(artifactId, out var deleted))
                    {
                        // Remove from parent's children
                        if (deleted.ParentId != null
                            && proj.ArtifactIndex.TryGetValue(deleted.ParentId, out var delParent))
                            delParent.ChildrenIds.Remove(artifactId);

                        // Remove from all ancestors' descendant lists
                        foreach (var ancId in deleted.AncestorIds)
                        {
                            if (proj.ArtifactIndex.TryGetValue(ancId, out var anc))
                                foreach (var id in allIds)
                                    anc.DescendantIds.Remove(id);
                        }
                    }

                    foreach (var id in allIds)
                        proj.ArtifactIndex.Remove(id);
                    break;
                }

                case "ArtifactMoved":
                {
                    var artifactId = data["artifactId"].AsString;
                    var oldParentId = data["oldParentId"].AsString;
                    var newParentId = data["newParentId"].AsString;

                    if (!proj.ArtifactIndex.TryGetValue(artifactId, out var moved)) break;
                    var movedAndDescendants = new List<string> { artifactId };
                    movedAndDescendants.AddRange(moved.DescendantIds);

                    // Remove from old parent
                    if (proj.ArtifactIndex.TryGetValue(oldParentId, out var oldPar))
                    {
                        oldPar.ChildrenIds.Remove(artifactId);
                        foreach (var ancId in moved.AncestorIds)
                        {
                            if (proj.ArtifactIndex.TryGetValue(ancId, out var anc))
                                foreach (var id in movedAndDescendants)
                                    anc.DescendantIds.Remove(id);
                        }
                    }

                    // Attach to new parent
                    if (proj.ArtifactIndex.TryGetValue(newParentId, out var newPar))
                    {
                        newPar.ChildrenIds.Add(artifactId);
                        var newAncestors = new List<string> { newParentId };
                        newAncestors.AddRange(newPar.AncestorIds);

                        moved.ParentId = newParentId;
                        moved.AncestorIds = newAncestors;
                        moved.Depth = newPar.Depth + 1;

                        foreach (var id in movedAndDescendants)
                        {
                            foreach (var ancId in newAncestors)
                            {
                                if (proj.ArtifactIndex.TryGetValue(ancId, out var anc))
                                    anc.DescendantIds.Add(id);
                            }
                        }
                    }
                    break;
                }

                case "ArtifactEnabled":
                case "ArtifactDisabled":
                {
                    var artifactId = data["artifactId"].AsString;
                    var enabled = data["enabled"].AsBoolean;
                    var affected = data["affectedDescendantIds"]
                        .AsBsonArray.Select(v => v.AsString);

                    if (proj.ArtifactIndex.TryGetValue(artifactId, out var tog))
                        tog.IsEnabled = enabled;
                    foreach (var id in affected)
                        if (proj.ArtifactIndex.TryGetValue(id, out var aff))
                            aff.IsEnabled = enabled;
                    break;
                }

                case "BranchCleared":
                {
                    var parentId = data["parentId"].AsString;
                    var cleared = data["clearedArtifactIds"]
                        .AsBsonArray.Select(v => v.AsString).ToList();

                    if (proj.ArtifactIndex.TryGetValue(parentId, out var parent2))
                    {
                        foreach (var ancId in parent2.AncestorIds)
                        {
                            if (proj.ArtifactIndex.TryGetValue(ancId, out var anc))
                                foreach (var id in cleared)
                                    anc.DescendantIds.Remove(id);
                        }
                        parent2.ChildrenIds.Clear();
                        parent2.DescendantIds.Clear();
                    }

                    foreach (var id in cleared)
                        proj.ArtifactIndex.Remove(id);
                    break;
                }
            }

            proj.Version = evt.Version;
        }

        proj.RecalculateStats();
        return proj;
    }

    private async Task<int> NextVersionAsync(string clientId, string projectId)
    {
        var filter = Builders<EventStoreDocument>.Filter.And(
            Builders<EventStoreDocument>.Filter.Eq(e => e.ClientId, clientId),
            Builders<EventStoreDocument>.Filter.Eq(e => e.AggregateId, projectId));

        var sort = Builders<EventStoreDocument>.Sort.Descending(e => e.Version);
        var latest = await _collection.Find(filter).Sort(sort).Limit(1).FirstOrDefaultAsync();
        return (latest?.Version ?? 0) + 1;
    }

    private async Task AppendEventAsync(
        string clientId, string projectId,
        string eventType, BsonDocument payload, int version)
    {
        var doc = new EventStoreDocument
        {
            AggregateId = projectId,
            AggregateType = "ArtifactTree",
            EventType = eventType,
            EventData = payload,
            Version = version,
            OccurredOn = DateTime.UtcNow,
            ClientId = clientId,
        };
        await _collection.InsertOneAsync(doc);
    }
}

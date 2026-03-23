using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace TreeVsEventSim.Models;

/// <summary>Domain event types stored in the EventStore collection.</summary>
public enum EventType
{
    ArtifactAdded,
    ArtifactDeleted,
    ArtifactUpdated,
    ArtifactMoved,
    ArtifactEnabled,
    ArtifactDisabled,
    BranchCleared,
    TreeSnapshot,
}

/// <summary>
/// MongoDB document stored in the EventStore collection.
/// Each mutation to the artifact tree is persisted as an immutable event.
/// </summary>
public sealed class EventStoreDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    /// <summary>Project ID — the aggregate root.</summary>
    [BsonElement("aggregateId")]
    public string AggregateId { get; set; } = string.Empty;

    [BsonElement("aggregateType")]
    public string AggregateType { get; set; } = "ArtifactTree";

    [BsonElement("eventType")]
    public string EventType { get; set; } = string.Empty;

    [BsonElement("eventId")]
    public string EventId { get; set; } = Guid.NewGuid().ToString();

    [BsonElement("occurredOn")]
    public DateTime OccurredOn { get; set; } = DateTime.UtcNow;

    /// <summary>Event-specific payload serialized to BsonDocument.</summary>
    [BsonElement("eventData")]
    public BsonDocument EventData { get; set; } = new();

    /// <summary>Monotonically increasing sequence number within the aggregate.</summary>
    [BsonElement("version")]
    public int Version { get; set; }

    [BsonElement("metadata")]
    public EventMetadata? Metadata { get; set; }

    [BsonElement("clientId")]
    public string ClientId { get; set; } = string.Empty;
}

public sealed class EventMetadata
{
    [BsonElement("transactionId")]
    public string? TransactionId { get; set; }

    [BsonElement("correlationId")]
    public string? CorrelationId { get; set; }

    [BsonElement("source")]
    public string? Source { get; set; }
}

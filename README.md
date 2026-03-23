# TreeVsEventSim

A benchmarking console application that measures performance of three different storage strategies for an artifact tree data structure, inspired by Anvian's AI-powered SDLC platform.

## What it benchmarks

The **Artifact Tree** is a hierarchical breakdown of a software project — from business capabilities down to user stories, test cases, and generated code. This app compares storing that tree using:

| Strategy | Description |
|---|---|
| **Event Sourcing (MongoDB)** | Each mutation is an immutable event; full replay rebuilds the tree |
| **Materialized Document (MongoDB)** | Complete tree stored as one document; in-place field updates |
| **Neo4j Graph Database** | Each artifact is a graph node with `[:PARENT]` relationships |

## Benchmark operations

Each strategy is measured on:

- `WriteTree` — bulk write all nodes
- `ReadFullTree` — reconstruct the complete tree
- `GetSingleNode` — O(1) vs O(replay) lookup
- `GetChildren` — direct children of a node
- `GetParent` — direct parent of a node
- `AddArtifact` — append a new leaf
- `DeleteArtifact` — delete with cascade
- `MoveArtifact` — re-parent a node
- `SetArtifactEnabled` — enable/disable with descendants
- `ClearBranch` — remove all children of a node

**Metrics reported:** p50 / p95 / p99 latency, throughput (ops/sec), storage size, peak memory delta.

## Tree sizes

| Size | Nodes |
|---|---|
| Small | ~54 |
| Medium | ~666 |
| Large | ~2,544 |

## Getting started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- MongoDB running locally (or Docker)
- *(Optional)* Neo4j 5 for graph benchmarks

### Start backends

```bash
# MongoDB
docker run -d -p 27017:27017 mongo:7

# Neo4j (optional)
docker run -d -p 7474:7474 -p 7687:7687 -e NEO4J_AUTH=neo4j/yourpassword neo4j:5
```

### Configure

Edit `TreeVsEventSim/appsettings.json` or use environment variables:

```bash
# Override via env vars (prefix BENCH_)
export BENCH_MongoDB__ConnectionString=mongodb://localhost:27017
export BENCH_Neo4j__Password=yourpassword
```

> **Security note:** The default Neo4j password in `appsettings.json` is for local development only. Always use environment variables or a secrets manager for credentials in production.

### Build and run

```bash
cd TreeVsEventSim
dotnet run
```

### Sample output

```
Tree Size: Medium  (666 nodes)

                     Latency (milliseconds)
┌─────────────────────┬───────────────┬─────────────────┬────────┐
│ Operation           │ EventSourcing │ MaterializedDoc │  Neo4j │
│                     │   p50 / p95   │   p50 / p95     │  p50   │
├─────────────────────┼───────────────┼─────────────────┼────────┤
│ ReadFullTree        │  26.4 / 29.4  │  8.57 / 13.0    │   N/A  │
│ GetSingleNode       │  22.3 / 27.6  │  0.63 / 0.67    │   N/A  │
│ MoveArtifact        │   2.1 /  2.4  │  10.2 / 13.5    │   N/A  │
└─────────────────────┴───────────────┴─────────────────┴────────┘
```

## Project structure

```
TreeVsEventSim/
├── Program.cs                         Entry point, benchmark loop
├── appsettings.json                   Connection strings & config
├── Models/
│   ├── ArtifactType.cs                33-value enum
│   ├── ArtifactNode.cs                Single tree node
│   ├── ArtifactTreeProjection.cs      Reconstructed tree + stats
│   └── EventStoreDocument.cs          MongoDB event document
├── Simulation/
│   └── TreeSimulator.cs               Generates reproducible test trees
├── Strategies/
│   ├── IStorageStrategy.cs            Uniform interface for all backends
│   ├── EventSourcing/
│   │   └── EventSourcingStrategy.cs   MongoDB append-only event log
│   ├── MaterializedDocument/
│   │   └── MaterializedDocumentStrategy.cs  Single MongoDB document
│   └── Neo4j/
│       └── Neo4jStrategy.cs           Neo4j graph with Cypher queries
├── Benchmarks/
│   └── BenchmarkResult.cs             Latency samples + percentile calc
└── Reporting/
    └── ResultsReporter.cs             Spectre.Console formatted tables
```

## Key findings from benchmarks

| Characteristic | Event Sourcing | Materialized Doc | Neo4j |
|---|---|---|---|
| Read scalability | O(events) — degrades with history | O(1) — constant | O(nodes) for full tree |
| Single-node lookup | Full replay (slow at scale) | Field projection (fast) | Single node query |
| Write simplicity | Append-only | Read-modify-write | Create node + edge |
| Audit trail | ✓ Complete history | ✗ | ✗ |
| Cascade operations | Pre-computed lists needed | Pre-computed lists needed | Native graph traversal |
| Infrastructure | MongoDB | MongoDB | MongoDB + Neo4j |

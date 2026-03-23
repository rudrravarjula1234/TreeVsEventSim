namespace TreeVsEventSim.Models;

/// <summary>
/// Artifact type hierarchy — 33 values (0–32) covering product artifacts,
/// architecture, documents, and code-generation artifacts.
/// </summary>
public enum ArtifactType
{
    // ── Root / Project level (depth 0) ──────────────────────────────────────
    ProjectUnderstanding = 0,   // Root — no parent
    Capability = 1,             // Root-level; predecessor: ProjectUnderstanding
    BusinessProcessFlow = 2,    // Root-level; predecessors: ProjectUnderstanding, Capability
    Architecture = 3,           // Root-level; predecessor: ProjectUnderstanding

    // ── Epic level (depth 0 in tree) ────────────────────────────────────────
    Epic = 4,                   // Root-level; predecessors: ProjectUnderstanding, Capability, BPF

    // ── Feature level (depth 1) ─────────────────────────────────────────────
    Feature = 5,                // Parent: Epic
    EpicArchitecture = 6,       // Tied to Epic; predecessors include Architecture

    // ── User Story level (depth 2) ──────────────────────────────────────────
    UserStory = 7,              // Parent: Epic or Feature

    // ── Leaf / Code-generation level (depth 3) ──────────────────────────────
    TestCase = 8,               // Parent: Epic, Feature, or UserStory
    FrontEndCode = 9,           // Parent: UserStory
    BackEndCode = 10,           // Parent: UserStory
    UnitTestCases = 11,         // Parent: UserStory

    // ── Document artifacts ───────────────────────────────────────────────────
    TechnicalSpecification = 12,
    FunctionalSpecification = 13,
    DesignDocument = 14,
    ApiContract = 15,
    DataModel = 16,
    SecurityReview = 17,
    PerformanceAnalysis = 18,
    DeploymentPlan = 19,

    // ── Architecture artifacts ───────────────────────────────────────────────
    ArchitectureDecisionRecord = 20,
    ComponentDiagram = 21,
    SequenceDiagram = 22,
    DataFlowDiagram = 23,
    InfrastructurePlan = 24,

    // ── Quality artifacts ────────────────────────────────────────────────────
    AcceptanceCriteria = 25,
    IntegrationTestCase = 26,
    PerformanceTestCase = 27,
    SecurityTestCase = 28,

    // ── CI/CD and operational artifacts ─────────────────────────────────────
    PipelineConfiguration = 29,
    InfrastructureAsCode = 30,
    MonitoringAlert = 31,
    ReleaseNote = 32,
}

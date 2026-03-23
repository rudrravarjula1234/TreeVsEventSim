namespace TreeVsEventSim.Models;

/// <summary>
/// Artifact types used across the benchmark tree, including the core
/// hierarchical nodes and additional document/quality artifacts.
/// </summary>
public enum ArtifactType
{
    // ── Primary benchmark hierarchy ─────────────────────────────────────────
    ProjectUnderstanding = 0,
    Capability = 1,
    BusinessProcess = 2,
    BusinessProcessFlow = 3,
    Epic = 4,
    Feature = 5,
    Architecture = 6,
    UserStory = 7,
    TestCase = 8,
    GitCommit = 9,
    AdoPush = 10,
    GeneratedArtifact = 11,

    // ── Additional engineering artifacts ────────────────────────────────────
    FrontEndCode = 12,
    BackEndCode = 13,
    UnitTestCases = 14,

    // ── Document artifacts ───────────────────────────────────────────────────
    TechnicalSpecification = 15,
    FunctionalSpecification = 16,
    DesignDocument = 17,
    ApiContract = 18,
    DataModel = 19,
    SecurityReview = 20,
    PerformanceAnalysis = 21,
    DeploymentPlan = 22,

    // ── Architecture artifacts ───────────────────────────────────────────────
    EpicArchitecture = 23,
    ArchitectureDecisionRecord = 24,
    ComponentDiagram = 25,
    SequenceDiagram = 26,
    DataFlowDiagram = 27,
    InfrastructurePlan = 28,

    // ── Quality artifacts ────────────────────────────────────────────────────
    AcceptanceCriteria = 29,
    IntegrationTestCase = 30,
    PerformanceTestCase = 31,
    SecurityTestCase = 32,

    // ── CI/CD and operational artifacts ─────────────────────────────────────
    PipelineConfiguration = 33,
    InfrastructureAsCode = 34,
    MonitoringAlert = 35,
    ReleaseNote = 36,
}

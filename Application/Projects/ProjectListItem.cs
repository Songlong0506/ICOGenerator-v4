using ICOGenerator.Domain;

namespace ICOGenerator.Application.Projects;

public record ProjectListItem(
    Project Project,
    bool HasMockup,
    bool HasSource,
    string? LatestWorkflowStatus,
    string? LatestWorkflowStage,
    bool HasRunningWorkflow);

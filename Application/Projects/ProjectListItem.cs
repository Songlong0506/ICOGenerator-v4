using ICOGenerator.Domain;

namespace ICOGenerator.Application.Projects;

public record ProjectListItem(
    Project Project,
    bool HasMockup,
    string? LatestWorkflowStatus,
    string? LatestWorkflowStage,
    bool HasRunningWorkflow);

using ICOGenerator.Domain;

namespace ICOGenerator.Application.Projects;

public record ProjectListItem(
    Project Project,
    bool HasMockup,
    string? LatestWorkflowStatus,
    string? LatestWorkflowStage,
    bool HasRunningWorkflow,
    // Tên đơn vị yêu cầu tra từ OrgUnits theo Project.OrgUnitCode; null khi project chưa gắn
    // hoặc mã không còn trong dữ liệu HR (khi đó view fallback hiển thị mã thô).
    string? OrgUnitName = null);

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
    string? OrgUnitName = null,
    // Tên hiển thị của người tạo (AppUser.DisplayName tra từ Project.CreatedByUsername); null khi
    // project chưa có chủ hoặc không tra được user (view fallback về username thô rồi tới "—").
    string? CreatedByDisplayName = null);

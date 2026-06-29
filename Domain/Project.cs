using ICOGenerator.Domain.Enums;
namespace ICOGenerator.Domain;
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ProjectStatus Status { get; set; } = ProjectStatus.Planning;
    public string? BackendGitUrl { get; set; }
    public string? FrontendGitUrl { get; set; }
    // Cấu hình delivery do TeamDev điền ở Agent Dashboard (sau bước POC), không phải end-user lúc tạo project.
    // null = TeamDev CHƯA chọn Generation Mode; cổng Approve chặn đẩy sang Architecture cho tới khi có giá trị
    // rõ ràng (true = Bosch template, false = để TechLead tự định kiến trúc) — tránh âm thầm mặc định Bosch.
    public bool? IsUseBoschTemplate { get; set; }
    // Username (claim Name) của người tạo project. Dùng để lọc danh sách: User thường chỉ thấy project
    // do mình tạo; Admin/TeamDev (quyền ProjectsViewAll) thấy tất cả. Nullable để tương thích các project
    // cũ tạo trước khi có cột này — chúng coi như "không có chủ" và chỉ hiện cho người xem-tất-cả.
    public string? CreatedByUsername { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<ProjectDocument> Documents { get; set; } = new List<ProjectDocument>();
    public ICollection<ProjectSourceFile> SourceFiles { get; set; } = new List<ProjectSourceFile>();
    public ICollection<AgentConversation> Conversations { get; set; } = new List<AgentConversation>();
    public ICollection<AgentModelCallLog> ModelCallLogs { get; set; } = new List<AgentModelCallLog>();
    public ICollection<WorkflowRun> WorkflowRuns { get; set; } = new List<WorkflowRun>();
    public ICollection<AgentTask> AgentTasks { get; set; } = new List<AgentTask>();
}

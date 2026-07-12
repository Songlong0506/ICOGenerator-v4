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
    // Mặc định = true (dùng Bosch template); TeamDev có thể đổi sang false (để TechLead tự định kiến trúc) ở
    // Agent Dashboard. Luôn có giá trị rõ ràng — không còn trạng thái "chưa chọn".
    public bool IsUseBoschTemplate { get; set; } = true;
    // Username (claim Name) của người tạo project. Dùng để lọc danh sách: User thường chỉ thấy project
    // do mình tạo; Admin/TeamDev (quyền ProjectsViewAll) thấy tất cả. Nullable để tương thích các project
    // cũ tạo trước khi có cột này — chúng coi như "không có chủ" và chỉ hiện cho người xem-tất-cả.
    public string? CreatedByUsername { get; set; }
    // Mã đơn vị yêu cầu (OrgUnits.OrgUnitCode) — người dùng chọn lúc tạo project (tùy chọn). Chỉ lưu MÃ,
    // tên phòng/manager tra lại từ OrgUnits/Associates lúc cần (tên có thể đổi khi đồng bộ HR). Dùng cho:
    // ghi chú "đơn vị yêu cầu" trong ngữ cảnh BA + tài liệu (OrganizationContextService) và thống kê
    // Usage theo phòng ban. null = chưa gắn — mọi luồng chạy như trước.
    public string? OrgUnitCode { get; set; }
    // Bộ nhớ dài hạn của hội thoại BA: tóm tắt (text) các lượt CŨ đã rơi ra ngoài cửa sổ gần nhất, được
    // gộp DẦN để hội thoại dài vẫn giữ ngữ cảnh mà prompt không phình token. null = chưa có gì để tóm tắt.
    // SummarizedTurnCount = số lượt cũ nhất (xếp theo CreatedAt) đã được gộp vào ConversationSummary, làm
    // con trỏ để biết lượt nào còn phải gửi nguyên văn. Xem ConversationMemoryService.
    public string? ConversationSummary { get; set; }
    public int SummarizedTurnCount { get; set; }
    // Con trỏ riêng cho bộ nhớ CẤP USER (AppUser.UserMemory): số lượt cũ nhất (xếp theo CreatedAt) của
    // project này đã được chắt lọc vào hồ sơ user của người tạo. Tách khỏi SummarizedTurnCount vì hai bộ
    // nhớ tiến theo nhịp/độ trễ khác nhau. Xem UserMemoryService.
    public int UserMemoryHarvestedTurnCount { get; set; }
    // Đánh dấu dự án này ĐÃ được rà soát một lần để rút "khoảng trống checklist" (thông tin người dùng
    // phải tự nêu ra mà BA chưa từng hỏi) vào Agent.LearnedChecklistNotes — dùng chung cho MỌI dự án sau
    // này. Chỉ rà soát MỘT LẦN, ngay sau khi tài liệu được sinh thành công (lúc đó mới có bức tranh Q&A
    // đầy đủ). Xem ChecklistGapMemoryService.
    public bool ChecklistGapHarvested { get; set; }
    // "Bản đồ bao phủ yêu cầu" của dự án: bảng trạng thái (text, 12 nhóm cố định) cho biết nhóm thông tin
    // nào đã khai thác [RÕ]/[MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG], cập nhật sau mỗi lượt chat để BA chọn
    // câu hỏi kế tiếp và cổng readiness khỏi đoán lại từ đầu. null = chưa có lượt chat nào được ghi nhận.
    // CoverageHarvestedTurnCount = số lượt cũ nhất (xếp theo CreatedAt) đã gộp vào bản đồ — con trỏ để
    // biết lượt nào còn phải gộp tiếp (fail-open: lời gọi lỗi thì con trỏ đứng yên, lần sau gộp bù).
    // Xem RequirementCoverageService.
    public string? RequirementCoverageMap { get; set; }
    public int CoverageHarvestedTurnCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<ProjectDocument> Documents { get; set; } = new List<ProjectDocument>();
    // Thành viên được mời vào project (reviewer/stakeholder) — thấy project trong danh sách của mình và
    // được góp ý trên Product Brief/POC. Xem ProjectMember.
    public ICollection<ProjectMember> Members { get; set; } = new List<ProjectMember>();
    public ICollection<ProjectSourceFile> SourceFiles { get; set; } = new List<ProjectSourceFile>();
    public ICollection<AgentConversation> Conversations { get; set; } = new List<AgentConversation>();
    public ICollection<AgentModelCallLog> ModelCallLogs { get; set; } = new List<AgentModelCallLog>();
    public ICollection<WorkflowRun> WorkflowRuns { get; set; } = new List<WorkflowRun>();
    public ICollection<AgentTask> AgentTasks { get; set; } = new List<AgentTask>();
}

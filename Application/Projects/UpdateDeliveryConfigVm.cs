namespace ICOGenerator.Application.Projects;

// Cấu hình kỹ thuật của project do TeamDev điền ở Agent Dashboard SAU bước POC (xem AgentDashboardController).
// Mọi field đều tùy chọn để TeamDev điền dần theo đúng stage cần tới: Generation Mode trước bước Architecture,
// Backend/Frontend Git (+ nhánh đích, tài khoản Git) trước bước Pull Request — hoặc điền tất cả một lần.
// Cổng Approve mới là nơi bắt buộc.
public class UpdateDeliveryConfigVm
{
    public Guid ProjectId { get; set; }

    // true = Bosch template; false = để TechLead tự định kiến trúc. Mặc định true.
    public bool IsUseBoschTemplate { get; set; } = true;

    public string? BackendGitUrl { get; set; }
    public string? FrontendGitUrl { get; set; }

    // Nhánh đích của Pull Request; trống = "main" (xem ProjectGitSettings).
    public string? GitBaseBranch { get; set; }

    // Tài khoản Git dùng để clone/push repo đích. Trống = dùng credential sẵn có của máy chủ.
    public string? GitUsername { get; set; }

    // Ô token là ô CHỈ-GHI: trang không bao giờ render token đã lưu, nên ô trống nghĩa là "giữ nguyên",
    // không phải "xoá". Muốn xoá thì tích ClearGitAccessToken.
    public string? GitAccessToken { get; set; }
    public bool ClearGitAccessToken { get; set; }
}

namespace ICOGenerator.Application.Projects;

// Cấu hình kỹ thuật của project do TeamDev điền ở Agent Dashboard SAU bước POC (xem AgentDashboardController).
// Mọi field đều tùy chọn để TeamDev điền dần theo đúng stage cần tới: Generation Mode trước bước Architecture,
// Backend/Frontend Git trước bước Pull Request — hoặc điền cả ba một lần. Cổng Approve mới là nơi bắt buộc.
public class UpdateDeliveryConfigVm
{
    public Guid ProjectId { get; set; }

    // true = Bosch template; false = để TechLead tự định kiến trúc. Mặc định true.
    public bool IsUseBoschTemplate { get; set; } = true;

    public string? BackendGitUrl { get; set; }
    public string? FrontendGitUrl { get; set; }
}

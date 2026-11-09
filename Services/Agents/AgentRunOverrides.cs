using ICOGenerator.Domain;

namespace ICOGenerator.Services.Agents;

/// <summary>
/// Ghi đè cấu hình cho ĐÚNG MỘT lượt <see cref="AgentRunService.RunAsync"/> mà không sửa cấu hình agent.
///
/// <para>
/// Tồn tại vì model gán cho agent (<see cref="Agent.AiModelId"/>) là cấu hình TOÀN CỤC, dùng chung mọi
/// dự án: muốn thử một model khác mà đi đổi agent thì mọi task của agent đó ở các dự án khác chạy trong
/// lúc ấy cũng đổi model theo. Người dùng đầu tiên là biến thể POC thử model (xem
/// <see cref="Workflows.PocVariantRunner"/>).
/// </para>
/// </summary>
/// <param name="Model">Model dùng thay cho model của agent. Null ⇒ model của agent.</param>
/// <param name="WorkspacePath">Đường dẫn TUYỆT ĐỐI làm gốc workspace của các tool. Null ⇒ workspace của dự án.</param>
/// <param name="Purpose">Nhãn <c>Purpose</c> ghi vào AgentModelCallLogs. Null ⇒ "AgentRun".</param>
/// <param name="SaveConversation">
/// Có ghi bàn giao cuối vào <c>AgentConversations</c> của dự án không. Một lượt thí nghiệm không phải lời
/// của agent nói với dự án — ghi vào là trộn nó vào lịch sử thật.
/// </param>
/// <param name="AllowedTools">
/// Chỉ quảng bá cho model các tool có tên trong danh sách này (giao với tập tool đã gán cho agent). Null ⇒ mọi
/// tool của agent. Dùng cho bước dựng POC mới: còn <c>ReadFile</c>/<c>SearchInFiles</c>/<c>RunCommand</c> thì
/// model đi săn lỗi bằng các lượt lẻ thay vì sửa gộp bằng <c>FixPocAndAudit</c> — xem
/// <see cref="Tools.WorkspaceTools.PocBuildToolNames"/>.
/// </param>
public sealed record AgentRunOverrides(
    AiModel? Model = null,
    string? WorkspacePath = null,
    string? Purpose = null,
    bool SaveConversation = true,
    IReadOnlyCollection<string>? AllowedTools = null);

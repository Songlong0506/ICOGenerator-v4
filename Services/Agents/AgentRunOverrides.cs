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
/// <param name="AllowedTools">
/// Chỉ quảng bá cho model các tool có tên trong danh sách này (giao với tập tool đã gán cho agent). Null ⇒ mọi
/// tool của agent. Dùng cho bước dựng POC mới: còn <c>ReadFile</c>/<c>SearchInFiles</c>/<c>RunCommand</c> thì
/// model đi săn lỗi bằng các lượt lẻ thay vì sửa gộp bằng <c>FixPocAndAudit</c> — xem
/// <see cref="Tools.WorkspaceTools.PocBuildToolNames"/>.
/// </param>
/// <param name="AutoContinue">
/// false ⇒ <c>maxSteps</c> là TRẦN CỨNG: cạn ngân sách thì sang thẳng lượt salvage, không có pha "làm tiếp" tới
/// gấp ba số bước. Dành cho caller tự có vòng lặp ngoài chấm lại sau mỗi lượt (vòng tự sửa của cổng UAT —
/// <see cref="Workflows.PocUatGate"/>): ở đó vòng kế được mở với báo cáo audit MỚI, nên kéo dài một vòng đang
/// lạc hướng chỉ tốn token mà không thêm thông tin.
/// </param>
public sealed record AgentRunOverrides(
    AiModel? Model = null,
    string? WorkspacePath = null,
    string? Purpose = null,
    IReadOnlyCollection<string>? AllowedTools = null,
    bool AutoContinue = true);

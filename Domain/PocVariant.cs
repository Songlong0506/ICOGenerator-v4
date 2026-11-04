using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một lần THỬ dựng lại bản demo POC bằng một model khác cho agent Developer — để so model nào hợp với
/// bước POC trên chính dự án thật, cùng đầu vào với bản đang review.
///
/// <para>
/// Đây là THÍ NGHIỆM, không phải một vòng của pipeline: biến thể chạy trong thư mục riêng ngoài workspace
/// của dự án (<see cref="Services.Artifacts.WorkspacePathResolver.GetPocVariantPath"/>), không đụng
/// <c>poc-demo.html</c>, bản chụp các vòng, kết quả tự kiểm, ghi chú POC hay trạng thái cổng duyệt; và
/// model thử chỉ áp cho đúng lần chạy này — cấu hình model của agent (dùng chung mọi dự án) giữ nguyên.
/// </para>
/// </summary>
public class PocVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    /// <summary>Model dùng cho lần thử. Null khi model đã bị xoá khỏi màn AI Models — tên vẫn ở <see cref="ModelLabel"/>.</summary>
    public Guid? AiModelId { get; set; }
    public AiModel? AiModel { get; set; }

    /// <summary>
    /// <c>ModelId</c> của model lúc bấm thử. Chụp lại vì bảng so sánh phải còn đọc được sau khi model bị
    /// sửa/xoá — một dòng kết quả mất tên model là một dòng vô nghĩa.
    /// </summary>
    public string ModelLabel { get; set; } = string.Empty;

    /// <summary>
    /// Task <c>PocPreview</c> dựng MỚI bản demo chính mà biến thể lấy đầu vào (AI Design Spec trong
    /// <c>Input</c>). Không khai FK: task bị dọn thì dòng thí nghiệm vẫn giữ được kết quả đã đo.
    /// </summary>
    public Guid SourceTaskId { get; set; }

    public PocVariantStatus Status { get; set; } = PocVariantStatus.Queued;

    public string? RequestedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public string? Error { get; set; }

    /// <summary>Bàn giao bằng chữ của agent (cắt ngắn) — để đọc model này tự nhận đã làm gì.</summary>
    public string? Output { get; set; }

    // Chi phí đo từ AgentModelCallLogs của chính lần chạy (Purpose = PocVariant), chốt lúc xong. Lưu lại
    // thay vì tính mỗi lần mở bảng: các dòng log là nguồn của trang Usage, còn đây là con số của MỘT thí
    // nghiệm — nó không được đổi khi đơn giá model được sửa về sau.
    public int ModelCalls { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public decimal CostUsd { get; set; }
}

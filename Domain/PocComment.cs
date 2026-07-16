using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một ghi chú được GHIM TRỰC TIẾP lên một phần tử trong POC demo (trang Projects/PocReview): người
/// xem bật chế độ ghim, click vào phần tử chưa đúng và gõ nhận xét. Khác với nhận xét gõ tay ở cổng
/// duyệt (vốn chung chung), ghi chú ghim mang đủ ngữ cảnh máy-đọc-được — màn hình nào, phần tử nào
/// (nhãn + CSS selector), vị trí — nên Developer agent sửa POC chính xác hơn hẳn. Các ghi chú Open
/// được gom vào <see cref="AgentTask.RevisionFeedback"/> khi người duyệt "Yêu cầu chỉnh sửa" ở cổng POC.
/// </summary>
public class PocComment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    /// <summary>Nhãn data-view của .page-view đang mở khi ghim (rỗng với POC một màn hình).</summary>
    public string PageView { get; set; } = string.Empty;

    /// <summary>Mô tả phần tử cho NGƯỜI đọc (vd: Nút "Save" · BUTTON) — hiển thị ở danh sách ghi chú.</summary>
    public string ElementLabel { get; set; } = string.Empty;

    /// <summary>CSS selector (tương đối trong POC) để neo lại pin và để agent tìm đúng phần tử trong HTML.</summary>
    public string ElementPath { get; set; } = string.Empty;

    /// <summary>Vị trí click theo % viewport POC — neo dự phòng khi selector không còn khớp sau chỉnh sửa.</summary>
    public double XPercent { get; set; }

    public double YPercent { get; set; }

    public string Comment { get; set; } = string.Empty;

    public PocCommentStatus Status { get; set; } = PocCommentStatus.Open;

    public string? CreatedByUsername { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

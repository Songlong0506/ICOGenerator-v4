using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một ghi chú của người review, và là MỘT DÒNG LỊCH SỬ không bị xoá. Hai nguồn đổ vào cùng bảng này
/// (xem <see cref="PocCommentTarget"/>):
/// <list type="bullet">
///   <item><b>POC</b> — ghim trực tiếp lên một phần tử trong bản demo (trang Projects/PocReview): người
///   xem bật chế độ ghim, click vào phần tử chưa đúng và gõ nhận xét. Khác với nhận xét gõ tay ở cổng
///   duyệt (vốn chung chung), ghi chú ghim mang đủ ngữ cảnh máy-đọc-được — màn hình nào, phần tử nào
///   (nhãn + CSS selector), vị trí — nên Developer agent sửa POC chính xác hơn hẳn.</item>
///   <item><b>Brief</b> — ghim lên một đoạn bản xem trước Product Brief (<see cref="Quote"/> là đoạn được
///   bôi đen). Trước đây chúng chỉ được nối thành một lượt chat gửi BA rồi không lưu dòng nào, nên sau
///   khi Brief lên version mới thì không còn cách nào biết version cũ từng bị chê gì.</item>
/// </list>
/// Các ghi chú Open được gom vào <see cref="AgentTask.RevisionFeedback"/> khi người duyệt "Yêu cầu chỉnh
/// sửa" ở cổng POC. <b>Không bao giờ xoá cứng</b>: bỏ một ghi chú là <see cref="WithdrawnAtUtc"/> (thu
/// hồi mềm) để dòng lịch sử còn nguyên — xem WithdrawPocCommentUseCase.
/// </summary>
public class PocComment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    /// <summary>Ghi chú nói về bản demo hay về bản mô tả sản phẩm.</summary>
    public PocCommentTarget Target { get; set; } = PocCommentTarget.Poc;

    /// <summary>
    /// Phiên bản Product Brief mà ghi chú này nói VỀ ("draft", "V1", "V2"…) — đóng dấu lúc ghim và không
    /// đổi về sau. Quy tắc: ghi chú POC lấy version đã duyệt hiện hành (POC dựng từ chính nó); ghi chú
    /// Brief đóng dấu "draft" rồi được ApproveRequirementUseCase nâng lên V{n} CÙNG LÚC với file draft —
    /// đúng thứ nó đang nói về. Không có cột này thì sau vài vòng, danh sách trộn lẫn mọi thế hệ ghi chú
    /// mà không phân biệt được cái nào thuộc bản nào.
    /// </summary>
    public string BriefVersion { get; set; } = "draft";

    /// <summary>Nhãn data-view của .page-view đang mở khi ghim (rỗng với POC một màn hình).</summary>
    public string PageView { get; set; } = string.Empty;

    /// <summary>Mô tả phần tử cho NGƯỜI đọc (vd: Nút "Save" · BUTTON) — hiển thị ở danh sách ghi chú.</summary>
    public string ElementLabel { get; set; } = string.Empty;

    /// <summary>CSS selector (tương đối trong POC) để neo lại pin và để agent tìm đúng phần tử trong HTML.</summary>
    public string ElementPath { get; set; } = string.Empty;

    /// <summary>Vị trí click theo % viewport POC — neo dự phòng khi selector không còn khớp sau chỉnh sửa.</summary>
    public double XPercent { get; set; }

    public double YPercent { get; set; }

    /// <summary>
    /// Neo của ghi chú <see cref="PocCommentTarget.Brief"/>: đoạn văn người dùng bôi đen trong bản xem
    /// trước. Rỗng = ghi chú chung cho cả bản mô tả. Ghi chú POC không dùng trường này (đã có selector).
    /// </summary>
    public string? Quote { get; set; }

    public string Comment { get; set; } = string.Empty;

    public PocCommentStatus Status { get; set; } = PocCommentStatus.Open;

    public string? CreatedByUsername { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Thời điểm vòng chỉnh sửa POC mang ghi chú này chạy xong (null = chưa qua vòng sửa nào).</summary>
    public DateTime? AddressedAtUtc { get; set; }

    /// <summary>
    /// Bàn giao của Developer agent ở vòng sửa đó (cắt ngắn) — người review đọc được "agent nói mình đã
    /// làm gì" ngay cạnh ghi chú, thay vì chỉ thấy ghi chú im lặng chuyển trạng thái.
    /// </summary>
    public string? AddressedNote { get; set; }

    /// <summary>
    /// Đường xử lý đã gửi ghi chú này đi (<c>null</c> = chưa gửi). Tách khỏi <see cref="Status"/> để một
    /// ghi chú đi đường tài liệu vẫn có vòng đời riêng — xem <see cref="PocCommentRoute"/>.
    /// </summary>
    public PocCommentRoute? Route { get; set; }

    /// <summary>
    /// Vòng sửa POC đã mang ghi chú này đi (<see cref="AgentTask"/> có <c>RevisionFeedback</c>). Bảng lịch
    /// sử dùng nó để mở đúng bàn giao TOÀN VĂN của vòng đó — <see cref="AddressedNote"/> chỉ là bản cắt
    /// 1500 ký tự để hiện gọn cạnh ghi chú.
    /// </summary>
    public Guid? RevisionTaskId { get; set; }

    /// <summary>
    /// Thu hồi mềm: ghi chú gõ nhầm biến mất khỏi danh sách làm việc nhưng dòng lịch sử còn nguyên.
    /// Xoá cứng từng là hành vi của nút 🗑 — mất luôn cả việc ai xoá và xoá lúc nào.
    /// </summary>
    public DateTime? WithdrawnAtUtc { get; set; }

    public string? WithdrawnByUsername { get; set; }
}

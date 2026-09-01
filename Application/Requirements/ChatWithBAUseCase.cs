using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Application.Requirements;

public class ChatWithBAUseCase
{
    private readonly BAChatService _baChatService;

    public ChatWithBAUseCase(BAChatService baChatService)
    {
        _baChatService = baChatService;
    }

    /// <param name="onStatus">Callback trạng thái ngắn cho UI streaming (null khi gọi kiểu postback cổ điển).</param>
    /// <param name="onToken">Callback nhận text hiển thị được khi BA "đang gõ" (null = không stream).</param>
    public Task<BAChatTurnResult> ExecuteAsync(Guid projectId, string message,
        Action<string>? onStatus = null, Action<string>? onToken = null, CancellationToken cancellationToken = default) =>
        _baChatService.ChatAsync(projectId, message, onStatus, onToken, cancellationToken);

    /// <summary>
    /// Thử lại lượt BA vừa lỗi LLM: xóa lượt lỗi cuối rồi chạy lại lượt chat trên transcript hiện có
    /// (không ghi thêm lượt user). Trả <see cref="ChatWithBAResult.NothingToRetry"/> khi lượt cuối
    /// không phải thông báo lỗi.
    /// </summary>
    public Task<BAChatTurnResult> RetryAsync(Guid projectId,
        Action<string>? onStatus = null, Action<string>? onToken = null, CancellationToken cancellationToken = default) =>
        _baChatService.RetryLastTurnAsync(projectId, onStatus, onToken, cancellationToken);

    /// <summary>
    /// Sửa lượt user vừa gửi rồi trả lời lại: ghi đè nội dung lượt user mới nhất, xóa câu trả lời cũ và
    /// chạy lại lượt. Các con trỏ gộp (bản đồ bao phủ, nhật ký chốt, bộ nhớ) được kéo lùi để mọi bản đúc
    /// kết dựng lại từ nội dung ĐÃ SỬA — xem <see cref="BAChatService.EditLastUserTurnAsync"/>.
    /// </summary>
    public Task<BAChatTurnResult> EditLastAsync(Guid projectId, string message,
        Action<string>? onStatus = null, Action<string>? onToken = null, CancellationToken cancellationToken = default) =>
        _baChatService.EditLastUserTurnAsync(projectId, message, onStatus, onToken, cancellationToken);

    /// <summary>
    /// Cho biết câu trả lời của BA cho lượt hiện tại còn "đang chờ" (lượt hội thoại mới nhất là của người
    /// dùng, BA vẫn đang sinh lượt assistant với CancellationToken.None) và liệu lượt chờ đó đã CHẾT hẳn
    /// hay chưa. Dùng để khôi phục khung "BA đang soạn…" sau khi tải lại trang giữa chừng — và để không
    /// treo ở đó vĩnh viễn khi câu trả lời không bao giờ tới. Xem
    /// <see cref="BAChatService.GetReplyStateAsync"/>.
    /// </summary>
    public Task<ChatReplyState> GetReplyStateAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _baChatService.GetReplyStateAsync(projectId, cancellationToken);

    /// <summary>
    /// Gộp lượt chat mới vào "triển vọng phỏng vấn" (điểm cần làm rõ + màn hình dự kiến + ví dụ tính thử) —
    /// gọi SAU khi user đã nhận câu trả lời (sau frame done ở đường streaming) để lời gọi LLM này không
    /// cộng vào độ chờ.
    /// </summary>
    public Task<InterviewOutlook> UpdateInterviewOutlookAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _baChatService.UpdateInterviewOutlookAsync(projectId, cancellationToken);

    /// <summary>
    /// Sau upload tài liệu nguồn: lưu lượt user (ghi chú + file đính kèm để bubble hiển thị ảnh trong
    /// hội thoại) rồi BA tóm tắt những gì đọc được + xin xác nhận (thêm một lượt assistant; lỗi LLM được
    /// lưu thành lượt ⚠️ có nút "Thử lại"). <paramref name="note"/> là ghi chú tùy chọn người dùng gõ
    /// cạnh ảnh trong khung chat trước khi gửi; <paramref name="attachments"/> là các file vừa upload.
    /// Fail-open — trả false khi bước tóm tắt không thành công.
    /// </summary>
    public Task<bool> AcknowledgeSourcesAsync(Guid projectId, string? note = null, IReadOnlyList<ChatAttachment>? attachments = null, CancellationToken cancellationToken = default) =>
        _baChatService.AcknowledgeSourcesAsync(projectId, note, attachments, cancellationToken);
}

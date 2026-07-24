using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Application.Requirements;

public class ChatWithBAUseCase
{
    private readonly BAChatService _baChatService;
    private readonly ProjectDomainClassifier _domainClassifier;

    public ChatWithBAUseCase(BAChatService baChatService, ProjectDomainClassifier domainClassifier)
    {
        _baChatService = baChatService;
        _domainClassifier = domainClassifier;
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
    /// Gộp lượt chat mới vào "Điều đã chốt" — gọi SAU khi user đã nhận câu trả lời (sau frame done ở
    /// đường streaming / trước redirect ở đường postback) để lời gọi LLM này không cộng vào độ chờ.
    /// </summary>
    public Task<IReadOnlyList<string>> UpdateDecisionsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _baChatService.UpdateDecisionsAsync(projectId, cancellationToken);

    /// <summary>
    /// Gộp lượt chat mới vào "triển vọng phỏng vấn" (điểm cần làm rõ + màn hình dự kiến + ví dụ tính thử) —
    /// gọi SAU khi user đã nhận câu trả lời (hậu kỳ) như <see cref="UpdateDecisionsAsync"/>.
    /// </summary>
    public Task<InterviewOutlook> UpdateInterviewOutlookAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _baChatService.UpdateInterviewOutlookAsync(projectId, cancellationToken);

    /// <summary>
    /// Phân loại miền nghiệp vụ của dự án nếu chưa có (idempotent, fail-open) — cũng chạy ở hậu kỳ lượt
    /// chat để không cộng vào độ chờ. Miền quyết định bucket "checklist học được" nào của BA được dùng.
    /// </summary>
    public Task EnsureProjectDomainAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _domainClassifier.TryClassifyAsync(projectId, cancellationToken);

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

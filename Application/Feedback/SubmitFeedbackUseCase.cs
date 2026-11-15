using ICOGenerator.Localization;
using ICOGenerator.Data;
using ICOGenerator.Services.Feedback;
using FeedbackEntity = ICOGenerator.Domain.Feedback;

namespace ICOGenerator.Application.Feedback;

/// <summary>
/// Ghi một phản hồi mới (kèm file đính kèm tuỳ chọn). Validate nội dung + file theo lô: nếu sai thì ném
/// <see cref="FeedbackValidationException"/> và KHÔNG lưu gì (controller bắt để báo người dùng).
/// </summary>
public class SubmitFeedbackUseCase
{
    private const int MaxTitleLength = 200;
    private const int MaxMessageLength = 20_000;

    private readonly AppDbContext _db;
    private readonly FeedbackAttachmentStore _store;

    public SubmitFeedbackUseCase(AppDbContext db, FeedbackAttachmentStore store)
    {
        _db = db;
        _store = store;
    }

    public async Task ExecuteAsync(
        SubmitFeedbackVm input, IReadOnlyList<IFormFile>? files, string? username, string? displayName,
        CancellationToken cancellationToken = default)
    {
        var title = (input.Title ?? string.Empty).Trim();
        var message = (input.Message ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(title))
            throw new FeedbackValidationException(Loc.T("SubmitFeedbackUseCase.ExecuteAsync"));
        if (title.Length > MaxTitleLength)
            throw new FeedbackValidationException(Loc.T("SubmitFeedbackUseCase.ExecuteAsync.2", MaxTitleLength));
        if (string.IsNullOrWhiteSpace(message))
            throw new FeedbackValidationException(Loc.T("SubmitFeedbackUseCase.ExecuteAsync.3"));
        if (message.Length > MaxMessageLength)
            throw new FeedbackValidationException(Loc.T("SubmitFeedbackUseCase.ExecuteAsync.4", MaxMessageLength));

        var feedback = new FeedbackEntity
        {
            Type = input.Type,
            Title = title,
            Message = message,
            CreatedByUsername = username,
            SubmittedByName = string.IsNullOrWhiteSpace(displayName) ? username : displayName,
        };

        // Ghi file TRƯỚC khi add DB: nếu file lỗi validate thì ném ra ngay, chưa đụng tới DB.
        feedback.Attachments = await _store.StoreAsync(feedback.Id, files, cancellationToken);

        _db.Feedbacks.Add(feedback);
        await _db.SaveChangesAsync(cancellationToken);
    }
}

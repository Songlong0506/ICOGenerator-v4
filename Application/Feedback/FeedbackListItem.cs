using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Application.Feedback;

/// <summary>Một dòng phản hồi để render danh sách.</summary>
public record FeedbackListItem(
    Guid Id,
    FeedbackType Type,
    FeedbackStatus Status,
    string Title,
    string Message,
    string SubmittedByName,
    DateTime CreatedAt,
    bool IsMine,
    IReadOnlyList<FeedbackAttachmentVm> Attachments);

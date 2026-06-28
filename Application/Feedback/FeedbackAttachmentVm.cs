using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Application.Feedback;

/// <summary>Một file đính kèm để hiển thị trong danh sách phản hồi.</summary>
public record FeedbackAttachmentVm(
    Guid Id,
    string FileName,
    FeedbackAttachmentKind Kind,
    string ContentType,
    long SizeBytes)
{
    public bool IsImage => Kind == FeedbackAttachmentKind.Image;
    public bool IsVideo => Kind == FeedbackAttachmentKind.Video;

    /// <summary>Kích thước thân thiện (KB/MB) để hiển thị.</summary>
    public string SizeLabel => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024d / 1024d:0.#} MB"
        : $"{Math.Max(1, SizeBytes / 1024):0} KB";
}

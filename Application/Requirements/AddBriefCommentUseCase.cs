using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum AddBriefCommentResult
{
    Added,
    ProjectNotFound,

    /// <summary>Nội dung góp ý trống.</summary>
    MissingContent
}

/// <summary>
/// Thêm một góp ý của reviewer trên Product Brief. Ai vào được workspace (quyền RequirementsComment)
/// đều góp ý được — mục tiêu là gom được càng nhiều phản hồi của stakeholder trước khi Approve càng tốt.
/// AnchorText (đoạn trích được bôi đen trên brief) là tùy chọn; quá dài thì cắt gọn thay vì từ chối.
/// </summary>
public class AddBriefCommentUseCase
{
    // Khớp HasMaxLength của cột trong AppDbContext — cắt ở app để không văng lỗi DB khi client gửi dài hơn.
    private const int MaxAnchorLength = 500;
    private const int MaxContentLength = 4000;

    private readonly AppDbContext _db;

    public AddBriefCommentUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AddBriefCommentResult> ExecuteAsync(
        Guid projectId, string? content, string? anchorText, string? actorUsername, CancellationToken cancellationToken = default)
    {
        content = content?.Trim();
        if (string.IsNullOrEmpty(content))
            return AddBriefCommentResult.MissingContent;

        var projectExists = await _db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken);
        if (!projectExists)
            return AddBriefCommentResult.ProjectNotFound;

        anchorText = anchorText?.Trim();
        if (string.IsNullOrEmpty(anchorText))
            anchorText = null;

        _db.BriefComments.Add(new BriefComment
        {
            ProjectId = projectId,
            AuthorUsername = actorUsername ?? "",
            AnchorText = Truncate(anchorText, MaxAnchorLength),
            Content = Truncate(content, MaxContentLength)!
        });

        await _db.SaveChangesAsync(cancellationToken);
        return AddBriefCommentResult.Added;
    }

    private static string? Truncate(string? value, int max) =>
        value == null || value.Length <= max ? value : value[..(max - 1)] + "…";
}

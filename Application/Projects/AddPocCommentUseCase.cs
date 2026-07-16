using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum AddPocCommentResult
{
    Ok,
    ProjectNotFound,

    /// <summary>Nội dung ghi chú trống — không có gì để agent sửa theo.</summary>
    MissingComment,

    /// <summary>Project đã có quá nhiều ghi chú (chống spam/lỗi vòng lặp client).</summary>
    TooManyComments
}

/// <summary>
/// Ghim một ghi chú lên POC (từ trang Projects/PocReview). Các trường neo (PageView/ElementLabel/
/// ElementPath/X/Y) do annotator trong iframe POC thu thập — dữ liệu client nên chỉ cắt gọn theo
/// MaxLength và kẹp %, không tin cậy gì hơn (chúng chỉ dùng để hiển thị pin và cho agent tìm phần tử).
/// </summary>
public class AddPocCommentUseCase
{
    // Trần ghi chú mỗi project: review thật hiếm khi vượt vài chục; trần rộng chỉ để chặn client lỗi.
    private const int MaxCommentsPerProject = 300;

    private readonly AppDbContext _db;

    public AddPocCommentUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(AddPocCommentResult Result, PocCommentItem? Item)> ExecuteAsync(
        Guid projectId,
        string? pageView,
        string? elementLabel,
        string? elementPath,
        double xPercent,
        double yPercent,
        string? comment,
        string? createdByUsername,
        CancellationToken cancellationToken = default)
    {
        comment = comment?.Trim();
        if (string.IsNullOrEmpty(comment))
            return (AddPocCommentResult.MissingComment, null);

        if (!await _db.Projects.AnyAsync(x => x.Id == projectId, cancellationToken))
            return (AddPocCommentResult.ProjectNotFound, null);

        if (await _db.PocComments.CountAsync(x => x.ProjectId == projectId, cancellationToken) >= MaxCommentsPerProject)
            return (AddPocCommentResult.TooManyComments, null);

        var entity = new PocComment
        {
            ProjectId = projectId,
            PageView = Clip(pageView, 200),
            ElementLabel = Clip(elementLabel, 300),
            ElementPath = Clip(elementPath, 600),
            XPercent = Math.Clamp(xPercent, 0, 100),
            YPercent = Math.Clamp(yPercent, 0, 100),
            Comment = Clip(comment, 4000),
            CreatedByUsername = createdByUsername
        };

        _db.PocComments.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return (AddPocCommentResult.Ok, new PocCommentItem(
            entity.Id, entity.PageView, entity.ElementLabel, entity.ElementPath,
            entity.XPercent, entity.YPercent, entity.Comment, entity.Status.ToString(),
            entity.CreatedByUsername, entity.CreatedAt, CanDelete: true));
    }

    private static string Clip(string? value, int maxLength)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

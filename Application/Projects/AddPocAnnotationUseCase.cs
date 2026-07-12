using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum AddPocAnnotationResult
{
    Added,
    ProjectNotFound,

    /// <summary>Thiếu nhận xét — annotation không có nội dung thì agent không biết sửa gì.</summary>
    MissingComment
}

/// <summary>
/// Ghi một annotation trên POC: phần tử người dùng đã click (nhãn + đường dẫn CSS gần đúng, do script
/// annotation trong iframe gửi lên) và nhận xét của họ. ElementLabel trống (góp ý chung, không neo vào
/// phần tử nào) được thay bằng nhãn mặc định để danh sách vẫn đọc được.
/// </summary>
public class AddPocAnnotationUseCase
{
    // Khớp HasMaxLength của cột trong AppDbContext — cắt ở app để không văng lỗi DB khi client gửi dài hơn.
    private const int MaxLabelLength = 300;
    private const int MaxPathLength = 500;
    private const int MaxCommentLength = 2000;

    public const string GeneralLabel = "(góp ý chung cho cả trang)";

    private readonly AppDbContext _db;

    public AddPocAnnotationUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AddPocAnnotationResult> ExecuteAsync(
        Guid projectId, string? elementLabel, string? elementPath, string? comment, string? actorUsername,
        CancellationToken cancellationToken = default)
    {
        comment = comment?.Trim();
        if (string.IsNullOrEmpty(comment))
            return AddPocAnnotationResult.MissingComment;

        var projectExists = await _db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken);
        if (!projectExists)
            return AddPocAnnotationResult.ProjectNotFound;

        elementLabel = elementLabel?.Trim();
        if (string.IsNullOrEmpty(elementLabel))
            elementLabel = GeneralLabel;

        elementPath = elementPath?.Trim();
        if (string.IsNullOrEmpty(elementPath))
            elementPath = null;

        _db.PocAnnotations.Add(new PocAnnotation
        {
            ProjectId = projectId,
            AuthorUsername = actorUsername ?? "",
            ElementLabel = Truncate(elementLabel, MaxLabelLength)!,
            ElementPath = Truncate(elementPath, MaxPathLength),
            Comment = Truncate(comment, MaxCommentLength)!
        });

        await _db.SaveChangesAsync(cancellationToken);
        return AddPocAnnotationResult.Added;
    }

    private static string? Truncate(string? value, int max) =>
        value == null || value.Length <= max ? value : value[..(max - 1)] + "…";
}

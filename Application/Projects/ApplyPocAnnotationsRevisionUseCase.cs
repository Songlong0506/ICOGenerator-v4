using System.Text;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum ApplyPocAnnotationsRevisionResult
{
    Queued,

    /// <summary>Không có annotation nào (Open/Submitted) để gom thành yêu cầu chỉnh sửa.</summary>
    NothingToApply,

    /// <summary>Không có workflow nào đang chờ duyệt (cổng POC) để yêu cầu chỉnh sửa.</summary>
    NoWaitingRun,

    /// <summary>Đã dùng hết số vòng chỉnh sửa cho bước này.</summary>
    RevisionLimitReached
}

/// <summary>
/// Bước của đội Dev (quyền DeliveryAdvance): gom mọi annotation chưa xử lý (Open + Submitted) thành MỘT
/// nhận xét có cấu trúc rồi đi qua đúng cơ chế "Yêu cầu chỉnh sửa" sẵn có của cổng duyệt
/// (RequestStageRevisionUseCase) — agent sửa lại POC theo từng góp ý. Chỉ khi enqueue THÀNH CÔNG các
/// annotation mới được đánh dấu Processed; hai lần SaveChanges không atomic nhưng chiều hỏng an toàn:
/// tệ nhất annotation vẫn ở trạng thái cũ và lần bấm sau gom lại — không bao giờ mất góp ý.
/// </summary>
public class ApplyPocAnnotationsRevisionUseCase
{
    private readonly AppDbContext _db;
    private readonly RequestStageRevisionUseCase _requestStageRevision;

    public ApplyPocAnnotationsRevisionUseCase(AppDbContext db, RequestStageRevisionUseCase requestStageRevision)
    {
        _db = db;
        _requestStageRevision = requestStageRevision;
    }

    public async Task<ApplyPocAnnotationsRevisionResult> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var annotations = await _db.PocAnnotations
            .Where(a => a.ProjectId == projectId && a.Status != PocAnnotationStatus.Processed)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        if (annotations.Count == 0)
            return ApplyPocAnnotationsRevisionResult.NothingToApply;

        var feedback = ComposeFeedback(annotations.Select(a => (a.ElementLabel, a.Comment, a.AuthorUsername)));

        var result = await _requestStageRevision.ExecuteAsync(projectId, feedback);
        switch (result)
        {
            case RequestStageRevisionResult.NoWaitingRun:
                return ApplyPocAnnotationsRevisionResult.NoWaitingRun;
            case RequestStageRevisionResult.RevisionLimitReached:
                return ApplyPocAnnotationsRevisionResult.RevisionLimitReached;
        }

        var now = DateTime.UtcNow;
        foreach (var annotation in annotations)
        {
            annotation.Status = PocAnnotationStatus.Processed;
            annotation.ProcessedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return ApplyPocAnnotationsRevisionResult.Queued;
    }

    /// <summary>
    /// Dựng nhận xét có cấu trúc từ danh sách góp ý để agent sửa POC theo từng mục. Public static để
    /// test được định dạng mà không cần dựng cả use case.
    /// </summary>
    public static string ComposeFeedback(IEnumerable<(string ElementLabel, string Comment, string AuthorUsername)> annotations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Người dùng đã review POC trực tiếp trên giao diện và để lại các góp ý sau (mỗi mục: phần tử được chọn — nhận xét):");
        sb.AppendLine();

        var index = 0;
        foreach (var (label, comment, author) in annotations)
        {
            index++;
            sb.AppendLine($"{index}. [{label}] — {comment} (góp ý bởi {author})");
        }

        sb.AppendLine();
        sb.Append("Hãy chỉnh sửa POC theo ĐÚNG các góp ý trên; giữ nguyên mọi phần không được nhắc tới.");
        return sb.ToString();
    }
}

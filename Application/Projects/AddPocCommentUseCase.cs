using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum AddPocCommentResult
{
    Ok,
    ProjectNotFound,

    /// <summary>Nội dung ghi chú trống — không có gì để agent sửa theo.</summary>
    MissingComment,

    /// <summary>Project đã có quá nhiều ghi chú (chống spam/lỗi vòng lặp client).</summary>
    TooManyComments,

    /// <summary>Bản demo đã được nghiệm thu ⇒ nội dung đang khoá (xem <see cref="PocAcceptanceGate"/>).</summary>
    PocAccepted
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
    private readonly BriefVersionResolver _briefVersions;
    private readonly PocAcceptanceGate _acceptanceGate;

    public AddPocCommentUseCase(AppDbContext db, BriefVersionResolver briefVersions, PocAcceptanceGate acceptanceGate)
    {
        _db = db;
        _briefVersions = briefVersions;
        _acceptanceGate = acceptanceGate;
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
        Guid? shareLinkId = null,
        CancellationToken cancellationToken = default)
    {
        comment = comment?.Trim();
        if (string.IsNullOrEmpty(comment))
            return (AddPocCommentResult.MissingComment, null);

        if (!await _db.Projects.AnyAsync(x => x.Id == projectId, cancellationToken))
            return (AddPocCommentResult.ProjectNotFound, null);

        // Khoá sau nghiệm thu — chốt Ở ĐÂY chứ không ở controller vì đường ghim còn cửa thứ hai: khách
        // vào bằng link chia sẻ (PocShareController) không đi qua trang review nên không thấy nút khoá.
        if (await _acceptanceGate.IsLockedAsync(projectId, cancellationToken))
            return (AddPocCommentResult.PocAccepted, null);

        // Trần đếm ghi chú CÒN HIỆU LỰC: dòng đã thu hồi vẫn nằm lại làm lịch sử, không được phép chiếm
        // chỗ của ghi chú mới (nếu không, "không xoá được" sẽ tự khoá trang review lại sau vài buổi).
        if (await _db.PocComments.CountAsync(
                x => x.ProjectId == projectId && x.WithdrawnAtUtc == null, cancellationToken) >= MaxCommentsPerProject)
            return (AddPocCommentResult.TooManyComments, null);

        // Đóng dấu bản Brief mà POC này được dựng từ đó — bản đã duyệt cao nhất tại thời điểm ghim.
        var briefVersion = await _briefVersions.GetCurrentAsync(projectId, cancellationToken);

        var entity = new PocComment
        {
            ProjectId = projectId,
            Target = PocCommentTarget.Poc,
            BriefVersion = briefVersion,
            PageView = Clip(pageView, 200),
            ElementLabel = Clip(elementLabel, 300),
            ElementPath = Clip(elementPath, 600),
            XPercent = Math.Clamp(xPercent, 0, 100),
            YPercent = Math.Clamp(yPercent, 0, 100),
            Comment = Clip(comment, 4000),
            CreatedByUsername = createdByUsername,
            // Khách (ghi qua link chia sẻ) ghi BẢN NHÁP của riêng mình: chưa tới tay người tạo dự án cho tới
            // khi khách bấm "Send Note" (SubmitGuestPocCommentsUseCase).
            PocShareLinkId = shareLinkId,
            Status = shareLinkId.HasValue ? PocCommentStatus.GuestDraft : PocCommentStatus.Open
        };

        _db.PocComments.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        // Ghi chú vừa ghi: chưa đường nào, chưa vòng sửa nào ⇒ chính người gõ rút được ngay (xoá thật).
        return (AddPocCommentResult.Ok, shareLinkId.HasValue
            ? PocCommentItem.ForGuest(entity, locked: false)
            : PocCommentItem.From(entity, canManage: false, currentUsername: createdByUsername));
    }

    private static string Clip(string? value, int maxLength)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

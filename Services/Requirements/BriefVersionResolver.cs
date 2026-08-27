using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Trả về tên phiên bản Product Brief để ĐÓNG DẤU lên ghi chú (<see cref="PocComment.BriefVersion"/>).
/// Một chỗ duy nhất giữ quy tắc đánh số version, dùng chung với ApproveRequirementUseCase — nếu hai nơi
/// tự đếm "V{n}" theo hai cách thì bảng lịch sử sẽ gán ghi chú vào bản không tồn tại.
/// </summary>
public class BriefVersionResolver
{
    /// <summary>Chưa duyệt bản nào — cũng là dấu tạm của ghi chú Brief cho tới khi bản draft được duyệt.</summary>
    public const string DraftVersion = "draft";

    private readonly AppDbContext _db;
    private readonly IProjectArtifactCatalog _artifactCatalog;

    public BriefVersionResolver(AppDbContext db, IProjectArtifactCatalog artifactCatalog)
    {
        _db = db;
        _artifactCatalog = artifactCatalog;
    }

    /// <summary>
    /// Phiên bản Product Brief ĐÃ DUYỆT cao nhất của project ("V3"), hoặc <see cref="DraftVersion"/> khi
    /// chưa duyệt lần nào. Đây là bản mà POC đang phục vụ được dựng từ đó, nên cũng là dấu đúng cho một
    /// ghi chú vừa ghim lên POC.
    /// </summary>
    public async Task<string> GetCurrentAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var versions = await _db.ProjectDocuments.AsNoTracking()
            .Where(d => d.ProjectId == projectId
                        && d.IsApproved
                        && d.FileName == _artifactCatalog.ProductBrief.FileName)
            .Select(d => d.VersionName)
            .ToListAsync(cancellationToken);

        return Highest(versions);
    }

    /// <summary>Bản cao nhất trong danh sách tên version, hoặc <see cref="DraftVersion"/>. Tên lạ ⇒ bỏ qua.</summary>
    public static string Highest(IEnumerable<string> versionNames)
    {
        var max = versionNames
            .Select(TryParseNumber)
            .Where(n => n > 0)
            .DefaultIfEmpty(0)
            .Max();

        return max == 0 ? DraftVersion : $"V{max}";
    }

    private static int TryParseNumber(string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName) || !versionName.StartsWith('V'))
            return 0;

        return int.TryParse(versionName[1..], out var n) ? n : 0;
    }
}

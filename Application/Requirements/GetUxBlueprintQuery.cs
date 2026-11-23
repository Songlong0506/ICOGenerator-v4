using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// Đọc BẢN THIẾT KẾ UX của dự án cho popup «Thiết kế màn hình» ở trang Requirements — chỉ đọc. Người dùng không
/// rà hay chốt bản này (họ góp ý ở POC Review); popup tồn tại để thiết kế không phải là một hộp đen: ai cũng
/// thấy được ứng dụng có những màn nào, mỗi vai vào từ đâu, mỗi luồng đi qua những màn nào, và máy đã kiểm
/// ra những gì. Một nguồn duy nhất — cột <c>Project.UxBlueprint</c> — đọc tươi mỗi lần mở.
/// </summary>
public class GetUxBlueprintQuery
{
    private readonly AppDbContext _db;

    public GetUxBlueprintQuery(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Bản thiết kế; null khi dự án chưa được thiết kế (hoặc không tồn tại).</summary>
    public async Task<UxBlueprint?> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var json = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.UxBlueprint)
            .FirstOrDefaultAsync(cancellationToken);

        return UxBlueprintBuilder.IsDesigned(json) ? UxBlueprintBuilder.Parse(json) : null;
    }
}

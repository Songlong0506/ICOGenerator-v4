using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>
/// Đường dẫn bản demo của một biến thể đã chạy xong — để mở xem cạnh bản chính. Biến thể phải thuộc ĐÚNG
/// dự án trên URL (guard project chỉ xác nhận quyền với projectId, không biết gì về variantId), và đường
/// dẫn dựng từ hai Guid chứ không từ chuỗi người dùng gửi lên.
/// </summary>
public class GetPocVariantFileQuery
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _resolver;

    public GetPocVariantFileQuery(AppDbContext db, WorkspacePathResolver resolver)
    {
        _db = db;
        _resolver = resolver;
    }

    public async Task<string?> ExecuteAsync(Guid projectId, Guid variantId, CancellationToken cancellationToken = default)
    {
        // Chỉ cần dòng còn tồn tại: lượt Failed giữa chừng vẫn có thể đã dựng được nửa bản demo, và nửa đó
        // cũng là dữ kiện để đánh giá model.
        var exists = await _db.PocVariants.AsNoTracking()
            .AnyAsync(v => v.Id == variantId && v.ProjectId == projectId && v.Status != PocVariantStatus.Queued, cancellationToken);
        if (!exists)
            return null;

        var path = Path.Combine(_resolver.GetPocVariantPath(projectId, variantId), PocTemplate.MockupRelativePath);
        return File.Exists(path) ? path : null;
    }
}

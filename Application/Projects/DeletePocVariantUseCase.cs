using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum DeletePocVariantResult { Ok, NotFound, Running }

/// <summary>
/// Xoá một biến thể POC thử model: dòng DB + thư mục riêng của nó. Lượt đang chạy thì không xoá được —
/// agent đang ghi vào thư mục đó và runner sắp chốt chi phí vào chính dòng này. Lượt còn chờ thì xoá được
/// (huỷ trước khi chạy). Chi phí đã tiêu vẫn nằm nguyên ở AgentModelCallLogs/trang Usage: xoá kết quả thí
/// nghiệm không xoá được số tiền đã trả.
/// </summary>
public class DeletePocVariantUseCase
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _resolver;

    public DeletePocVariantUseCase(AppDbContext db, WorkspacePathResolver resolver)
    {
        _db = db;
        _resolver = resolver;
    }

    public async Task<DeletePocVariantResult> ExecuteAsync(Guid projectId, Guid variantId, CancellationToken cancellationToken = default)
    {
        var variant = await _db.PocVariants
            .FirstOrDefaultAsync(v => v.Id == variantId && v.ProjectId == projectId, cancellationToken);
        if (variant == null)
            return DeletePocVariantResult.NotFound;
        if (variant.Status == PocVariantStatus.Running)
            return DeletePocVariantResult.Running;

        _db.PocVariants.Remove(variant);
        await _db.SaveChangesAsync(cancellationToken);

        // Dọn đĩa SAU khi DB đã xoá: lỗi IO chỉ để lại một thư mục mồ côi, còn ngược lại sẽ là một dòng
        // trỏ vào thư mục trống. Best-effort.
        try
        {
            var path = _resolver.GetPocVariantPath(projectId, variantId);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }

        return DeletePocVariantResult.Ok;
    }
}

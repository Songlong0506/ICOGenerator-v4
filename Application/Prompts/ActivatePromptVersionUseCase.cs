using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Prompts;

/// <summary>
/// Kích hoạt (rollback về) MỘT phiên bản đã có: bản được chọn thành bản đang dùng, các bản khác của
/// cùng template tắt hết — không tạo snapshot mới, lịch sử giữ nguyên. Trả về PromptKey của phiên
/// bản (để controller redirect về trang chi tiết) hoặc null nếu không tồn tại.
/// </summary>
public class ActivatePromptVersionUseCase
{
    private readonly AppDbContext _db;
    private readonly IPromptOverrideProvider _overrides;
    private readonly IAuditLogger _audit;

    public ActivatePromptVersionUseCase(AppDbContext db, IPromptOverrideProvider overrides, IAuditLogger audit)
    {
        _db = db;
        _overrides = overrides;
        _audit = audit;
    }

    public async Task<string?> ExecuteAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        var target = await _db.PromptTemplateVersions
            .FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);
        if (target == null)
            return null;

        if (!target.IsActive)
        {
            var siblings = await _db.PromptTemplateVersions
                .Where(v => v.PromptKey == target.PromptKey && v.IsActive)
                .ToListAsync(cancellationToken);
            foreach (var sibling in siblings)
                sibling.IsActive = false;
            target.IsActive = true;

            await _db.SaveChangesAsync(cancellationToken);
            _overrides.Invalidate();

            await _audit.LogAsync(AuditCategory.Prompt, AuditAction.Update, target.PromptKey,
                $"Kích hoạt phiên bản v{target.VersionNumber} cho prompt \"{target.PromptKey}\"",
                cancellationToken: cancellationToken);
        }

        return target.PromptKey;
    }
}

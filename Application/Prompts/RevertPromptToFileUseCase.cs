using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Prompts;

/// <summary>
/// Gỡ mọi bản DB active của một template — nội dung FILE trong repo lại là bản đang dùng. Lịch sử
/// phiên bản giữ nguyên (kích hoạt lại được bất kỳ lúc nào).
/// </summary>
public class RevertPromptToFileUseCase
{
    private readonly AppDbContext _db;
    private readonly IPromptOverrideProvider _overrides;
    private readonly IAuditLogger _audit;

    public RevertPromptToFileUseCase(AppDbContext db, IPromptOverrideProvider overrides, IAuditLogger audit)
    {
        _db = db;
        _overrides = overrides;
        _audit = audit;
    }

    public async Task ExecuteAsync(string? promptKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(promptKey))
            return;

        var key = promptKey.Trim();
        var actives = await _db.PromptTemplateVersions
            .Where(v => v.PromptKey == key && v.IsActive)
            .ToListAsync(cancellationToken);
        if (actives.Count == 0)
            return;

        foreach (var version in actives)
            version.IsActive = false;

        await _db.SaveChangesAsync(cancellationToken);
        _overrides.Invalidate();

        await _audit.LogAsync(AuditCategory.Prompt, AuditAction.Update, key,
            $"Quay về nội dung file cho prompt \"{key}\" (gỡ bản DB active)",
            cancellationToken: cancellationToken);
    }
}

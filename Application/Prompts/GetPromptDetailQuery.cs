using ICOGenerator.Data;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Prompts;

public record PromptVersionItemVm(
    Guid Id,
    int VersionNumber,
    string? ChangeNote,
    bool IsActive,
    string? CreatedByUsername,
    DateTime CreatedAt);

/// <summary>
/// Chi tiết một template: nội dung ĐANG DÙNG (bản DB active, không có thì file), nội dung file gốc
/// và danh sách phiên bản (mới nhất trước, không kèm Content — xem qua Diff).
/// </summary>
public record PromptDetailVm(
    string PromptKey,
    bool FileExists,
    int? ActiveVersionNumber,
    string ActiveContent,
    IReadOnlyList<PromptVersionItemVm> Versions);

/// <summary>Trả null khi template không tồn tại (không có file dưới /Prompts và cũng không có phiên bản DB).</summary>
public class GetPromptDetailQuery
{
    private readonly AppDbContext _db;
    private readonly PromptFileCatalog _catalog;
    private readonly PromptTemplateService _templates;

    public GetPromptDetailQuery(AppDbContext db, PromptFileCatalog catalog, PromptTemplateService templates)
    {
        _db = db;
        _catalog = catalog;
        _templates = templates;
    }

    public async Task<PromptDetailVm?> ExecuteAsync(string? promptKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(promptKey))
            return null;

        var key = promptKey.Trim();
        var versions = await _db.PromptTemplateVersions.AsNoTracking()
            .Where(v => v.PromptKey == key)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(cancellationToken);

        var fileExists = _catalog.Exists(key);
        if (!fileExists && versions.Count == 0)
            return null;

        var active = versions.FirstOrDefault(v => v.IsActive);
        // Không có bản DB active thì nội dung đang dùng là file; file cũng không còn (template mồ côi
        // chưa kích hoạt bản nào) thì đành trống — UI hiển thị cảnh báo.
        var activeContent = active?.Content
            ?? (fileExists ? _templates.GetFileContent(key) : string.Empty);

        return new PromptDetailVm(
            key,
            fileExists,
            active?.VersionNumber,
            activeContent,
            versions
                .Select(v => new PromptVersionItemVm(v.Id, v.VersionNumber, v.ChangeNote, v.IsActive, v.CreatedByUsername, v.CreatedAt))
                .ToList());
    }
}

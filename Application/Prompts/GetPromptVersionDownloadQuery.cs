using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Prompts;

public record PromptVersionDownloadVm(string FileName, string Content);

/// <summary>
/// Xuất nội dung MỘT phiên bản DB của template ra file .md — để đồng bộ ngược một bản đã "chín"
/// (thắng eval) về repo, hoặc mang sang môi trường khác (import = nạp file vào editor rồi Lưu).
/// Tên file mang cả số phiên bản ("requirement-chat.v3.db-v2.md") để không nhầm với file gốc.
/// Trả null khi phiên bản không tồn tại.
/// </summary>
public class GetPromptVersionDownloadQuery
{
    private readonly AppDbContext _db;

    public GetPromptVersionDownloadQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PromptVersionDownloadVm?> ExecuteAsync(string? promptKey, int versionNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(promptKey) || versionNumber < 1)
            return null;

        var key = promptKey.Trim();
        var content = await _db.PromptTemplateVersions.AsNoTracking()
            .Where(v => v.PromptKey == key && v.VersionNumber == versionNumber)
            .Select(v => v.Content)
            .FirstOrDefaultAsync(cancellationToken);
        if (content == null)
            return null;

        var baseName = Path.GetFileNameWithoutExtension(key);
        return new PromptVersionDownloadVm($"{baseName}.db-v{versionNumber}.md", content);
    }
}

using ICOGenerator.Data;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Prompts;

/// <summary>Một template trong bảng Prompt Studio: nguồn đang dùng (file hay bản DB v{n}) + thống kê lịch sử.</summary>
public record PromptStudioItemVm(
    string PromptKey,
    bool FileExists,
    int VersionCount,
    int? ActiveVersionNumber,
    DateTime? LastChangedAt,
    string? LastChangedBy);

public record PromptStudioPageVm(IReadOnlyList<PromptStudioItemVm> Templates);

/// <summary>
/// Dữ liệu trang Prompt Studio: mọi template .md dưới /Prompts (từ <see cref="PromptFileCatalog"/>)
/// ghép với thống kê phiên bản DB. Template có phiên bản DB nhưng file đã bị xoá khỏi repo vẫn được
/// liệt kê (FileExists=false) — lịch sử và bản active của nó vẫn dùng được.
/// </summary>
public class GetPromptStudioPageQuery
{
    private readonly AppDbContext _db;
    private readonly PromptFileCatalog _catalog;

    public GetPromptStudioPageQuery(AppDbContext db, PromptFileCatalog catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<PromptStudioPageVm> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Chỉ kéo metadata (không Content — cột LOB) cho phần thống kê.
        var versions = await _db.PromptTemplateVersions.AsNoTracking()
            .Select(v => new { v.PromptKey, v.VersionNumber, v.IsActive, v.CreatedAt, v.CreatedByUsername })
            .ToListAsync(cancellationToken);

        var statsByKey = versions
            .GroupBy(v => v.PromptKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var latest = g.OrderByDescending(v => v.VersionNumber).First();
                return new
                {
                    Count = g.Count(),
                    Active = g.Where(v => v.IsActive).Select(v => (int?)v.VersionNumber).FirstOrDefault(),
                    LastAt = (DateTime?)latest.CreatedAt,
                    LastBy = latest.CreatedByUsername
                };
            }, StringComparer.OrdinalIgnoreCase);

        var fileKeys = _catalog.PromptKeys;
        var orphanKeys = statsByKey.Keys
            .Where(k => !fileKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        var items = fileKeys.Select(key => Build(key, fileExists: true))
            .Concat(orphanKeys.Select(key => Build(key, fileExists: false)))
            .ToList();

        return new PromptStudioPageVm(items);

        PromptStudioItemVm Build(string key, bool fileExists)
        {
            var stats = statsByKey.GetValueOrDefault(key);
            return new PromptStudioItemVm(key, fileExists,
                stats?.Count ?? 0, stats?.Active, stats?.LastAt, stats?.LastBy);
        }
    }
}

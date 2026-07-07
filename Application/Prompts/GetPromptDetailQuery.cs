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
/// Điểm eval gộp theo MỘT phiên bản prompt của template này (null = nội dung file): trả lời trực
/// tiếp "phiên bản nào tốt hơn" bằng số thay vì lần từng run.
/// </summary>
public record PromptEvalStatVm(int? VersionNumber, double AverageScore, int ResultCount, DateTime LastRunAt);

/// <summary>
/// Chi tiết một template: nội dung ĐANG DÙNG (bản DB active, không có thì file), danh sách phiên bản
/// (mới nhất trước, không kèm Content — xem qua Diff) và điểm eval gộp theo phiên bản.
/// </summary>
public record PromptDetailVm(
    string PromptKey,
    bool FileExists,
    int? ActiveVersionNumber,
    string ActiveContent,
    IReadOnlyList<PromptVersionItemVm> Versions,
    IReadOnlyList<PromptEvalStatVm> EvalStats);

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
                .ToList(),
            await LoadEvalStatsAsync(key, cancellationToken));
    }

    // Gộp điểm judge của MỌI kết quả eval thuộc các scenario của template này theo phiên bản prompt
    // đã đo (EvalResult.PromptVersionNumber; null = nội dung file). Kết quả lỗi (Score null) bỏ qua.
    private async Task<IReadOnlyList<PromptEvalStatVm>> LoadEvalStatsAsync(string key, CancellationToken cancellationToken)
    {
        var scenarioIds = await _db.EvalScenarios.AsNoTracking()
            .Where(s => s.PromptKey == key)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        if (scenarioIds.Count == 0)
            return Array.Empty<PromptEvalStatVm>();

        var results = await _db.EvalResults.AsNoTracking()
            .Where(r => scenarioIds.Contains(r.EvalScenarioId) && r.Score != null)
            .Select(r => new { r.PromptVersionNumber, r.Score, r.CreatedAt })
            .ToListAsync(cancellationToken);

        // "file" (null) đứng đầu như mốc 0 của diff, rồi các phiên bản DB tăng dần.
        return results
            .GroupBy(r => r.PromptVersionNumber)
            .Select(g => new PromptEvalStatVm(
                g.Key,
                Math.Round(g.Average(r => r.Score!.Value), 2),
                g.Count(),
                g.Max(r => r.CreatedAt)))
            .OrderBy(s => s.VersionNumber ?? 0)
            .ToList();
    }
}

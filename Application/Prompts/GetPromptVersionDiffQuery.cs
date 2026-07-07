using ICOGenerator.Data;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Prompts;

/// <summary>Diff giữa hai mốc nội dung của một template; nhãn kiểu "file" / "v3" cho header UI.</summary>
public record PromptDiffVm(
    string PromptKey,
    string FromLabel,
    string ToLabel,
    IReadOnlyList<DiffLine> Lines);

/// <summary>
/// Diff nội dung một template giữa hai mốc: số phiên bản DB (1-based) hoặc <c>0</c> = nội dung FILE
/// trong repo. Tái dùng <see cref="DocumentDiffService"/> (LCS theo dòng) của lịch sử tài liệu.
/// Trả null khi template/mốc không tồn tại.
/// </summary>
public class GetPromptVersionDiffQuery
{
    private readonly AppDbContext _db;
    private readonly PromptFileCatalog _catalog;
    private readonly PromptTemplateService _templates;
    private readonly DocumentDiffService _diff;

    public GetPromptVersionDiffQuery(
        AppDbContext db, PromptFileCatalog catalog, PromptTemplateService templates, DocumentDiffService diff)
    {
        _db = db;
        _catalog = catalog;
        _templates = templates;
        _diff = diff;
    }

    public async Task<PromptDiffVm?> ExecuteAsync(
        string? promptKey, int fromVersion, int toVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(promptKey) || fromVersion < 0 || toVersion < 0)
            return null;

        var key = promptKey.Trim();
        var from = await GetContentAsync(key, fromVersion, cancellationToken);
        var to = await GetContentAsync(key, toVersion, cancellationToken);
        if (from == null || to == null)
            return null;

        return new PromptDiffVm(key, Label(fromVersion), Label(toVersion), _diff.Diff(from, to));
    }

    private static string Label(int version) => version == 0 ? "file" : $"v{version}";

    private async Task<string?> GetContentAsync(string key, int version, CancellationToken cancellationToken)
    {
        if (version > 0)
        {
            return await _db.PromptTemplateVersions.AsNoTracking()
                .Where(v => v.PromptKey == key && v.VersionNumber == version)
                .Select(v => v.Content)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (!_catalog.Exists(key))
            return null;
        try
        {
            return _templates.GetFileContent(key);
        }
        catch (IOException)
        {
            return null;
        }
    }
}

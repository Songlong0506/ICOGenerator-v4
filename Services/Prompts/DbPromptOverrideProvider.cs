using ICOGenerator.Data;
using Microsoft.Extensions.Caching.Memory;

namespace ICOGenerator.Services.Prompts;

/// <summary>
/// Hiện thực <see cref="IPromptOverrideProvider"/> trên bảng PromptTemplateVersions. Prompt được nạp
/// trên MỌI lời gọi LLM nên không thể query DB mỗi lần: toàn bộ các bản active nạp MỘT query rồi cache
/// (IMemoryCache — singleton, dùng chung mọi request) trong <see cref="CacheDuration"/>; các thao tác
/// ghi ở Prompt Studio gọi <see cref="Invalidate"/> nên thay đổi có hiệu lực ngay trong cùng tiến trình.
/// Fail-open: DB lỗi ⇒ trả null (log warning), mọi prompt rơi về nội dung file — app không bao giờ
/// hỏng vì bảng phiên bản.
/// </summary>
public class DbPromptOverrideProvider : IPromptOverrideProvider
{
    internal const string CacheKey = "PromptOverrides.Active";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DbPromptOverrideProvider> _logger;

    public DbPromptOverrideProvider(AppDbContext db, IMemoryCache cache, ILogger<DbPromptOverrideProvider> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public PromptOverride? GetActiveOverride(string promptKey)
    {
        try
        {
            var overrides = _cache.GetOrCreate(CacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheDuration;
                return LoadActiveOverrides();
            });
            return overrides != null && overrides.TryGetValue(promptKey, out var found) ? found : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không đọc được bản prompt active từ DB — dùng nội dung file cho {PromptKey}.", promptKey);
            return null;
        }
    }

    public void Invalidate() => _cache.Remove(CacheKey);

    // Sync (không async) vì được gọi từ PromptTemplateService.Get — API sync nằm trên đường dựng prompt.
    private Dictionary<string, PromptOverride> LoadActiveOverrides()
    {
        return _db.PromptTemplateVersions
            .Where(v => v.IsActive)
            .Select(v => new { v.PromptKey, v.Id, v.VersionNumber, v.Content })
            .ToList()
            // Bất biến "một bản active mỗi key" đã có unique-ish index hỗ trợ; dữ liệu bẩn thì lấy bản mới nhất.
            .GroupBy(v => v.PromptKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(v => v.VersionNumber).Select(v => new PromptOverride(v.Id, v.VersionNumber, v.Content)).First(),
                StringComparer.OrdinalIgnoreCase);
    }
}

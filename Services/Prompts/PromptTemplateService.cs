using System.Collections.Concurrent;

namespace ICOGenerator.Services.Prompts;

public class PromptTemplateService
{
    private readonly IWebHostEnvironment _environment;
    private readonly IPromptOverrideProvider? _overrides;

    // Prompt nằm trong file .md tĩnh (copy lúc build, PreserveNewest) nên không đổi khi
    // chạy. Cache nội dung theo relativePath để tránh đọc đĩa lại trên mỗi bước agent.
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    // overrides (tùy chọn — test stub kế thừa không cần truyền): bản prompt chỉnh runtime từ Prompt
    // Studio. Template có bản DB active thì bản đó THAY nội dung file; provider fail-open về null nên
    // đường file luôn là chỗ rơi an toàn.
    public PromptTemplateService(IWebHostEnvironment environment, IPromptOverrideProvider? overrides = null)
    {
        _environment = environment;
        _overrides = overrides;
    }

    // virtual: cho phép test thay bằng stub không phụ thuộc hệ thống file (prompt thật vẫn nạp như cũ ở runtime).
    public virtual string Get(string relativePath) =>
        _overrides?.GetActiveOverride(relativePath)?.Content ?? GetFileContent(relativePath);

    /// <summary>
    /// Nội dung FILE của template (bỏ qua bản DB active) — Prompt Studio dùng làm baseline hiển thị/diff
    /// và chụp phiên bản gốc trước lần sửa đầu tiên.
    /// </summary>
    public virtual string GetFileContent(string relativePath) => Cache.GetOrAdd(relativePath, ReadTemplate);

    private string ReadTemplate(string relativePath)
    {
        var safeRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "Prompts", safeRelativePath));
        var rootPath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "Prompts"));

        if (!fullPath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid prompt path.");
        }

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Prompt template not found: {relativePath}", fullPath);

        return File.ReadAllText(fullPath);
    }
}

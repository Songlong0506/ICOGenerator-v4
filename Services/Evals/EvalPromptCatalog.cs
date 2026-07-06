namespace ICOGenerator.Services.Evals;

/// <summary>
/// Danh mục các template prompt (.md dưới /Prompts) mà scenario eval có thể nhắm tới. Quét thư mục
/// một lần rồi cache (prompt là file tĩnh, copy lúc build — cùng giả định với PromptTemplateService).
/// Key trả về là đường dẫn tương đối kiểu "BA/requirement-chat.v3.md" (separator '/'), khớp đúng định
/// dạng PromptTemplateService.Get nhận.
/// </summary>
public class EvalPromptCatalog
{
    private readonly IWebHostEnvironment _environment;
    private readonly Lazy<IReadOnlyList<string>> _keys;

    public EvalPromptCatalog(IWebHostEnvironment environment)
    {
        _environment = environment;
        _keys = new Lazy<IReadOnlyList<string>>(ScanPromptKeys);
    }

    public IReadOnlyList<string> PromptKeys => _keys.Value;

    public bool Exists(string promptKey) =>
        PromptKeys.Contains(promptKey, StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> ScanPromptKeys()
    {
        var root = Path.Combine(_environment.ContentRootPath, "Prompts");
        if (!Directory.Exists(root))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Select(full => Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

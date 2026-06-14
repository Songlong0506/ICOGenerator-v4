using System.Collections.Concurrent;

namespace ICOGenerator.Services.Prompts;

public class PromptTemplateService
{
    private readonly IWebHostEnvironment _environment;

    // Prompt nằm trong file .md tĩnh (copy lúc build, PreserveNewest) nên không đổi khi
    // chạy. Cache nội dung theo relativePath để tránh đọc đĩa lại trên mỗi bước agent.
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public PromptTemplateService(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public string Get(string relativePath) => Cache.GetOrAdd(relativePath, ReadTemplate);

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

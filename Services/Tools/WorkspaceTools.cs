using System.ComponentModel;

namespace ICOGenerator.Services.Tools;

public class WorkspaceTools
{
    private readonly IConfiguration _configuration;
    public WorkspaceTools(IConfiguration configuration) { _configuration = configuration; }
    public string CurrentWorkspacePath { get; private set; } = string.Empty;

    public void SetWorkspace(string projectName)
    {
        var rootPath = _configuration["AgentWorkspace:RootPath"];
        if (string.IsNullOrWhiteSpace(rootPath)) throw new InvalidOperationException("AgentWorkspace:RootPath is missing.");
        CurrentWorkspacePath = Path.GetFullPath(Path.Combine(rootPath, MakeSafeFolderName(projectName)));
        Directory.CreateDirectory(CurrentWorkspacePath);
    }

    [Description("Write a source code or documentation file into the current workspace.")]
    public async Task<string> WriteFile(string relativePath, string content)
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(relativePath);
        ValidateExtension(fullPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(fullPath, content);
        return $"File written: {relativePath}";
    }

    [Description("Read a file from the current workspace.")]
    public async Task<string> ReadFile(string relativePath)
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(relativePath);
        if (!File.Exists(fullPath)) return $"File not found: {relativePath}";
        var content = await File.ReadAllTextAsync(fullPath);
        return content.Length > 12000 ? content[..12000] + "\n...[truncated]" : content;
    }

    [Description("List all files in the current workspace.")]
    public string ListFiles()
    {
        EnsureWorkspace();
        var files = Directory.GetFiles(CurrentWorkspacePath, "*.*", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
            .Select(x => Path.GetRelativePath(CurrentWorkspacePath, x)).Take(500).ToList();
        return files.Count == 0 ? "No files found." : string.Join(Environment.NewLine, files);
    }

    [Description("Search files by keyword in relative path.")]
    public string SearchFiles(string keyword)
    {
        EnsureWorkspace();
        var files = Directory.GetFiles(CurrentWorkspacePath, "*.*", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
            .Select(x => Path.GetRelativePath(CurrentWorkspacePath, x))
            .Where(x => x.Contains(keyword, StringComparison.OrdinalIgnoreCase)).Take(100).ToList();
        return files.Count == 0 ? "No matched files." : string.Join(Environment.NewLine, files);
    }

    [Description("Replace text in an existing file.")]
    public async Task<string> ReplaceInFile(string relativePath, string oldText, string newText)
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(relativePath);
        if (!File.Exists(fullPath)) return $"File not found: {relativePath}";
        var content = await File.ReadAllTextAsync(fullPath);
        if (!content.Contains(oldText)) return $"Old text not found in file: {relativePath}";
        await File.WriteAllTextAsync(fullPath, content.Replace(oldText, newText));
        return $"File updated: {relativePath}";
    }

    private string GetSafeFullPath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(CurrentWorkspacePath, relativePath));
        if (!fullPath.StartsWith(CurrentWorkspacePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid file path.");
        return fullPath;
    }
    private void ValidateExtension(string fullPath)
    {
        var allowed = _configuration.GetSection("AllowedFileExtensions").Get<string[]>() ?? [];
        if (allowed.Length == 0) return;
        var fileName = Path.GetFileName(fullPath);
        if (!allowed.Any(x => fileName.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"File extension is not allowed: {fileName}");
    }
    private void EnsureWorkspace()
    {
        if (string.IsNullOrWhiteSpace(CurrentWorkspacePath)) throw new InvalidOperationException("Workspace is not initialized.");
    }
    private static string MakeSafeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return name.Replace(" ", "-").ToLowerInvariant();
    }
}

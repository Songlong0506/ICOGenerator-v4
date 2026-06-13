using System.ComponentModel;
using ICOGenerator.Services.Artifacts;

namespace ICOGenerator.Services.Tools;

public class WorkspaceTools
{
    private readonly IConfiguration _configuration;
    private readonly WorkspacePathResolver _workspacePathResolver;

    public WorkspaceTools(IConfiguration configuration, WorkspacePathResolver workspacePathResolver)
    {
        _configuration = configuration;
        _workspacePathResolver = workspacePathResolver;
    }
    public string CurrentWorkspacePath { get; private set; } = string.Empty;

    public void SetWorkspace(string projectName)
    {
        CurrentWorkspacePath = _workspacePathResolver.GetProjectWorkspacePath(projectName);
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

    private const int MaxFullReadBytes = 200 * 1024;

    [Description("Read a file from the current workspace. Files under 200 KB are returned in full. Only larger files are paginated: pass offset (a line number) to read the next chunk, and the response will end with '[truncated: use offset=N to continue reading]' telling you the exact next line.")]
    public async Task<string> ReadFile(string relativePath, int offset = 0)
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(relativePath);
        if (!File.Exists(fullPath)) return $"File not found: {relativePath}";

        var fileSize = new FileInfo(fullPath).Length;
        if (fileSize <= MaxFullReadBytes)
            return await File.ReadAllTextAsync(fullPath);

        // Only genuinely large files are paginated by line offset.
        var lines = await File.ReadAllLinesAsync(fullPath);
        if (offset < 0) offset = 0;
        if (offset >= lines.Length) return $"Offset {offset} exceeds file length ({lines.Length} lines).";

        const int maxChars = 16000;
        var sb = new System.Text.StringBuilder();
        var i = offset;
        while (i < lines.Length)
        {
            var line = $"{i + 1}: {lines[i]}\n";
            if (sb.Length + line.Length > maxChars) break;
            sb.Append(line);
            i++;
        }

        if (i < lines.Length)
            sb.Append($"\n...[truncated: use offset={i} to continue reading]");

        return sb.ToString();
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

    public void RenameFolder(string oldRelativePath, string newRelativePath)
    {
        EnsureWorkspace();

        var oldFullPath = GetSafeFullPath(oldRelativePath);
        var newFullPath = GetSafeFullPath(newRelativePath);

        if (!Directory.Exists(oldFullPath))
            throw new InvalidOperationException($"Folder not found: {oldRelativePath}");

        if (Directory.Exists(newFullPath))
            throw new InvalidOperationException($"Target folder already exists: {newRelativePath}");

        Directory.Move(oldFullPath, newFullPath);
    }

    private string GetSafeFullPath(string relativePath)
    {
        return _workspacePathResolver.GetSafeFullPath(CurrentWorkspacePath, relativePath);
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
}

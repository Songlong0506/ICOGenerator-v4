using System.ComponentModel;
using System.Text.Json;
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

    // Ambient cancellation for the current agent run, shared with long-running tools (e.g. CommandTools)
    // so a workflow cancel / app shutdown actually stops a spawned process instead of waiting out the timeout.
    public CancellationToken RunCancellationToken { get; private set; } = CancellationToken.None;

    public void SetWorkspace(string projectKey)
    {
        CurrentWorkspacePath = _workspacePathResolver.GetProjectWorkspacePath(projectKey);
        Directory.CreateDirectory(CurrentWorkspacePath);
    }

    public void SetRunCancellation(CancellationToken cancellationToken) => RunCancellationToken = cancellationToken;

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
        var files = Directory.EnumerateFiles(CurrentWorkspacePath, "*.*", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
            .Select(x => Path.GetRelativePath(CurrentWorkspacePath, x)).Take(500).ToList();
        return files.Count == 0 ? "No files found." : string.Join(Environment.NewLine, files);
    }

    [Description("Search files by keyword in relative path.")]
    public string SearchFiles(string keyword)
    {
        EnsureWorkspace();
        var files = Directory.EnumerateFiles(CurrentWorkspacePath, "*.*", SearchOption.AllDirectories)
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

    [Description("Set the POC feature UI AND customise the page shell for the generated demo, in ONE call. " +
        "'content' (required): only the inner HTML for the content region (no <html>/<head>/<body>/sidebar/topbar); it is placed between the POC_CONTENT markers in 03_Implementation/poc-demo.html. " +
        "'appName': the application/product name, shown in the sidebar header and the browser tab — never leave it as the template default \"App Name\". " +
        "'breadcrumb': the top-bar breadcrumb text, e.g. \"Home > Orders\". " +
        "'navItems': the left sidebar menu — an array of objects { \"label\": string, \"icon\"?: string, \"children\"?: (string | { \"label\": string, \"icon\"?: string })[] }. 'icon' is an optional Bootstrap Icons name shown before the label (e.g. \"house\", \"cart3\", \"people\", \"bag\", \"gear\"; full list at https://icons.getbootstrap.com — the leading \"bi-\" is optional); set one for every item, and if omitted a neutral default icon is used. 'children' is optional and turns the entry into an expandable group. Set these to the real screens, not the template's Overview/Module A/Module B/Settings. " +
        "The rest of the shell (style/script, topbar, popups) is kept untouched. Use this instead of ReplaceInFile for the POC.")]
    public async Task<string> SetPocContent(string content, string? appName = null, string? breadcrumb = null, JsonElement? navItems = null)
    {
        EnsureWorkspace();
        var fullPath = GetSafeFullPath(PocTemplate.MockupRelativePath);
        if (!File.Exists(fullPath)) return $"File not found: {PocTemplate.MockupRelativePath}";

        var current = await File.ReadAllTextAsync(fullPath);
        var updated = PocTemplate.ReplaceContent(current, content ?? string.Empty);
        if (updated == null)
            return $"POC content markers not found in file: {PocTemplate.MockupRelativePath}";

        // Customise the shell bits outside the content markers (App Name, title, breadcrumb, menu). Each step no-ops on empty/malformed input, so a slip in one never blocks the others or the content — worst case the shell stays as the template, never a failure.
        if (!string.IsNullOrWhiteSpace(appName))
            updated = PocTemplate.ReplaceAppName(updated, appName);
        if (!string.IsNullOrWhiteSpace(breadcrumb))
            updated = PocTemplate.ReplaceBreadcrumb(updated, breadcrumb);
        if (navItems is { } navJson)
        {
            var items = PocNavItem.ParseList(navJson);
            if (items.Count > 0)
                updated = PocTemplate.ReplaceNav(updated, items);
        }

        await File.WriteAllTextAsync(fullPath, updated);
        return $"POC content updated: {PocTemplate.MockupRelativePath}";
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
        // Match the true file extension exactly (e.g. ".cs") and reject extensionless names, rather than
        // a loose suffix check on the whole file name.
        var ext = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(ext) || !allowed.Any(x => x.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"File extension is not allowed: {Path.GetFileName(fullPath)}");
    }
    private void EnsureWorkspace()
    {
        if (string.IsNullOrWhiteSpace(CurrentWorkspacePath)) throw new InvalidOperationException("Workspace is not initialized.");
    }
}

using System.Net;
using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements.Templates;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public class GetDocumentPreviewQuery
{
    private readonly AppDbContext _db;
    private readonly DocxTemplateWriter _docxWriter;
    private readonly WorkspacePathResolver _workspacePathResolver;
    private readonly ILogger<GetDocumentPreviewQuery> _logger;

    public GetDocumentPreviewQuery(
        AppDbContext db,
        DocxTemplateWriter docxWriter,
        WorkspacePathResolver workspacePathResolver,
        ILogger<GetDocumentPreviewQuery> logger)
    {
        _db = db;
        _docxWriter = docxWriter;
        _workspacePathResolver = workspacePathResolver;
        _logger = logger;
    }

    // Addressed either by DB row Id, or by projectId + workspace-relative path for on-disk-only
    // files (no DB row). Those previously got a fresh random Guid each request so the Id lookup
    // never matched and preview was broken; the path branch fixes that.
    public async Task<object?> ExecuteAsync(Guid id, Guid projectId = default, string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
            return await PreviewWorkspaceFileAsync(projectId, path);

        var doc = await _db.ProjectDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (doc == null)
            return null;

        var html = BuildHtml(doc.FilePath, doc.Content);

        if (string.IsNullOrWhiteSpace(html))
            html = "<p class=\"doc-empty\">No document yet</p>";

        return new { name = doc.FileName, html };
    }

    private async Task<object?> PreviewWorkspaceFileAsync(Guid projectId, string relativePath)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId);
        if (project == null)
            return null;

        var workspacePath = _workspacePathResolver.GetProjectWorkspacePath(
            WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));

        string fullPath;
        try
        {
            // The path is client-supplied, so enforce workspace containment (rejects any
            // '..'/symlink escape) before touching the filesystem.
            fullPath = _workspacePathResolver.GetSafeFullPath(workspacePath, relativePath);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (!File.Exists(fullPath))
            return null;

        var html = BuildHtml(fullPath, ReadWorkspaceContent(fullPath));

        if (string.IsNullOrWhiteSpace(html))
            html = "<p class=\"doc-empty\">No document yet</p>";

        return new { name = Path.GetFileName(fullPath), html };
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".css", ".csv", ".html", ".htm", ".js", ".json", ".md", ".sql", ".txt", ".xml", ".yml", ".yaml"
    };

    // Mirrors the dashboard's preview rules: .docx is rendered from the file by BuildHtml (so the
    // returned content is unused); text files are read; anything else gets a "no preview" note.
    private string ReadWorkspaceContent(string fullPath)
    {
        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (!TextExtensions.Contains(extension))
            return $"Preview is not available for binary file: {Path.GetFileName(fullPath)}";

        try
        {
            var content = File.ReadAllText(fullPath);
            return content.Length > 12000 ? content[..12000] + "\n...[truncated]" : content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read workspace file {FilePath} for preview.", fullPath);
            return $"Preview unavailable ({Path.GetFileName(fullPath)}): {ex.Message}";
        }
    }

    private string BuildHtml(string? filePath, string content)
    {
        if (!string.IsNullOrWhiteSpace(filePath)
            && filePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
            && File.Exists(filePath))
        {
            try
            {
                return _docxWriter.ExtractHtml(filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
            {
                // Fall back to stored plain-text if the file can't be read/parsed. Narrowed from a
                // bare catch (which hid every failure) and logged so a corrupt .docx is diagnosable.
                _logger.LogWarning(ex, "Could not extract HTML from document {FilePath}; falling back to stored content.", filePath);
            }
        }

        return RenderPlainText(content);
    }

    private static string RenderPlainText(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        var encoded = WebUtility.HtmlEncode(content)
            .Replace("\r\n", "\n")
            .Replace("\n", "<br>");

        return "<p>" + encoded + "</p>";
    }
}

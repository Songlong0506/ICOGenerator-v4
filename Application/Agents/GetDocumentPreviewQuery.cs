using System.Net;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements.Templates;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public class GetDocumentPreviewQuery
{
    private readonly AppDbContext _db;
    private readonly DocxTemplateWriter _docxWriter;
    private readonly ILogger<GetDocumentPreviewQuery> _logger;

    public GetDocumentPreviewQuery(AppDbContext db, DocxTemplateWriter docxWriter, ILogger<GetDocumentPreviewQuery> logger)
    {
        _db = db;
        _docxWriter = docxWriter;
        _logger = logger;
    }

    public async Task<object?> ExecuteAsync(Guid id)
    {
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
                // Fall back to the stored plain-text content if the file cannot be read/parsed.
                // Narrowed (was a bare catch that hid every failure) and logged so a corrupt
                // .docx is diagnosable instead of silently degrading.
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

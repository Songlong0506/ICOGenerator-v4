using ICOGenerator.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public record DocumentDownloadResult(string FilePath, string FileName, string ContentType);

public class GetDocumentDownloadQuery
{
    private readonly IAppDbContext _db;

    public GetDocumentDownloadQuery(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<DocumentDownloadResult?> ExecuteAsync(Guid id)
    {
        var doc = await _db.ProjectDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (doc == null || string.IsNullOrWhiteSpace(doc.FilePath) || !File.Exists(doc.FilePath))
            return null;

        return new DocumentDownloadResult(
            doc.FilePath,
            doc.FileName,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
    }
}

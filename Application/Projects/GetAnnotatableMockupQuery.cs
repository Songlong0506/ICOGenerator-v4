using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public record AnnotatableMockupResult(string Html);

/// <summary>
/// Trả nội dung mockup ĐÃ TIÊM script annotation (chế độ review). Script được tham chiếu qua
/// &lt;script src&gt; tới file tĩnh trong wwwroot (static files phục vụ TRƯỚC auth nên iframe sandbox
/// opaque-origin vẫn nạp được); nó chạy BÊN TRONG iframe sandbox và chỉ nói chuyện với trang cha qua
/// postMessage — không nới lỏng gì CSP sandbox của mockup (vẫn không có allow-same-origin).
/// </summary>
public class GetAnnotatableMockupQuery
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;

    private const string EmbedScriptTag = "<script src=\"/js/poc-annotate-embed.js\" defer></script>";

    public GetAnnotatableMockupQuery(AppDbContext db, WorkspacePathResolver workspacePathResolver)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
    }

    public async Task<AnnotatableMockupResult?> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return null;

        var filePath = _workspacePathResolver.GetMockupPath(
            WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));

        if (!File.Exists(filePath))
            return null;

        var html = await File.ReadAllTextAsync(filePath, cancellationToken);

        // Tiêm trước </body> nếu có (vị trí chuẩn để DOM đã dựng xong); mockup do LLM sinh có thể thiếu
        // </body> — khi đó nối vào cuối, script dùng defer/DOMContentLoaded nên vẫn chạy đúng.
        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        html = bodyClose >= 0
            ? html.Insert(bodyClose, EmbedScriptTag)
            : html + EmbedScriptTag;

        return new AnnotatableMockupResult(html);
    }
}

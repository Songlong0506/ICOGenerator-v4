using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public record DeliverableFileVm(string RelativePath, string Name, long SizeBytes, string ModifiedUtc, bool TextPreviewable);

public record DeliverablePhaseVm(string Phase, IReadOnlyList<DeliverableFileVm> Files);

public record ProjectDeliverablesVm(
    Guid ProjectId, string ProjectName, bool HasWorkspace, bool PocReady, IReadOnlyList<DeliverablePhaseVm> Phases);

/// <summary>
/// Liệt kê toàn bộ sản phẩm (deliverable) mà pipeline đã sinh ra trong workspace của project,
/// gom theo thư mục giai đoạn (01_Requirement … 05_Test): UI/UX spec, kiến trúc, code đa file,
/// biên bản review, test report, POC… Chỉ đọc filesystem, không sửa gì.
/// </summary>
public class GetProjectDeliverablesQuery
{
    // Giới hạn tổng số file để một thư mục lớn (vd có node_modules sót) không làm trang treo.
    private const int MaxFiles = 500;

    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _resolver;

    public GetProjectDeliverablesQuery(AppDbContext db, WorkspacePathResolver resolver)
    {
        _db = db;
        _resolver = resolver;
    }

    public async Task<ProjectDeliverablesVm?> ExecuteAsync(Guid projectId)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId);
        if (project == null)
            return null;

        var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
        var workspacePath = _resolver.GetProjectWorkspacePath(projectKey);
        var pocReady = File.Exists(_resolver.GetMockupPath(projectKey));

        if (!Directory.Exists(workspacePath))
            return new ProjectDeliverablesVm(project.Id, project.Name, false, pocReady, Array.Empty<DeliverablePhaseVm>());

        var phases = new List<DeliverablePhaseVm>();
        var budget = MaxFiles;

        foreach (var phase in ProjectWorkspaceLayout.Phases)
        {
            if (budget <= 0)
                break;

            var phasePath = Path.Combine(workspacePath, phase);
            if (!Directory.Exists(phasePath))
                continue;

            var files = new List<DeliverableFileVm>();
            foreach (var file in EnumerateFiles(phasePath, budget))
            {
                var info = new FileInfo(file);
                var rel = Path.GetRelativePath(workspacePath, file).Replace(Path.DirectorySeparatorChar, '/');
                files.Add(new DeliverableFileVm(
                    rel,
                    Path.GetFileName(file),
                    info.Length,
                    info.LastWriteTimeUtc.ToString("o"),
                    DeliverableFileTypes.IsTextPreviewable(file)));
            }

            budget -= files.Count;
            if (files.Count > 0)
                phases.Add(new DeliverablePhaseVm(
                    phase,
                    files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return new ProjectDeliverablesVm(project.Id, project.Name, true, pocReady, phases);
    }

    // Duyệt file đệ quy, bỏ qua thư mục nặng/không phải sản phẩm và dừng khi đạt giới hạn.
    private static IEnumerable<string> EnumerateFiles(string root, int limit)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var count = 0;

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] subDirs;
            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
                subDirs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue; // thư mục không đọc được → bỏ qua
            }

            foreach (var f in files)
            {
                yield return f;
                if (++count >= limit)
                    yield break;
            }

            foreach (var sub in subDirs)
            {
                if (!DeliverableFileTypes.SkipDirectories.Contains(Path.GetFileName(sub)))
                    stack.Push(sub);
            }
        }
    }
}

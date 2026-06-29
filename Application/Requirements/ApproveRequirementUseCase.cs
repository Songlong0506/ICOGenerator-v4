using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public class ApproveRequirementUseCase
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;
    private readonly IProjectArtifactCatalog _artifactCatalog;
    private readonly IWorkflowOrchestrator _workflowOrchestrator;
    private readonly ILogger<ApproveRequirementUseCase> _logger;

    public ApproveRequirementUseCase(AppDbContext db, WorkspacePathResolver workspacePathResolver, IProjectArtifactCatalog artifactCatalog, IWorkflowOrchestrator workflowOrchestrator, ILogger<ApproveRequirementUseCase> logger)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
        _artifactCatalog = artifactCatalog;
        _workflowOrchestrator = workflowOrchestrator;
        _logger = logger;
    }

    public async Task<ApproveRequirementResult> ExecuteAsync(Guid projectId)
    {
        var project = await _db.Projects
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == projectId);

        if (project == null)
            return ApproveRequirementResult.ProjectNotFound;

        var draftDocs = project.Documents
            .Where(x => x.VersionName == "draft" && !x.IsApproved)
            .ToList();

        if (!draftDocs.Any())
            return ApproveRequirementResult.NoDraftDocuments;

        // AI Design Spec không còn được sinh ở "Write Requirement" nữa — nó được sinh từ Product Brief
        // đã duyệt ngay bên dưới. Vì vậy điều kiện duyệt giờ là phải có Product Brief draft.
        var productBrief = draftDocs.FirstOrDefault(x => x.FileName == _artifactCatalog.ProductBrief.FileName);
        if (productBrief == null)
            return ApproveRequirementResult.MissingProductBrief;

        var nextVersion = project.Documents
            .Where(x => x.IsApproved && x.VersionName.StartsWith("V"))
            .Select(x => int.TryParse(x.VersionName.Replace("V", ""), out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        var versionName = $"V{nextVersion}";

        foreach (var doc in draftDocs)
        {
            doc.VersionName = versionName;
            doc.IsApproved = true;

            if (!string.IsNullOrWhiteSpace(doc.FilePath))
            {
                var fileName = Path.GetFileName(doc.FilePath);
                var phaseFolder = Path.GetDirectoryName(Path.GetDirectoryName(doc.FilePath)); // <root>/<phase>

                if (!string.IsNullOrWhiteSpace(phaseFolder))
                    doc.FilePath = Path.Combine(phaseFolder, versionName, fileName);
            }
        }

        // Promote draft folders on disk BEFORE persisting: the doc changes are still only in the
        // change tracker, so if the destructive move fails we return without SaveChangesAsync and
        // the DB stays on the draft — no half-approved state, retryable. Previously an IOException
        // here (e.g. an open .docx) escaped as an HTTP 500.
        try
        {
            PromoteDraftFolders(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name), draftDocs.Select(x => x.Folder).Distinct(), versionName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ApproveRequirementResult.PromotionFailed;
        }

        await _db.SaveChangesAsync();

        // Approval is now committed. Sinh AI Design Spec từ Product Brief đã duyệt là một lời gọi LLM chậm —
        // trước đây chạy ĐỒNG BỘ ngay đây làm màn hình Approve treo chờ. Nay đẩy sang một workflow NỀN
        // ("Requirement Progress") để tiến độ report live; worker sinh spec xong sẽ tự khởi động delivery
        // workflow dựng POC. Đây chỉ là vài INSERT (nhanh), nên Approve trả về ngay.
        try
        {
            await _workflowOrchestrator.StartAiDesignSpecWorkflowAsync(projectId, versionName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Requirement {Version} approved for project {ProjectId} but starting the AI Design Spec workflow failed.", versionName, projectId);
            return ApproveRequirementResult.WorkflowStartFailed;
        }

        return ApproveRequirementResult.Approved;
    }

    private void PromoteDraftFolders(string projectKey, IEnumerable<string> phases, string versionName)
    {
        foreach (var phase in phases)
        {
            var draftPath = _workspacePathResolver.GetPhaseDraftPath(projectKey, phase);
            var versionPath = _workspacePathResolver.GetPhaseVersionPath(projectKey, phase, versionName);

            if (!Directory.Exists(draftPath))
                continue;

            if (Directory.Exists(versionPath))
                Directory.Delete(versionPath, true);

            Directory.Move(draftPath, versionPath);
        }
    }
}

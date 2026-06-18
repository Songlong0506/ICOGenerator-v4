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

        var aiDesignSpec = draftDocs.FirstOrDefault(x => x.FileName == _artifactCatalog.AiDesignSpec.FileName);
        if (aiDesignSpec == null)
            return ApproveRequirementResult.MissingAiDesignSpec;

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

        // Promote the draft folders on disk BEFORE persisting the approval. The doc-entity
        // changes above are still only in the change tracker, so if the (destructive) folder
        // move fails we return without SaveChangesAsync and the DB keeps pointing at the
        // draft — no half-approved state, and the user can retry once the file is released.
        // Previously an IOException here (e.g. an open .docx) escaped as an HTTP 500.
        try
        {
            PromoteDraftFolders(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name), draftDocs.Select(x => x.Folder).Distinct(), versionName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ApproveRequirementResult.PromotionFailed;
        }

        await _db.SaveChangesAsync();

        // The approval is now committed (folders moved + DB saved). Starting the delivery
        // workflow is a separate, retryable step — if it throws, don't surface a 500 that hides
        // an already-successful approval. Report a distinct result the user can act on.
        try
        {
            await _workflowOrchestrator.StartDeliveryWorkflowAsync(projectId, versionName, aiDesignSpec.Content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Requirement {Version} approved for project {ProjectId} but starting the delivery workflow failed.", versionName, projectId);
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

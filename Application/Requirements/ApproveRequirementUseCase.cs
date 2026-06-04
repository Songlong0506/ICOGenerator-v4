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

    public ApproveRequirementUseCase(AppDbContext db, WorkspacePathResolver workspacePathResolver, IProjectArtifactCatalog artifactCatalog, IWorkflowOrchestrator workflowOrchestrator)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
        _artifactCatalog = artifactCatalog;
        _workflowOrchestrator = workflowOrchestrator;
    }

    public async Task<ApproveRequirementResult> ExecuteAsync(Guid projectId)
    {
        var project = await _db.Projects
            .Include(x => x.Documents)
            .FirstAsync(x => x.Id == projectId);

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
            doc.Folder = $"docs/{versionName}";
            doc.IsApproved = true;

            if (!string.IsNullOrWhiteSpace(doc.FilePath))
            {
                var fileName = Path.GetFileName(doc.FilePath);
                var docsFolder = Path.GetDirectoryName(Path.GetDirectoryName(doc.FilePath));

                if (!string.IsNullOrWhiteSpace(docsFolder))
                    doc.FilePath = Path.Combine(docsFolder, versionName, fileName);
            }
        }

        RenameDraftFolder(project.Name, versionName);

        await _db.SaveChangesAsync();

        await _workflowOrchestrator.StartDeliveryWorkflowAsync(projectId, versionName, aiDesignSpec.Content);

        return ApproveRequirementResult.Approved;
    }

    private void RenameDraftFolder(string projectName, string versionName)
    {
        var draftPath = _workspacePathResolver.GetDraftDocsPath(projectName);
        var versionPath = _workspacePathResolver.GetVersionDocsPath(projectName, versionName);

        if (!Directory.Exists(draftPath))
            return;

        if (Directory.Exists(versionPath))
            Directory.Delete(versionPath, true);

        Directory.Move(draftPath, versionPath);
    }
}

public enum ApproveRequirementResult
{
    Approved,
    NoDraftDocuments,
    MissingAiDesignSpec
}

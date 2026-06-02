using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workspace;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Requirements;

public class ApproveRequirementUseCase
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;
    private readonly IProjectArtifactCatalog _artifactCatalog;

    public ApproveRequirementUseCase(AppDbContext db, WorkspacePathResolver workspacePathResolver, IProjectArtifactCatalog artifactCatalog)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
        _artifactCatalog = artifactCatalog;
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

        var dev = await _db.Agents.FirstOrDefaultAsync(x => x.Name == "Developer");

        var workflowRun = new WorkflowRun
        {
            ProjectId = projectId,
            Name = $"Delivery Workflow {versionName}",
            Status = WorkflowRunStatus.Queued,
            CurrentStage = WorkflowStageKey.Implementation,
            StartedAt = null
        };

        var implementationTask = new AgentTask
        {
            WorkflowRunId = workflowRun.Id,
            ProjectId = projectId,
            AgentId = dev?.Id,
            Type = AgentTaskType.Implementation,
            Status = AgentTaskStatus.Queued,
            Title = "Generate POC from approved AI Design Spec",
            Input = aiDesignSpec.Content
        };

        _db.WorkflowRuns.Add(workflowRun);
        _db.AgentTasks.Add(implementationTask);
        await _db.SaveChangesAsync();

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

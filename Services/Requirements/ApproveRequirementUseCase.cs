using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Workspace;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Requirements;

public class ApproveRequirementUseCase
{
    private readonly AppDbContext _db;
    private readonly AgentRunService _agentRunService;
    private readonly WorkspacePathResolver _workspacePathResolver;

    public ApproveRequirementUseCase(
        AppDbContext db,
        AgentRunService agentRunService,
        WorkspacePathResolver workspacePathResolver)
    {
        _db = db;
        _agentRunService = agentRunService;
        _workspacePathResolver = workspacePathResolver;
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

        var aiDesignSpec = draftDocs.FirstOrDefault(x => x.FileName == "AIDesignSpec.docx");

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

        var dev = await _db.Agents.FirstOrDefaultAsync(x => x.Name == "Developer");

        var workflowRun = new WorkflowRun
        {
            ProjectId = projectId,
            Name = $"Delivery Workflow {versionName}",
            Status = "Running",
            CurrentStage = "Implementation",
            StartedAt = DateTime.UtcNow
        };

        var implementationTask = new AgentTask
        {
            WorkflowRunId = workflowRun.Id,
            ProjectId = projectId,
            AgentId = dev?.Id,
            Type = "Implementation",
            Status = dev == null ? "Queued" : "Running",
            Title = "Generate POC from approved AI Design Spec",
            Input = aiDesignSpec.Content,
            StartedAt = dev == null ? null : DateTime.UtcNow
        };

        _db.WorkflowRuns.Add(workflowRun);
        _db.AgentTasks.Add(implementationTask);

        await _db.SaveChangesAsync();

        if (dev != null)
        {
            var output = await _agentRunService.RunAsync(
                projectId,
                dev.Id,
                $"""
User đã approve requirement.

Chỉ sử dụng AI Design Spec bên dưới để generate code.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

# AI Design Spec

{aiDesignSpec.Content}
""");

            implementationTask.Status = "Completed";
            implementationTask.Output = output;
            implementationTask.FinishedAt = DateTime.UtcNow;
            workflowRun.Status = "Completed";
            workflowRun.CurrentStage = "Completed";
            workflowRun.FinishedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

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

using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public enum GenerateTechnicalDocsResult
{
    Started,
    ProjectNotFound,
    NoApprovedRequirement
}

// Team dev trigger ở Agent Dashboard: kiểm tra đã có requirement duyệt (Product Brief + AI Design Spec)
// rồi khởi tạo workflow nền sinh BRD/SRS/FSD/UserStories. Tách guard ra đây để dashboard báo lỗi thân
// thiện trước khi enqueue, thay vì để task nền fail.
public class GenerateTechnicalDocsUseCase
{
    private readonly AppDbContext _db;
    private readonly IProjectArtifactCatalog _artifactCatalog;
    private readonly IWorkflowOrchestrator _workflowOrchestrator;

    public GenerateTechnicalDocsUseCase(AppDbContext db, IProjectArtifactCatalog artifactCatalog, IWorkflowOrchestrator workflowOrchestrator)
    {
        _db = db;
        _artifactCatalog = artifactCatalog;
        _workflowOrchestrator = workflowOrchestrator;
    }

    public async Task<GenerateTechnicalDocsResult> ExecuteAsync(Guid projectId)
    {
        if (!await _db.Projects.AnyAsync(x => x.Id == projectId))
            return GenerateTechnicalDocsResult.ProjectNotFound;

        // Cần đã có AI Design Spec đã duyệt (V{n}) — đây là nguồn để soạn tài liệu kỹ thuật.
        var hasApprovedSpec = await _db.ProjectDocuments.AnyAsync(x =>
            x.ProjectId == projectId &&
            x.IsApproved &&
            x.VersionName.StartsWith("V") &&
            x.FileName == _artifactCatalog.AiDesignSpec.FileName);

        if (!hasApprovedSpec)
            return GenerateTechnicalDocsResult.NoApprovedRequirement;

        await _workflowOrchestrator.StartTechnicalDocsWorkflowAsync(projectId);
        return GenerateTechnicalDocsResult.Started;
    }
}

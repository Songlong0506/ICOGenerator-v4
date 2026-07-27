using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum ConfirmAssumptionsResult { Ok, ProjectNotFound, NothingPending, SpecMissing }

/// <summary>
/// Cổng "Xác nhận & dựng bản demo": người dùng đã rà xong danh sách giả định của AI Design Spec
/// (<c>Project.PendingAssumptionsVersion</c>, do <see cref="AgentTaskWorker"/> đặt sau khi sinh spec) và
/// đồng ý với chúng ⇒ gỡ cổng rồi khởi động delivery workflow dựng POC — đúng lời gọi mà worker trước
/// đây tự chạy ngay sau khi sinh spec.
///
/// Cổng cố tình đặt Ở ĐÂY, không phải sau khi POC dựng xong: một giả định sai chỉ lộ ra khi xem POC là
/// đã tốn trọn một lượt dựng (lượt đắt nhất tuyến), trong khi rà vài dòng chữ chỉ tốn vài giây.
/// </summary>
public class ConfirmSpecAssumptionsUseCase
{
    private readonly AppDbContext _db;
    private readonly IProjectArtifactCatalog _artifactCatalog;
    private readonly IWorkflowOrchestrator _workflowOrchestrator;

    public ConfirmSpecAssumptionsUseCase(
        AppDbContext db,
        IProjectArtifactCatalog artifactCatalog,
        IWorkflowOrchestrator workflowOrchestrator)
    {
        _db = db;
        _artifactCatalog = artifactCatalog;
        _workflowOrchestrator = workflowOrchestrator;
    }

    public async Task<ConfirmAssumptionsResult> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);

        if (project == null)
            return ConfirmAssumptionsResult.ProjectNotFound;

        var version = project.PendingAssumptionsVersion;
        if (string.IsNullOrWhiteSpace(version))
            return ConfirmAssumptionsResult.NothingPending;

        var spec = ProjectDocumentLookup.GetContent(project, _artifactCatalog.AiDesignSpec.FileName, version);
        if (string.IsNullOrWhiteSpace(spec))
            return ConfirmAssumptionsResult.SpecMissing;

        // Gỡ cổng TRƯỚC khi enqueue: hai tab cùng bấm thì tab thua thấy NothingPending thay vì tạo ra
        // hai delivery run song song trên cùng workspace.
        project.PendingAssumptionsVersion = null;
        await _db.SaveChangesAsync(cancellationToken);

        await _workflowOrchestrator.StartDeliveryWorkflowAsync(projectId, version, spec);
        return ConfirmAssumptionsResult.Ok;
    }
}

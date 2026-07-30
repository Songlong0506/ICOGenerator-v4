using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows;

public class WorkflowOrchestrator : IWorkflowOrchestrator
{
    // Tên run của vòng "Write Requirement". Dùng chung giữa nơi TẠO run, cổng chống bấm trùng bên dưới và
    // trang Requirements (khoá nút khi run đang bay) để ba chỗ không thể lệch nhau vì một chuỗi gõ tay.
    public const string RequirementDraftRunName = "Write Requirement";

    private readonly AppDbContext _db;

    public WorkflowOrchestrator(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> StartDeliveryWorkflowAsync(Guid projectId, string requirementVersionName, string aiDesignSpecContent)
    {
        // Bắt đầu pipeline ở bước đầu tiên (khai báo trong DeliveryPipeline) thay vì
        // cứng nhắc giao thẳng cho Developer. Các bước sau do AgentTaskWorker hand-off.
        var first = DeliveryPipeline.First;

        var firstAgent = await _db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RoleKey == first.Role);

        var workflowRun = new WorkflowRun
        {
            ProjectId = projectId,
            Name = $"Delivery Workflow {requirementVersionName}",
            Status = WorkflowRunStatus.Queued,
            CurrentStage = first.Stage,
            StartedAt = null
        };

        var firstTask = new AgentTask
        {
            WorkflowRunId = workflowRun.Id,
            ProjectId = projectId,
            AgentId = firstAgent?.Id,
            Type = first.TaskType,
            Status = AgentTaskStatus.Queued,
            Title = first.Title,
            Input = aiDesignSpecContent
        };

        _db.WorkflowRuns.Add(workflowRun);
        _db.AgentTasks.Add(firstTask);
        await _db.SaveChangesAsync();

        return workflowRun.Id;
    }

    public async Task<Guid> StartAiDesignSpecWorkflowAsync(Guid projectId, string requirementVersionName)
    {
        var ba = await _db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst);

        // Tên chứa "Requirement" để UI hiển thị panel "Requirement Progress" (run một bước của BA, không
        // phải pipeline delivery). versionName được nhét vào Input để worker biết phiên bản nào cần sinh spec.
        var workflowRun = new WorkflowRun
        {
            ProjectId = projectId,
            Name = $"Requirement Design Spec {requirementVersionName}",
            Status = WorkflowRunStatus.Queued,
            CurrentStage = WorkflowStageKey.RequirementApproved,
            StartedAt = null
        };

        var specTask = new AgentTask
        {
            WorkflowRunId = workflowRun.Id,
            ProjectId = projectId,
            AgentId = ba?.Id,
            Type = AgentTaskType.AiDesignSpec,
            Status = AgentTaskStatus.Queued,
            Title = "Sinh AI Design Spec từ Product Brief đã duyệt",
            Input = requirementVersionName
        };

        _db.WorkflowRuns.Add(workflowRun);
        _db.AgentTasks.Add(specTask);
        await _db.SaveChangesAsync();

        return workflowRun.Id;
    }

    public async Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId, bool coalesceWithActiveRun = false)
    {
        // CHỐNG BẤM TRÙNG. Sau khi POST, WriteRequirement redirect về Index và trang render lại NGAY: lúc đó
        // lượt BA mời bấm nút vẫn là lượt cuối hội thoại (run vừa xếp hàng, chưa ghi lượt "đã tạo xong"),
        // nên nút lại sáng và bấm tiếp được — mỗi lần thêm một run. Worker khoá theo project nên chúng không
        // giẫm file nhau, nhưng vẫn chạy TUẦN TỰ hết: mỗi run là 2–3 lời gọi LLM đắt sinh lại đúng bản draft
        // ấy, cộng một panel tiến độ rác trên timeline. Gộp về run đang bay ⇒ lần bấm thừa thành no-op.
        //
        // Còn một khe hẹp giữa lệnh đọc và SaveChanges (hai request song song từ hai tab cùng qua cửa) —
        // chấp nhận được: kết quả xấu nhất là 2 run thay vì hàng loạt, và UI đã khoá nút trong lúc run bay.
        if (coalesceWithActiveRun)
        {
            var activeRun = await _db.WorkflowRuns
                .AsNoTracking()
                .Where(x => x.ProjectId == projectId
                    && x.Name == RequirementDraftRunName
                    && (x.Status == WorkflowRunStatus.Queued || x.Status == WorkflowRunStatus.Running))
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync();

            if (activeRun != null)
                return activeRun.Id;
        }

        var ba = await _db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst);

        var workflowRun = new WorkflowRun
        {
            ProjectId = projectId,
            Name = RequirementDraftRunName,
            Status = WorkflowRunStatus.Queued,
            CurrentStage = WorkflowStageKey.RequirementApproved,
            StartedAt = null
        };

        var draftTask = new AgentTask
        {
            WorkflowRunId = workflowRun.Id,
            ProjectId = projectId,
            AgentId = ba?.Id,
            Type = AgentTaskType.RequirementAnalysis,
            Status = AgentTaskStatus.Queued,
            Title = "Generate/update requirement documents from conversation"
        };

        _db.WorkflowRuns.Add(workflowRun);
        _db.AgentTasks.Add(draftTask);
        await _db.SaveChangesAsync();

        return workflowRun.Id;
    }
}

using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Workflows;

/// <summary>
/// Một bước trong quy trình giao hàng: gắn một <see cref="WorkflowStageKey"/> với
/// vai trò agent (<see cref="AgentRoleKey"/>) và loại việc (<see cref="AgentTaskType"/>)
/// sẽ thực thi nó.
/// </summary>
public record PipelineStep(
    WorkflowStageKey Stage,
    AgentRoleKey Role,
    AgentTaskType TaskType,
    string Title);

/// <summary>
/// Định nghĩa khai báo của pipeline "BA → Tech Lead → Dev → Tester".
/// Thứ tự phần tử = thứ tự hand-off. Output của bước trước trở thành input của bước sau.
///
/// Đây là điểm mở rộng duy nhất: thêm/đổi/chèn một vai chỉ là thêm một dòng vào
/// <see cref="Steps"/> — <c>WorkflowOrchestrator</c> và <c>AgentTaskWorker</c> giữ nguyên,
/// generic, không cần if/else theo từng stage.
/// </summary>
public static class DeliveryPipeline
{
    public static readonly IReadOnlyList<PipelineStep> Steps = new[]
    {
        new PipelineStep(WorkflowStageKey.ArchitectureDesign, AgentRoleKey.TechLead,  AgentTaskType.ArchitectureDesign, "Đề xuất kiến trúc từ AI Design Spec"),
        new PipelineStep(WorkflowStageKey.Implementation,     AgentRoleKey.Developer, AgentTaskType.Implementation,     "Sinh POC từ kiến trúc đã đề xuất"),
        new PipelineStep(WorkflowStageKey.Testing,            AgentRoleKey.Tester,    AgentTaskType.Testing,            "Viết & chạy test, báo lỗi cho POC"),
    };

    /// <summary>Bước đầu tiên của pipeline.</summary>
    public static PipelineStep First => Steps[0];

    /// <summary>
    /// Trả về bước kế tiếp sau <paramref name="current"/>, hoặc <c>null</c> nếu
    /// <paramref name="current"/> là bước cuối (đã hoàn tất pipeline).
    /// </summary>
    public static PipelineStep? Next(WorkflowStageKey current)
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Stage == current)
                return i + 1 < Steps.Count ? Steps[i + 1] : null;
        }

        return null;
    }
}

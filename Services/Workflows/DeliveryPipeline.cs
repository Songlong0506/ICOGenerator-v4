using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Workflows;

/// <summary>
/// Nguồn dữ liệu đầu vào cho một bước pipeline.
/// </summary>
public enum PipelineInputSource
{
    /// <summary>Dùng nội dung AI Design Spec đã duyệt của project.</summary>
    DesignSpec,

    /// <summary>Dùng output của bước ngay trước (hand-off).</summary>
    PreviousOutput
}

/// <summary>
/// Một bước trong quy trình giao hàng: gắn một <see cref="WorkflowStageKey"/> với
/// vai trò agent, loại việc, nguồn input và số vòng tool tối đa cho phép.
/// </summary>
public record PipelineStep(
    WorkflowStageKey Stage,
    AgentRoleKey Role,
    AgentTaskType TaskType,
    string Title,
    PipelineInputSource InputSource,
    int MaxSteps);

/// <summary>
/// Định nghĩa khai báo của pipeline giao hàng. Thứ tự phần tử = thứ tự bước.
///
/// Quy trình có CỔNG DUYỆT giữa mọi bước: mỗi bước chạy xong thì workflow dừng ở
/// <see cref="Domain.Enums.WorkflowRunStatus.WaitingForHuman"/>; người dùng bấm duyệt
/// (ApproveStageUseCase) mới enqueue bước kế. Mục tiêu: xem trước rẻ (POC) và chốt
/// từng cổng trước khi đầu tư bước đắt (full code) — tránh build cả team rồi mới biết sai.
///
/// Đây là điểm mở rộng duy nhất: thêm/đổi/chèn vai chỉ là thêm một dòng vào <see cref="Steps"/>.
/// </summary>
public static class DeliveryPipeline
{
    public static readonly IReadOnlyList<PipelineStep> Steps = new[]
    {
        new PipelineStep(WorkflowStageKey.PocPreview,         AgentRoleKey.Developer, AgentTaskType.PocPreview,         "Tạo POC HTML để xem trước",        PipelineInputSource.DesignSpec,     10),
        new PipelineStep(WorkflowStageKey.ArchitectureDesign, AgentRoleKey.TechLead,  AgentTaskType.ArchitectureDesign, "Đề xuất kiến trúc từ AI Design Spec", PipelineInputSource.DesignSpec,  6),
        new PipelineStep(WorkflowStageKey.Implementation,     AgentRoleKey.Developer, AgentTaskType.Implementation,     "Sinh code đầy đủ từ kiến trúc",    PipelineInputSource.PreviousOutput, 14),
        new PipelineStep(WorkflowStageKey.Testing,            AgentRoleKey.Tester,    AgentTaskType.Testing,            "Viết & chạy test, báo lỗi",        PipelineInputSource.PreviousOutput, 8),
    };

    /// <summary>Bước đầu tiên của pipeline (POC preview).</summary>
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

    /// <summary>Tra cứu bước theo stage; <c>null</c> nếu stage không thuộc pipeline.</summary>
    public static PipelineStep? Find(WorkflowStageKey stage)
    {
        foreach (var step in Steps)
            if (step.Stage == stage)
                return step;

        return null;
    }
}

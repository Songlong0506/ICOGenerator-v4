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
        new PipelineStep(WorkflowStageKey.ArchitectureDesign, AgentRoleKey.TechLead,  AgentTaskType.ArchitectureDesign, "Đề xuất kiến trúc từ AI Design Spec", PipelineInputSource.DesignSpec,  8),
        // Implementation sinh dự án thật nhiều file. Mỗi bước = 1 lần gọi LLM = 1 action, nên budget phải đủ rộng;
        // agent nên dùng WriteFiles (ghi nhiều file/lần) để khỏi tiêu hết bước cho từng file lẻ. Nếu vẫn cạn,
        // AgentRunService còn một lượt "chốt kết quả" cuối để giữ phần đã làm thay vì fail trắng.
        new PipelineStep(WorkflowStageKey.Implementation,     AgentRoleKey.Developer, AgentTaskType.Implementation,     "Sinh code đầy đủ từ kiến trúc",    PipelineInputSource.PreviousOutput, 40),
        // Tech Lead soát code Developer vừa hiện thực TRƯỚC khi giao Tester — bắt sớm lệch kiến trúc/thiếu
        // tính năng/lỗi rõ ở cổng rẻ này, thay vì để Tester tốn lượt phát hiện. Review chỉ đọc file + ghi 1
        // báo cáo nên budget vừa phải; output (tóm tắt + phát hiện) thành input cho bước Testing.
        new PipelineStep(WorkflowStageKey.CodeReview,         AgentRoleKey.TechLead,  AgentTaskType.CodeReview,         "Review code đã hiện thực",         PipelineInputSource.PreviousOutput, 12),
        new PipelineStep(WorkflowStageKey.Testing,            AgentRoleKey.Tester,    AgentTaskType.Testing,            "Viết & chạy test, báo lỗi",        PipelineInputSource.PreviousOutput, 8),
    };

    /// <summary>
    /// Số lần tự sửa lỗi tối đa cho một workflow run. Khi Tester báo FAIL, worker tự giao
    /// Developer sửa rồi chạy lại Testing — lặp tới khi PASS hoặc chạm trần này (tránh đốt
    /// token vô hạn nếu lỗi không hội tụ; hết trần thì dừng và để người xem lại báo cáo test).
    /// </summary>
    public const int MaxBugFixAttempts = 3;

    /// <summary>
    /// Bước sửa lỗi — KHÔNG nằm trong <see cref="Steps"/> vì nó là một CHU TRÌNH quanh Testing
    /// (Testing↔BugFix), không phải hand-off tuyến tính. Worker dùng định nghĩa này khi Tester
    /// báo FAIL; <see cref="Next"/> cố tình không trả về nó để pipeline tuyến tính vẫn đọc thẳng.
    /// </summary>
    public static readonly PipelineStep BugFixStep = new(
        WorkflowStageKey.BugFix, AgentRoleKey.Developer, AgentTaskType.BugFix,
        "Sửa lỗi theo báo cáo test", PipelineInputSource.PreviousOutput, 30);

    /// <summary>Bước Testing (tra từ <see cref="Steps"/>) — dùng để enqueue lại sau khi sửa lỗi.</summary>
    public static readonly PipelineStep TestingStep =
        Steps.First(s => s.Stage == WorkflowStageKey.Testing);

    /// <summary>Bước đầu tiên của pipeline (POC preview).</summary>
    public static PipelineStep First => Steps[0];

    /// <summary>
    /// Mọi stage thuộc quy trình delivery (các bước tuyến tính + BugFix). Dùng để nhận diện một
    /// <see cref="Domain.WorkflowRun"/> do MAF engine quản lý (tách khỏi luồng "Write Requirement").
    /// Khai báo dạng mảng để EF dịch được <c>Contains</c> thành mệnh đề IN.
    /// </summary>
    public static readonly WorkflowStageKey[] DeliveryStages =
        Steps.Select(s => s.Stage).Append(BugFixStep.Stage).ToArray();

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

    /// <summary>
    /// Tra cứu bước theo stage (gồm cả bước sửa lỗi ngoài chuỗi tuyến tính); <c>null</c> nếu
    /// stage không thuộc pipeline. Dùng cho việc tra MaxSteps theo stage hiện tại của run.
    /// </summary>
    public static PipelineStep? Find(WorkflowStageKey stage)
    {
        if (stage == WorkflowStageKey.BugFix)
            return BugFixStep;

        foreach (var step in Steps)
            if (step.Stage == stage)
                return step;

        return null;
    }
}

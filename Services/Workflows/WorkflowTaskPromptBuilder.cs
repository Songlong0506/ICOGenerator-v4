using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;

namespace ICOGenerator.Services.Workflows;

/// <summary>
/// Dựng user-prompt cho một bước pipeline theo <see cref="AgentTaskType"/>.
/// Prompt nằm ở file template trong <c>Prompts/Workflow/</c> (nạp qua
/// <see cref="PromptTemplateService"/>); chỗ <c>{{input}}</c> được thay bằng nội
/// dung hand-off (output của bước trước, hoặc AI Design Spec cho bước đầu tiên).
///
/// Hành vi sâu theo vai (Tech Lead/Dev/Tester) đến từ system-prompt của agent
/// (instruction theo RoleKey); template ở đây chỉ mô tả *việc cần làm* cho bước.
///
/// Khi project bật "Bosch template" (<c>useBoschTemplate</c>), hai bước
/// thiết kế và hiện thực dùng biến thể "-bosch" để ép code theo khung chuẩn Bosch
/// (.NET + Angular) đã được clone vào workspace; các bước còn lại dùng chung template.
///
/// Với task CHỈNH SỬA (có <c>revisionFeedback</c> — người duyệt yêu cầu sửa lại bước tại cổng
/// duyệt), prompt gốc được nối thêm khối <c>Workflow/revision.v1.md</c>: nhắc agent rằng sản
/// phẩm lần trước còn nguyên trong workspace, kèm bàn giao cũ + nhận xét, và yêu cầu SỬA trên
/// cái đã có thay vì làm lại từ đầu.
/// </summary>
public class WorkflowTaskPromptBuilder
{
    private readonly PromptTemplateService _promptTemplateService;

    public WorkflowTaskPromptBuilder(PromptTemplateService promptTemplateService)
    {
        _promptTemplateService = promptTemplateService;
    }

    public string Build(AgentTaskType taskType, string input, bool useBoschTemplate,
        string? revisionFeedback = null, string? previousOutput = null)
    {
        var prompt = _promptTemplateService.Get(TemplatePath(taskType, useBoschTemplate))
            .Replace("{{input}}", input ?? string.Empty);

        if (string.IsNullOrWhiteSpace(revisionFeedback))
            return prompt;

        return prompt + _promptTemplateService.Get("Workflow/revision.v1.md")
            .Replace("{{previous_output}}", string.IsNullOrWhiteSpace(previousOutput) ? "(không có bàn giao lần trước — đọc trực tiếp sản phẩm trong workspace)" : previousOutput)
            .Replace("{{feedback}}", revisionFeedback);
    }

    private static string TemplatePath(AgentTaskType taskType, bool useBoschTemplate) => taskType switch
    {
        AgentTaskType.PocPreview         => "Workflow/poc-preview.v1.md",
        AgentTaskType.ArchitectureDesign => useBoschTemplate ? "Workflow/architecture-design-bosch.v1.md" : "Workflow/architecture-design.v1.md",
        AgentTaskType.Implementation     => useBoschTemplate ? "Workflow/implementation-bosch.v1.md" : "Workflow/implementation.v1.md",
        AgentTaskType.CodeReview         => "Workflow/code-review.v1.md",
        AgentTaskType.Testing            => "Workflow/testing.v1.md",
        AgentTaskType.BugFix             => "Workflow/bugfix.v1.md",
        AgentTaskType.PullRequest        => "Workflow/pull-request.v1.md",
        _ => throw new InvalidOperationException($"Không có prompt template cho task type '{taskType}'.")
    };
}

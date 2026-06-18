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
/// </summary>
public class WorkflowTaskPromptBuilder
{
    private readonly PromptTemplateService _promptTemplateService;

    public WorkflowTaskPromptBuilder(PromptTemplateService promptTemplateService)
    {
        _promptTemplateService = promptTemplateService;
    }

    public string Build(AgentTaskType taskType, string input)
    {
        return _promptTemplateService.Get(TemplatePath(taskType))
            .Replace("{{input}}", input ?? string.Empty);
    }

    private static string TemplatePath(AgentTaskType taskType) => taskType switch
    {
        AgentTaskType.PocPreview         => "Workflow/poc-preview.v1.md",
        AgentTaskType.ArchitectureDesign => "Workflow/architecture-design.v1.md",
        AgentTaskType.Implementation     => "Workflow/implementation.v1.md",
        AgentTaskType.Testing            => "Workflow/testing.v1.md",
        _ => throw new InvalidOperationException($"Không có prompt template cho task type '{taskType}'.")
    };
}

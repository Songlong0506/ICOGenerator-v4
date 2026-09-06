using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;

namespace ICOGenerator.Services.Agents;

public class AgentPromptBuilder
{
    private readonly PromptTemplateService _promptTemplateService;
    private readonly AgentInstructionProvider _instructionProvider;

    public AgentPromptBuilder(PromptTemplateService promptTemplateService, AgentInstructionProvider instructionProvider)
    {
        _promptTemplateService = promptTemplateService;
        _instructionProvider = instructionProvider;
    }

    /// <summary>
    /// System prompt for the native function-calling path: it carries no JSON-action contract and no tool
    /// list/schema in the text — the tools (and their schemas) are advertised to the model through the
    /// API's "tools" parameter instead.
    /// </summary>
    /// <param name="learnedChecklist">
    /// Các bài học vai này đã rút từ nhận xét của người duyệt ở những dự án TRƯỚC (đã render sẵn thành
    /// danh sách gạch đầu dòng — xem <see cref="Requirements.ChecklistNoteStore.BuildForChatAsync"/>),
    /// hoặc null khi vai chưa học được gì. Đứng ngay sau instruction vì nó BỔ SUNG chứ không thay thế:
    /// instruction là cách làm việc cố định, đây là những chỗ chính vai này từng bị góp ý.
    /// </param>
    public string BuildNative(Agent agent, string? learnedChecklist = null)
    {
        // Chỉ MỘT placeholder cho vai: Agent không có cột Name riêng, nên trước đây {{agentName}} và
        // {{roleTitle}} cùng nhận RoleKey.GetTitle() và prompt render ra "You are Developer - Developer.".
        return _promptTemplateService.Get("Shared/tool-agent-native.v1.md")
            .Replace("{{roleTitle}}", agent.RoleKey.GetTitle())
            .Replace("{{instruction}}", _instructionProvider.GetInstruction(agent))
            .Replace("{{learnedChecklist}}", RenderChecklist(learnedChecklist));
    }

    // Chưa học được gì ⇒ chuỗi RỖNG, không phải một tiêu đề trống: một khối "Bài học" không có mục nào chỉ
    // dạy model rằng phần đó vô nghĩa. Tiêu đề nói rõ nguồn gốc để model hiểu vì sao phải tuân thủ.
    private static string RenderChecklist(string? learnedChecklist) =>
        string.IsNullOrWhiteSpace(learnedChecklist)
            ? string.Empty
            : "\nLessons this role learned from reviewer feedback on previous projects — a reviewer already\n"
              + "sent work back for each of these, so follow them from the start:\n"
              + learnedChecklist;
}

using ICOGenerator.Domain;

namespace ICOGenerator.Application.Agents;

public record AgentManagementPage(
    IReadOnlyList<Agent> Agents,
    Agent? SelectedAgent,
    IReadOnlyList<AiModel> Models,
    IReadOnlyList<ToolDefinition> Tools,
    IReadOnlyList<AgentPromptItem> Prompts,
    bool SharedSelected);

/// <summary>
/// Một template prompt (.md dưới /Prompts) thuộc thư mục role của agent — thay cho ô Instruction cũ.
/// instruction.md chỉ là một prompt trong danh sách này (IsInstruction=true). Link sang Prompt Studio
/// (Detail) để xem/sửa theo phiên bản.
/// </summary>
public record AgentPromptItem(
    string PromptKey,
    string DisplayName,
    bool IsInstruction,
    bool FileExists,
    int VersionCount,
    int? ActiveVersionNumber,
    DateTime? LastChangedAt,
    string? LastChangedBy);

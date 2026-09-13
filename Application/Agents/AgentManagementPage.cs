using ICOGenerator.Domain;
using ICOGenerator.Services.Tools.Registry;

namespace ICOGenerator.Application.Agents;

public record AgentManagementPage(
    IReadOnlyList<Agent> Agents,
    Agent? SelectedAgent,
    IReadOnlyList<AiModel> Models,
    IReadOnlyList<ToolDefinition> Tools,
    IReadOnlyList<AgentPromptItem> Prompts,
    bool SharedSelected,
    // Nhóm tool cấp-cả-gói: màn hình gộp cả nhóm thành MỘT dòng thay vì liệt kê từng tool, vì tích lẻ
    // trong nhóm không phải một lựa chọn thật (xem ToolGroupAllOrNothingAttribute).
    IReadOnlyList<LockedToolGroup> LockedToolGroups);

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

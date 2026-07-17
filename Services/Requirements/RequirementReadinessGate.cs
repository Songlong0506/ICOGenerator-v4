using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Cổng readiness DUY NHẤT quyết định "đã đủ thông tin cốt lõi để soạn tài liệu chưa", dùng chung cho cả
/// lượt chat (<see cref="BAChatService"/> — kiểm ngay khi BA định mời bấm "Write Requirement") lẫn bước
/// sinh tài liệu (<see cref="ProductBriefDraftService"/>). Hai nơi PHẢI đi qua cùng một cổng, một tiêu
/// chuẩn — tách thành hai bản là tái diễn cảnh hai "giám khảo" vênh nhau: BA mời bấm nút, người dùng bấm
/// thì bị chặn "cần bổ sung thông tin".
/// </summary>
public class RequirementReadinessGate
{
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _promptTemplateService;
    private readonly RequirementReadinessParser _readinessParser;

    public RequirementReadinessGate(
        ILlmClient llm,
        PromptTemplateService promptTemplateService,
        RequirementReadinessParser readinessParser)
    {
        _llm = llm;
        _promptTemplateService = promptTemplateService;
        _readinessParser = readinessParser;
    }

    // Gọi một LLM nhẹ để quyết định đã đủ thông tin cốt lõi soạn tài liệu chưa. Fail-open: gate lỗi thì
    // cứ cho qua để không chặn cứng việc sinh tài liệu.
    public async Task<RequirementReadiness> CheckAsync(Guid projectId, Agent ba, AiModel model, string requirementBrief, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _promptTemplateService.Get("BusinessAnalyst/requirement-readiness.v3.md")),
            new(ChatRole.User, requirementBrief)
        };

        var (callResult, structuredReadiness) = await _llm.ChatStructuredAsync<RequirementReadiness>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAReadinessCheck"), cancellationToken: cancellationToken);

        if (!callResult.IsSuccess)
            return RequirementReadiness.ProceedDefault;

        return structuredReadiness ?? _readinessParser.Parse(callResult.Content);
    }

    // Lượt BA "mời bấm Write Requirement" — cùng tín hiệu mà UI dùng để làm nổi nút (Index.cshtml đọc
    // Contains tương tự trên lượt BA mới nhất) và BuildAssistantContext dùng để echo cờ ready. Từ khi có
    // cổng readiness chạy ngay trong lượt chat, một lời mời được LƯU đồng nghĩa gate đã pass trên đúng
    // transcript tại thời điểm đó.
    public static bool IsWriteRequirementInvite(string? message) =>
        message?.Contains("Write Requirement", StringComparison.OrdinalIgnoreCase) ?? false;

    // Lượt CÓ NỘI DUNG mới nhất của hội thoại là lời mời bấm "Write Requirement" của BA ⇒ gate readiness
    // đã pass ở bước chat và chưa có thông tin nào mới kể từ đó (người dùng gõ thêm thì lượt chat luôn
    // lưu một lượt BA mới đè lên vị trí cuối). Lượt lỗi LLM không bao giờ chứa lời mời nên không cần lọc
    // riêng. Thứ tự CreatedAt rồi Id — như ConversationTranscriptBuilder — vì CreatedAt có thể trùng.
    public static bool IsVerifiedInviteLatestTurn(IEnumerable<AgentConversation> conversations)
    {
        var lastTurn = conversations
            .Where(c => !string.IsNullOrWhiteSpace(c.Message))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .LastOrDefault();
        return lastTurn?.Role == "assistant" && IsWriteRequirementInvite(lastTurn.Message);
    }

    // Bản đồ bao phủ (nếu đã có từ các lượt chat) đính vào lời gọi readiness để gate đối chiếu các nhóm
    // ★ theo trạng thái đã ghi nhận thay vì suy lại toàn bộ từ transcript.
    public static string BuildCoverageNote(Project project)
    {
        if (string.IsNullOrWhiteSpace(project.RequirementCoverageMap))
            return string.Empty;

        return "\n## Bản đồ bao phủ yêu cầu (trạng thái khai thác từng nhóm thông tin)\n"
            + project.RequirementCoverageMap;
    }

    // Tóm tắt (text) tài liệu nguồn để đưa vào lời gọi readiness — vốn là call text-only nên KHÔNG kèm ảnh được;
    // bù lại nêu tên file + trích text (bóc từ PDF) có giới hạn, để gate đừng hỏi lại thứ đã có trong tài liệu.
    public static string BuildSourceBriefNote(List<ProjectSourceFile> sources)
    {
        if (sources.Count == 0)
            return string.Empty;

        const int maxCharsPerFile = 4000;
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"(Người dùng đã đính kèm {sources.Count} tài liệu nguồn: {string.Join(", ", sources.Select(s => s.FileName))}.)");
        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s.ExtractedText))
                continue;
            var text = s.ExtractedText!.Length > maxCharsPerFile
                ? s.ExtractedText[..maxCharsPerFile] + "…(đã cắt bớt)"
                : s.ExtractedText;
            sb.AppendLine($"[Nội dung trích từ {s.FileName}]");
            sb.AppendLine(text);
        }
        return sb.ToString();
    }
}

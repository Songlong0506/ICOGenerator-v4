using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// "Bản đồ bao phủ yêu cầu" của MỘT dự án — trạng thái sống của cuộc phỏng vấn. Khác các tầng bộ nhớ
/// (<see cref="ConversationMemoryService"/> nhớ ngữ cảnh, <see cref="UserMemoryService"/> nhớ người dùng,
/// <see cref="ChecklistGapMemoryService"/> rút kinh nghiệm bộ câu hỏi), service này duy trì một bảng
/// trạng thái theo 13 nhóm thông tin cố định (khớp checklist trong <c>Prompts/BA/requirement-chat.v2.md</c>):
/// nhóm nào đã [RÕ], nhóm nào [MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG] — lưu trên
/// <see cref="Project.RequirementCoverageMap"/>. BA đọc bản đồ để chọn câu hỏi kế tiếp thay vì phỏng vấn
/// tuyến tính, còn cổng readiness đối chiếu các dòng ★ thay vì đoán lại từ đầu.
/// <para>
/// Khác hai bộ nhớ kia, việc cập nhật KHÔNG gom theo lô: bản đồ phải tươi ở từng lượt mới dẫn được câu
/// hỏi kế tiếp, nên mỗi lượt chat gộp ngay các lượt mới (thường chỉ 1–2 lượt → lời gọi rất nhẹ). Vẫn
/// <b>fail-open</b>: lời gọi lỗi thì giữ bản đồ cũ + không dời con trỏ, lượt sau gộp bù.
/// </para>
/// </summary>
public class RequirementCoverageService
{
    // Chặn trên độ dài bản đồ để không tự phình vô hạn (13 dòng gọn là đủ; model trả dài hơn thì cắt).
    private const int MaxCoverageChars = 4000;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public RequirementCoverageService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>
    /// Gộp các lượt chat mới (kể từ con trỏ) vào bản đồ rồi trả về bản đồ hiện hành để caller nạp vào
    /// prompt. <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK — bản đồ + con trỏ được ghi
    /// thẳng lên nó và lưu trong này. Fail-open: lời gọi LLM lỗi thì GIỮ bản đồ cũ và KHÔNG dời con trỏ.
    /// </summary>
    public async Task<string?> UpdateAndLoadAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        var harvested = project.CoverageHarvestedTurnCount;

        // Thứ tự ổn định (CreatedAt rồi Id) để con trỏ khớp đúng các lượt đã gộp, như các bộ nhớ khác.
        var delta = await _db.AgentConversations
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip(harvested)
            .ToListAsync(cancellationToken);

        if (delta.Count == 0)
            return project.RequirementCoverageMap;

        var updated = await DistillAsync(project.RequirementCoverageMap, delta, ba, model, project.Id, cancellationToken);
        if (updated != null)
        {
            project.RequirementCoverageMap = string.IsNullOrWhiteSpace(updated) ? null : updated;
            project.CoverageHarvestedTurnCount = harvested + delta.Count;
            await _db.SaveChangesAsync(cancellationToken);
        }
        // updated == null ⇒ gộp lỗi: fail-open, giữ bản đồ cũ + con trỏ cũ, nạp lại như dưới.

        return project.RequirementCoverageMap;
    }

    // Gộp bản đồ hiện có + các lượt mới thành MỘT bản đồ duy nhất. Trả về null khi lời gọi lỗi/rỗng để
    // caller fail-open (giữ bản đồ cũ, không dời con trỏ).
    private async Task<string?> DistillAsync(string? existingMap, List<AgentConversation> turns, Agent ba, AiModel model, Guid projectId, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingMap))
        {
            sb.AppendLine("## Bản đồ hiện có (gộp/cập nhật cùng các lượt mới bên dưới)");
            sb.AppendLine(existingMap.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("## Các lượt hội thoại mới cần gộp vào bản đồ");
        foreach (var t in turns)
        {
            var who = t.Role == "assistant" ? "BA" : "Người dùng";
            sb.AppendLine($"- {who}: {(t.Message ?? string.Empty).Trim()}");
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BA/requirement-coverage.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var result = await _llm.ChatWithLogAsync(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BARequirementCoverage"),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Content))
            return null;

        var map = result.Content.Trim();
        return map.Length > MaxCoverageChars ? map[..MaxCoverageChars] : map;
    }
}

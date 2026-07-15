using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Bộ nhớ CẤP TOÀN HỆ THỐNG cho BA — khác <see cref="UserMemoryService"/> (gắn theo TỪNG người dùng) và
/// <see cref="ConversationMemoryService"/> (gắn theo TỪNG dự án), service này rút kinh nghiệm về CHÍNH BỘ
/// CÂU HỎI của BA: khi người dùng phải tự gõ ra một thông tin yêu cầu mà BA chưa từng hỏi tới, đó là dấu
/// hiệu checklist câu hỏi (<c>Prompts/BusinessAnalyst/requirement-chat.v3.md</c>) còn thiếu. Sau khi một dự án hoàn tất
/// sinh tài liệu, service phân tích lại toàn bộ hội thoại MỘT LẦN, khái quát hoá các khoảng trống thành
/// mục checklist mới, rồi gộp vào <see cref="Agent.LearnedChecklistNotes"/> — hồ sơ dùng chung cho MỌI dự
/// án MỚI sau này, của BẤT KỲ người dùng nào (chứ không riêng người tạo ra dự án vừa phân tích).
/// <para>
/// Chỉ chạy một lần cho mỗi dự án (đánh dấu bằng <see cref="Project.ChecklistGapHarvested"/>), ngay sau khi
/// tài liệu được sinh thành công — lúc đó mới có bức tranh Q&amp;A đầy đủ để đánh giá khoảng trống. Fail-open:
/// lời gọi LLM lỗi thì giữ nguyên checklist cũ và KHÔNG đánh dấu đã harvest, để lần sinh/tái sinh tài liệu
/// sau thử lại.
/// </para>
/// </summary>
public class ChecklistGapMemoryService
{
    // Chặn trên độ dài checklist bổ sung để không tự phình vô hạn qua nhiều dự án.
    private const int MaxNotesChars = 4000;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public ChecklistGapMemoryService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>
    /// Phân tích hội thoại của một dự án VỪA sinh tài liệu thành công để rút khoảng trống checklist, gộp vào
    /// hồ sơ chung của Agent BA. Bỏ qua nếu dự án đã harvest rồi hoặc chưa có hội thoại nào.
    /// <paramref name="project"/> và <paramref name="ba"/> phải là entity ĐANG ĐƯỢC TRACK — cột kết quả được
    /// ghi thẳng lên chúng rồi lưu trong này.
    /// </summary>
    public async Task HarvestAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        if (project.ChecklistGapHarvested)
            return;

        var turns = project.Conversations.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList();
        if (turns.Count == 0)
            return;

        var updated = await DistillAsync(ba.LearnedChecklistNotes, turns, ba, model, project.Id, cancellationToken);
        if (updated == null)
            return; // fail-open: chắt lọc lỗi, giữ checklist cũ + không đánh dấu, lần sau thử lại.

        ba.LearnedChecklistNotes = string.IsNullOrWhiteSpace(updated) ? null : updated;
        project.ChecklistGapHarvested = true;
        await _db.SaveChangesAsync(cancellationToken);
    }

    // Gộp existingNotes + toàn bộ hội thoại một dự án thành MỘT checklist bổ sung duy nhất. Trả về null khi
    // lời gọi lỗi để caller fail-open (giữ checklist cũ, không đánh dấu đã harvest).
    private async Task<string?> DistillAsync(string? existingNotes, List<AgentConversation> turns, Agent ba, AiModel model, Guid projectId, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingNotes))
        {
            sb.AppendLine("## Checklist bổ sung hiện có (gộp/cập nhật cùng phát hiện mới bên dưới)");
            sb.AppendLine(existingNotes.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("## Toàn bộ hội thoại của một dự án VỪA hoàn tất (đã sinh tài liệu) để rà soát khoảng trống");
        foreach (var t in turns)
        {
            var who = t.Role == "assistant" ? "BA" : "Người dùng";
            sb.AppendLine($"- {who}: {(t.Message ?? string.Empty).Trim()}");
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/checklist-gap.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var result = await _llm.ChatWithLogAsync(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAChecklistGap"),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess || result.Content == null)
            return null;

        var notes = result.Content.Trim();
        return notes.Length > MaxNotesChars ? notes[..MaxNotesChars] : notes;
    }
}

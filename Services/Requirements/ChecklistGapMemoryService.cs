using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Bộ nhớ CẤP TOÀN HỆ THỐNG cho BA — khác <see cref="UserMemoryService"/> (gắn theo TỪNG người dùng) và
/// <see cref="ConversationMemoryService"/> (gắn theo TỪNG dự án), service này rút kinh nghiệm về CHÍNH BỘ
/// CÂU HỎI của BA: khi người dùng phải tự gõ ra một thông tin yêu cầu mà BA chưa từng hỏi tới, đó là dấu
/// hiệu checklist câu hỏi (<c>Prompts/BusinessAnalyst/requirement-chat.v4.md</c>) còn thiếu. Sau khi một dự án hoàn tất
/// sinh tài liệu, service phân tích lại toàn bộ hội thoại MỘT LẦN, khái quát hoá các khoảng trống thành
/// bài học mới rồi THÊM vào bucket checklist của BA (<see cref="ChecklistNoteStore"/>) — hồ sơ dùng chung
/// cho MỌI dự án MỚI sau này, của BẤT KỲ người dùng nào (chứ không riêng người tạo ra dự án vừa phân tích).
/// <para>
/// Mỗi bài học được lưu kèm <b>lý do rút ra</b> và <b>trích dẫn bằng chứng</b>: vòng harvest là chỗ DUY
/// NHẤT còn nhìn thấy hội thoại gốc, nên nếu không bắt tại đây thì sau này không cách nào truy lại
/// "vì sao BA lại tự hỏi điều này" khi nó rút sai.
/// </para>
/// <para>
/// Chỉ chạy một lần cho mỗi dự án (đánh dấu bằng <see cref="Project.ChecklistGapHarvested"/>), ngay sau khi
/// tài liệu được sinh thành công — lúc đó mới có bức tranh Q&amp;A đầy đủ để đánh giá khoảng trống. Fail-open:
/// lời gọi LLM lỗi thì giữ nguyên checklist cũ và KHÔNG đánh dấu đã harvest, để lần sinh/tái sinh tài liệu
/// sau thử lại.
/// </para>
/// </summary>
public class ChecklistGapMemoryService
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly ChecklistNoteStore _noteStore;
    private readonly ILogger<ChecklistGapMemoryService> _logger;

    public ChecklistGapMemoryService(
        AppDbContext db,
        ILlmClient llm,
        PromptTemplateService prompts,
        ChecklistNoteStore noteStore,
        ILogger<ChecklistGapMemoryService> logger)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _noteStore = noteStore;
        _logger = logger;
    }

    /// <summary>
    /// Phân tích hội thoại của một dự án VỪA sinh tài liệu thành công để rút khoảng trống checklist, thêm
    /// vào hồ sơ của Agent BA — vào BUCKET phòng ban của đơn vị yêu cầu, hoặc bucket chung khi không giải
    /// được phòng ban. Bỏ qua nếu dự án đã harvest rồi hoặc chưa có hội thoại nào.
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

        var bucket = await _noteStore.ResolveBucketAsync(project.OrgUnitCode, cancellationToken);
        var existing = await _noteStore.LoadBucketAsync(ba, bucket, cancellationToken);
        var lessons = await DistillAsync(existing, turns, ba, model, project.Id, cancellationToken);
        if (lessons == null)
            return; // fail-open: chắt lọc lỗi, giữ checklist cũ + không đánh dấu, lần sau thử lại.

        _noteStore.MergeHarvest(ba, bucket, existing, lessons.Items, ChecklistItemSource.Conversation, project.Id);
        project.ChecklistGapHarvested = true;
        await _db.SaveChangesAsync(cancellationToken);
    }

    // Rút các bài học MỚI từ hội thoại một dự án. Trả về null khi lời gọi lỗi để caller fail-open (giữ
    // checklist cũ, không đánh dấu đã harvest); danh sách RỖNG nghĩa là "không có gì mới" — vẫn là thành công.
    private async Task<ChecklistLessonSet?> DistillAsync(
        List<AgentChecklistItem> existing,
        List<AgentConversation> turns,
        Agent ba,
        AiModel model,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var context = ChecklistNoteStore.RenderContextForHarvest(existing);
        if (context.Length > 0)
        {
            sb.AppendLine(context);
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
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/checklist-gap.v2.md")),
            new(ChatRole.User, sb.ToString())
        };

        var (result, structured) = await _llm.ChatStructuredAsync<ChecklistLessonSet>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAChecklistGap"),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess)
            return null;

        var lessons = structured ?? LlmJson.TryDeserialize<ChecklistLessonSet>(result.Content, requireKnownProperty: true);
        if (lessons != null)
            return lessons;

        // Gọi được nhưng phản hồi không đọc nổi: coi như "không có gì mới" và VẪN đánh dấu đã harvest —
        // dự án này đã tiêu một lời gọi, thử lại mỗi lần tái sinh tài liệu chỉ tốn thêm mà không khá hơn.
        _logger.LogWarning("Checklist gap harvest for project {ProjectId} returned unparseable output.", projectId);
        return new ChecklistLessonSet();
    }
}

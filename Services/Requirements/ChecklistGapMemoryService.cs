using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Bộ nhớ CẤP TOÀN HỆ THỐNG cho BA — khác <see cref="UserMemoryService"/> (gắn theo TỪNG người dùng) và
/// <see cref="ConversationMemoryService"/> (gắn theo TỪNG dự án), service này rút kinh nghiệm về CHÍNH BỘ
/// CÂU HỎI của BA: chỗ nào bộ câu hỏi (<c>Prompts/BusinessAnalyst/requirement-chat.v4.md</c>) còn thiếu
/// thì bài học được THÊM vào bucket checklist của BA (<see cref="ChecklistNoteStore"/>) — hồ sơ dùng chung
/// cho MỌI dự án MỚI sau này, của BẤT KỲ người dùng nào (chứ không riêng người tạo ra dự án vừa phân tích).
///
/// <para>
/// <b>Chạy ở mốc người dùng DUYỆT Product Brief</b>, không phải ngay sau khi bản nháp được sinh ra. Lúc
/// vừa sinh xong thì người dùng còn chưa đọc, nên bằng chứng duy nhất là suy đoán gián tiếp "chỗ nào họ
/// tự nêu mà BA chưa hỏi". Đến mốc duyệt thì các GHI CHÚ họ ghim lên bản nháp đã có mặt — mỗi ghi chú là
/// một chỗ BA viết thiếu hoặc hiểu sai, bằng chứng trực tiếp và sắc hơn hẳn. Hàng đợi là
/// <see cref="Project.PendingChecklistHarvestVersion"/> (<c>ApproveRequirementUseCase</c> ghi tên bản vừa
/// duyệt vào đó); vòng harvest chạy nền trong <see cref="RequirementMemoryHarvester"/> để màn hình Approve
/// không phải chờ một lời gọi LLM.
/// </para>
///
/// <para>
/// Hai nhánh, tách bằng việc bản vừa duyệt CÓ ghi chú hay không:
/// <list type="bullet">
///   <item><b>Có ghi chú</b> ⇒ harvest "sắc": ghi chú + hội thoại cùng vào prompt, bài học ghi nguồn
///         <see cref="ChecklistItemSource.BriefNote"/>. Chạy lại ở MỖI bản duyệt có ghi chú (V1, V2…) —
///         mỗi bản là một tập bằng chứng mới.</item>
///   <item><b>Không ghi chú</b> ⇒ <b>lưới đỡ</b>: vẫn rà hội thoại như trước (nguồn
///         <see cref="ChecklistItemSource.Conversation"/>), vì Brief đúng ngay có thể chỉ vì người dùng đã
///         tự khai đủ những gì BA quên hỏi — bộ câu hỏi vẫn thiếu, chỉ là không ai phàn nàn. Bằng chứng
///         gián tiếp thì chỉ đáng MỘT lời gọi cho cả đời dự án: gác bằng
///         <see cref="Project.ChecklistGapHarvested"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// Mỗi bài học được lưu kèm <b>lý do rút ra</b> và <b>trích dẫn bằng chứng</b>: vòng harvest là chỗ DUY
/// NHẤT còn nhìn thấy hội thoại và ghi chú gốc, nên nếu không bắt tại đây thì sau này không cách nào truy
/// lại "vì sao BA lại tự hỏi điều này" khi nó rút sai. <b>Fail-open</b> như các bộ nhớ khác: lời gọi lỗi
/// thì giữ nguyên checklist cũ + hàng đợi đứng yên, task sau gộp bù.
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
    /// Chắt lọc bản Product Brief vừa được duyệt (ghi chú trên bản đó + hội thoại đã dẫn tới nó) thành bài
    /// học cho bộ câu hỏi của BA — vào BUCKET phòng ban của đơn vị yêu cầu, hoặc bucket chung khi không
    /// giải được phòng ban. Không có gì trong hàng đợi ⇒ no-op. Mọi lỗi đều nuốt + log: đây là bước phụ
    /// trợ chạy nền, không được làm fail task đang chạy.
    /// </summary>
    public async Task TryHarvestAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
            if (project == null || string.IsNullOrWhiteSpace(project.PendingChecklistHarvestVersion))
                return;

            var ba = await _db.Agents
                .Include(a => a.AiModel)
                .FirstOrDefaultAsync(a => a.RoleKey == AgentRoleKey.BusinessAnalyst && a.AiModel != null, cancellationToken);
            if (ba == null)
                return;

            // Ghi chú của ĐÚNG bản vừa duyệt: ApproveRequirementUseCase nâng chúng từ "draft" lên V{n}
            // cùng lúc với file, nên lọc theo version là lọc đúng tập bằng chứng của bản đó. Ghi chú đã
            // thu hồi không tính — người dùng đã tự rút lại lời chê.
            var notes = await _db.PocComments.AsNoTracking()
                .Where(c => c.ProjectId == projectId
                            && c.Target == PocCommentTarget.Brief
                            && c.BriefVersion == project.PendingChecklistHarvestVersion
                            && c.WithdrawnAtUtc == null)
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .ToListAsync(cancellationToken);

            // Lưới đỡ đã tiêu lời gọi duy nhất của nó ở một bản trước ⇒ bản này không có ghi chú thì
            // không còn gì mới để học: dọn hàng đợi và về, đừng trả tiền cho cùng một transcript lần nữa.
            if (notes.Count == 0 && project.ChecklistGapHarvested)
            {
                await ClearQueueAsync(project, cancellationToken);
                return;
            }

            var turns = await _db.AgentConversations.AsNoTracking()
                .Where(c => c.ProjectId == projectId)
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .ToListAsync(cancellationToken);

            if (turns.Count == 0 && notes.Count == 0)
            {
                await ClearQueueAsync(project, cancellationToken);
                return;
            }

            var bucket = await _noteStore.ResolveBucketAsync(project.OrgUnitCode, cancellationToken);
            var existing = await _noteStore.LoadBucketAsync(ba, bucket, cancellationToken);
            var lessons = await DistillAsync(existing, turns, notes, ba, ba.AiModel!, projectId, cancellationToken);
            if (lessons == null)
                return; // fail-open: chắt lọc lỗi, giữ checklist cũ + hàng đợi, task sau thử lại.

            // Nguồn phản ánh BẰNG CHỨNG MẠNH NHẤT của vòng này: có ghi chú thì bài học truy về ghi chú,
            // không thì về hội thoại. Trang quản trị đọc cột này để diễn giải phần Evidence.
            var sourceKind = notes.Count > 0 ? ChecklistItemSource.BriefNote : ChecklistItemSource.Conversation;
            _noteStore.MergeHarvest(ba, bucket, existing, lessons.Items, sourceKind, projectId);

            if (notes.Count == 0)
                project.ChecklistGapHarvested = true;

            project.PendingChecklistHarvestVersion = null;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not harvest checklist gaps for project {ProjectId}.", projectId);
        }
    }

    private async Task ClearQueueAsync(Project project, CancellationToken cancellationToken)
    {
        project.PendingChecklistHarvestVersion = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    // Rút các bài học MỚI từ bản Brief vừa duyệt. Trả về null khi lời gọi lỗi để caller fail-open (giữ
    // checklist cũ + hàng đợi); danh sách RỖNG nghĩa là "không có gì mới" — vẫn là thành công.
    private async Task<ChecklistLessonSet?> DistillAsync(
        List<AgentChecklistItem> existing,
        List<AgentConversation> turns,
        List<PocComment> notes,
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

        if (notes.Count > 0)
        {
            sb.AppendLine("## Ghi chú người dùng ghim lên bản mô tả sản phẩm TRƯỚC KHI duyệt (bằng chứng chính)");
            foreach (var note in notes)
            {
                sb.Append("- ");
                if (!string.IsNullOrWhiteSpace(note.Quote))
                    sb.Append($"[Về đoạn: \"{Excerpt(note.Quote)}\"] ");
                sb.AppendLine(note.Comment.Trim());
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Toàn bộ hội thoại đã dẫn tới bản mô tả sản phẩm vừa được duyệt");
        foreach (var t in turns)
        {
            var who = t.Role == "assistant" ? "BA" : "Người dùng";
            sb.AppendLine($"- {who}: {(t.Message ?? string.Empty).Trim()}");
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/checklist-gap.v3.md")),
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

        // Gọi được nhưng phản hồi không đọc nổi: coi như "không có gì mới" và VẪN dọn hàng đợi — bản này
        // đã tiêu một lời gọi, thử lại ở task sau chỉ tốn thêm mà không khá hơn.
        _logger.LogWarning("Checklist gap harvest for project {ProjectId} returned unparseable output.", projectId);
        return new ChecklistLessonSet();
    }

    // Đoạn văn bị bôi đen có thể dài cả trang; prompt chỉ cần đủ để biết ghi chú nói VỀ chỗ nào.
    private static string Excerpt(string quote)
    {
        var clean = quote.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return clean.Length <= 200 ? clean : clean[..200] + "…";
    }
}

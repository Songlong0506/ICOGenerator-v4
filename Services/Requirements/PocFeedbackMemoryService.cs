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
/// Đóng vòng học từ GHI CHÚ TRÊN POC: mỗi ghi chú kiểu "thiếu màn hình X"/"tính sai Y" là bằng chứng
/// cuộc phỏng vấn yêu cầu đã bỏ sót — tín hiệu còn mạnh hơn khoảng trống hội thoại mà
/// <see cref="ChecklistGapMemoryService"/> khai thác khi không có ghi chú nào. Ở mốc người dùng DUYỆT bản
/// demo, service chắt lọc các ghi chú mới thành bài học khái quát và THÊM
/// vào bucket checklist học được của BA (theo miền nghiệp vụ của dự án — xem
/// <see cref="ChecklistNoteStore"/>) — BA sẽ hỏi tới điểm đó ngay từ phỏng vấn ở các dự án cùng miền sau,
/// lỗi không lặp lại ở POC. Mỗi bài học lưu kèm lý do rút ra + trích dẫn ghi chú gốc, vì đây là chỗ cuối
/// cùng còn nhìn thấy ghi chú đó.
/// <para>
/// <b>Chạy ở mốc người dùng DUYỆT bản demo</b> chứ không phải sau mỗi vòng chỉnh sửa: cờ
/// <see cref="Project.PendingPocFeedbackHarvest"/> do <c>ApproveStageUseCase</c> bật, vòng harvest chạy nền
/// trong <see cref="RequirementMemoryHarvester"/>. Vì sao đợi tới lúc duyệt: harvest theo từng vòng phải
/// trả một lời gọi LLM cho MỖI vòng sửa và học từ một bản vá chưa ai xác nhận là đạt; đợi tới cổng duyệt
/// thì mọi vòng gom vào ĐÚNG MỘT lời gọi, và lúc đó ghi chú nào chưa đạt đã được người review mở lại nên
/// bức tranh mới đầy đủ. Không có ghi chú nào ⇒ không có gì để học, no-op.
/// </para>
/// <para>
/// Con trỏ <see cref="Project.PocFeedbackHarvestedCount"/> (số ghi chú POC đã cân nhắc, xếp theo CreatedAt)
/// cho phép harvest nhiều lần duyệt mà không gộp lặp; <b>fail-open</b> như các bộ nhớ khác: lời gọi lỗi thì
/// giữ checklist cũ + con trỏ và cờ đứng yên, task sau gộp bù.
/// </para>
/// </summary>
public class PocFeedbackMemoryService
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly ChecklistNoteStore _noteStore;
    private readonly ILogger<PocFeedbackMemoryService> _logger;

    public PocFeedbackMemoryService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts, ChecklistNoteStore noteStore, ILogger<PocFeedbackMemoryService> logger)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _noteStore = noteStore;
        _logger = logger;
    }

    /// <summary>
    /// Chắt lọc các ghi chú POC MỚI (kể từ con trỏ) của project vào checklist học được của BA. Không có gì
    /// trong hàng đợi ⇒ no-op. Mọi lỗi đều nuốt + log — đây là bước phụ trợ chạy nền, không được làm fail
    /// task đang chạy.
    /// </summary>
    public async Task TryHarvestAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
            if (project == null || !project.PendingPocFeedbackHarvest)
                return;

            var ba = await _db.Agents
                .Include(a => a.AiModel)
                .FirstOrDefaultAsync(a => a.RoleKey == Domain.Enums.AgentRoleKey.BusinessAnalyst && a.AiModel != null, cancellationToken);
            if (ba == null)
                return;

            // Con trỏ trượt trên TOÀN BỘ ghi chú POC của dự án, KHÔNG lọc trạng thái. Lọc trước rồi mới
            // Skip là sai: trạng thái ghi chú co giãn hai chiều (Sent → Addressed khi vòng sửa xong,
            // Addressed → Open khi người review mở lại), nên tập bị lọc có thể NHỎ ĐI giữa hai lần harvest
            // và con trỏ đếm theo nó sẽ nhảy qua mất các ghi chú mới. Ghi chú không bao giờ bị xoá cứng,
            // nên tập KHÔNG lọc chỉ có lớn lên — đó là thứ duy nhất Skip đếm đúng được.
            // Lọc Target vì ghi chú Brief đi đường riêng (ChecklistGapMemoryService, ở mốc duyệt Brief).
            var delta = await _db.PocComments
                .AsNoTracking()
                .Where(c => c.ProjectId == projectId && c.Target == PocCommentTarget.Poc)
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .Skip(project.PocFeedbackHarvestedCount)
                .ToListAsync(cancellationToken);

            // Ghi chú đã thu hồi không phải bằng chứng — chính người ghim đã rút lại lời chê. Mọi trạng
            // thái còn lại đều tính: một ghi chú còn Open lúc người dùng bấm duyệt vẫn là điều bản demo
            // làm họ phải gõ ra, tức vẫn là câu hỏi buổi phỏng vấn lẽ ra phải hỏi.
            var evidence = delta.Where(c => c.WithdrawnAtUtc == null).ToList();

            // Duyệt thẳng bản demo, không ghi chú nào ⇒ không có bằng chứng nào để học: dời con trỏ, hạ cờ
            // và về, đừng trả tiền cho một lời gọi LLM chỉ để nghe "không có gì".
            if (evidence.Count == 0)
            {
                project.PocFeedbackHarvestedCount += delta.Count;
                project.PendingPocFeedbackHarvest = false;
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }

            // Bài học vào BUCKET phòng ban của đơn vị yêu cầu (bucket chung khi không giải được phòng
            // ban) — ghi chú POC của phòng kho không gây nhiễu phỏng vấn của phòng nhân sự. Xem
            // ChecklistNoteStore.
            var bucket = await _noteStore.ResolveBucketAsync(project.OrgUnitCode, cancellationToken);
            var existing = await _noteStore.LoadBucketAsync(ba, bucket, cancellationToken);
            var lessons = await DistillAsync(existing, evidence, ba, ba.AiModel!, projectId, cancellationToken);
            if (lessons == null)
                return; // fail-open: giữ checklist cũ + con trỏ và cờ đứng yên, task sau gộp bù.

            _noteStore.MergeHarvest(ba, bucket, existing, lessons.Items, ChecklistItemSource.PocFeedback, projectId);
            project.PocFeedbackHarvestedCount += delta.Count;
            project.PendingPocFeedbackHarvest = false;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not harvest POC feedback for project {ProjectId}.", projectId);
        }
    }

    // Rút bài học MỚI từ các ghi chú POC. Trả null khi lời gọi lỗi để caller fail-open (giữ checklist cũ,
    // con trỏ đứng yên); danh sách RỖNG nghĩa là "không rút được gì" — vẫn là thành công, con trỏ tiến.
    private async Task<ChecklistLessonSet?> DistillAsync(
        List<AgentChecklistItem> existing,
        List<PocComment> comments,
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
        sb.AppendLine("## Ghi chú người dùng ghim trên bản demo của một dự án (gom tới lúc họ bấm duyệt bản demo)");
        foreach (var c in comments)
        {
            sb.Append("- ");
            if (!string.IsNullOrWhiteSpace(c.PageView))
                sb.Append($"[Màn hình \"{c.PageView}\"] ");
            if (!string.IsNullOrWhiteSpace(c.ElementLabel))
                sb.Append($"Phần tử: {c.ElementLabel} — ");
            sb.AppendLine(c.Comment.Trim());
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/poc-feedback-gap.v2.md")),
            new(ChatRole.User, sb.ToString())
        };

        var (result, structured) = await _llm.ChatStructuredAsync<ChecklistLessonSet>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAPocFeedbackGap"),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess)
            return null;

        var lessons = structured ?? LlmJson.TryDeserialize<ChecklistLessonSet>(result.Content, requireKnownProperty: true);
        if (lessons != null)
            return lessons;

        // Gọi được nhưng phản hồi không đọc nổi: coi như không rút được gì và VẪN dời con trỏ — các ghi
        // chú này đã tiêu một lời gọi, gộp lại ở vòng sau chỉ tốn thêm mà không khá hơn.
        _logger.LogWarning("POC feedback harvest for project {ProjectId} returned unparseable output.", projectId);
        return new ChecklistLessonSet();
    }
}

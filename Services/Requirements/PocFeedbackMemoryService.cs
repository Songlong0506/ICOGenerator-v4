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
/// <see cref="ChecklistGapMemoryService"/> khai thác. Sau MỖI vòng chỉnh sửa POC hoàn tất (lúc đó ghi
/// chú đã thật sự dẫn tới một lần sửa), service chắt lọc các ghi chú mới thành bài học khái quát và THÊM
/// vào bucket checklist học được của BA (theo miền nghiệp vụ của dự án — xem
/// <see cref="ChecklistNoteStore"/>) — BA sẽ hỏi tới điểm đó ngay từ phỏng vấn ở các dự án cùng miền sau,
/// lỗi không lặp lại ở POC. Mỗi bài học lưu kèm lý do rút ra + trích dẫn ghi chú gốc, vì đây là chỗ cuối
/// cùng còn nhìn thấy ghi chú đó.
/// <para>
/// Con trỏ <see cref="Project.PocFeedbackHarvestedCount"/> (số ghi chú đã chắt lọc, xếp theo CreatedAt)
/// cho phép harvest nhiều vòng mà không gộp lặp; <b>fail-open</b> như các bộ nhớ khác: lời gọi lỗi thì
/// giữ checklist cũ + con trỏ đứng yên, vòng sau gộp bù.
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
    /// Chắt lọc các ghi chú POC MỚI (kể từ con trỏ) của project vào checklist học được của BA. Mọi lỗi
    /// đều nuốt + log — đây là bước phụ trợ chạy nền sau vòng sửa POC, không được làm fail task.
    /// </summary>
    public async Task TryHarvestAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
            if (project == null)
                return;

            var ba = await _db.Agents
                .Include(a => a.AiModel)
                .FirstOrDefaultAsync(a => a.RoleKey == Domain.Enums.AgentRoleKey.BusinessAnalyst && a.AiModel != null, cancellationToken);
            if (ba == null)
                return;

            // Chỉ ghi chú đã ĐƯỢC GỬI cho Developer (Sent) — chúng đã thật sự dẫn tới một lần sửa POC.
            var delta = await _db.PocComments
                .AsNoTracking()
                .Where(c => c.ProjectId == projectId && c.Status == Domain.Enums.PocCommentStatus.Sent)
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .Skip(project.PocFeedbackHarvestedCount)
                .ToListAsync(cancellationToken);

            if (delta.Count == 0)
                return;

            // Bài học vào BUCKET phòng ban của đơn vị yêu cầu (bucket chung khi không giải được phòng
            // ban) — ghi chú POC của phòng kho không gây nhiễu phỏng vấn của phòng nhân sự. Xem
            // ChecklistNoteStore.
            var bucket = await _noteStore.ResolveBucketAsync(project.OrgUnitCode, cancellationToken);
            var existing = await _noteStore.LoadBucketAsync(ba, bucket, cancellationToken);
            var lessons = await DistillAsync(existing, delta, ba, ba.AiModel!, projectId, cancellationToken);
            if (lessons == null)
                return; // fail-open: giữ checklist cũ + con trỏ đứng yên, vòng sau gộp bù.

            _noteStore.MergeHarvest(ba, bucket, existing, lessons.Items, ChecklistItemSource.PocFeedback, projectId);
            project.PocFeedbackHarvestedCount += delta.Count;
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
        sb.AppendLine("## Ghi chú người dùng ghim trên POC của một dự án (đã được gửi cho Developer sửa)");
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

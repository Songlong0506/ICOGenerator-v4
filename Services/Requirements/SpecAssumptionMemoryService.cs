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
/// Đóng vòng học từ CÁC GIẢ ĐỊNH BỊ BÁC ở cổng xác nhận giả định — đường thứ BA đổ vào checklist học
/// được của BA, cạnh <see cref="ChecklistGapMemoryService"/> (từ khoảng trống hội thoại) và
/// <see cref="PocFeedbackMemoryService"/> (từ ghi chú trên POC).
///
/// <para>
/// Vì sao tín hiệu này đáng học nhất trong ba đường: mỗi điểm người dùng bấm "Chưa đúng" là một chỗ mà
/// Product Brief KHÔNG NÓI GÌ (nên bước thiết kế buộc phải tự quyết) và cách tự quyết đó SAI — tức đúng
/// một câu hỏi buổi phỏng vấn lẽ ra phải hỏi, kèm sẵn cách hiểu đúng do chính người dùng gõ ra. Nó cũng
/// tới SỚM hơn ghi chú POC: cổng nằm trước lượt dựng demo, nên bài học có mặt trước khi bản demo tồn tại.
/// </para>
///
/// <para>
/// Hàng đợi là <see cref="Project.PendingAssumptionGaps"/> — <c>ReviseSpecAssumptionsUseCase</c> ghi vào
/// đó đúng khối đính chính vừa gửi, service này chắt lọc rồi xoá. Không đọc thẳng
/// <see cref="Project.SpecAssumptionCorrections"/> vì cột đó tích lũy qua nhiều vòng và bị cắt vòng, nên
/// không có cách nào biết phần nào đã học. <b>Fail-open</b> như hai đường kia: lời gọi lỗi ⇒ giữ nguyên
/// hàng đợi + checklist cũ, lượt sinh lại spec sau gộp bù.
/// </para>
/// </summary>
public class SpecAssumptionMemoryService
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly ChecklistNoteStore _noteStore;
    private readonly ILogger<SpecAssumptionMemoryService> _logger;

    public SpecAssumptionMemoryService(
        AppDbContext db,
        ILlmClient llm,
        PromptTemplateService prompts,
        ChecklistNoteStore noteStore,
        ILogger<SpecAssumptionMemoryService> logger)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _noteStore = noteStore;
        _logger = logger;
    }

    /// <summary>
    /// Chắt lọc hàng đợi giả định bị bác của project thành bài học cho bộ câu hỏi của BA. Mọi lỗi đều
    /// nuốt + log — đây là bước phụ trợ chạy nền trong lượt sinh lại spec, không được làm fail task.
    /// </summary>
    public async Task TryHarvestAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
            if (project == null || string.IsNullOrWhiteSpace(project.PendingAssumptionGaps))
                return;

            var ba = await _db.Agents
                .Include(a => a.AiModel)
                .FirstOrDefaultAsync(a => a.RoleKey == AgentRoleKey.BusinessAnalyst && a.AiModel != null, cancellationToken);
            if (ba == null)
                return;

            // Bài học vào BUCKET đúng miền nghiệp vụ của dự án (bucket chung khi chưa phân loại) — giả
            // định sai của dự án JD không gây nhiễu phỏng vấn dự án nghỉ phép. Xem ChecklistNoteStore.
            var existing = await _noteStore.LoadBucketAsync(ba, project.DomainKey, cancellationToken);
            var lessons = await DistillAsync(existing, project.PendingAssumptionGaps, ba, ba.AiModel!, projectId, cancellationToken);
            if (lessons == null)
                return; // fail-open: giữ checklist cũ + hàng đợi đứng yên, lượt sau gộp bù.

            _noteStore.MergeHarvest(ba, project.DomainKey, existing, lessons.Items, ChecklistItemSource.SpecAssumption, projectId);
            project.PendingAssumptionGaps = null;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not harvest rejected spec assumptions for project {ProjectId}.", projectId);
        }
    }

    // Rút bài học MỚI từ các giả định bị bác. Trả null khi lời gọi lỗi để caller fail-open (giữ hàng đợi);
    // danh sách RỖNG nghĩa là "không rút được gì" — vẫn là thành công, hàng đợi được dọn.
    private async Task<ChecklistLessonSet?> DistillAsync(
        List<AgentChecklistItem> existing,
        string queuedGaps,
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
        sb.AppendLine("## Giả định của bản thiết kế bị người dùng đánh dấu \"chưa đúng\" (kèm ý đúng của họ, nếu có)");
        sb.AppendLine(queuedGaps.Trim());

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/spec-assumption-gap.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var (result, structured) = await _llm.ChatStructuredAsync<ChecklistLessonSet>(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BASpecAssumptionGap"),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess)
            return null;

        var lessons = structured ?? LlmJson.TryDeserialize<ChecklistLessonSet>(result.Content, requireKnownProperty: true);
        if (lessons != null)
            return lessons;

        // Gọi được nhưng phản hồi không đọc nổi: coi như không rút được gì và VẪN dọn hàng đợi — khối này
        // đã tiêu một lời gọi, giữ lại để thử tiếp ở lượt sau chỉ tốn thêm mà không khá hơn.
        _logger.LogWarning("Spec assumption harvest for project {ProjectId} returned unparseable output.", projectId);
        return new ChecklistLessonSet();
    }
}

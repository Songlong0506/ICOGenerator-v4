using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Đóng vòng học từ GHI CHÚ TRÊN POC: mỗi ghi chú kiểu "thiếu màn hình X"/"tính sai Y" là bằng chứng
/// cuộc phỏng vấn yêu cầu đã bỏ sót — tín hiệu còn mạnh hơn khoảng trống hội thoại mà
/// <see cref="ChecklistGapMemoryService"/> khai thác. Sau MỖI vòng chỉnh sửa POC hoàn tất (lúc đó ghi
/// chú đã thật sự dẫn tới một lần sửa), service chắt lọc các ghi chú mới thành mục checklist khái quát
/// và gộp vào bucket checklist học được của BA (theo miền nghiệp vụ của dự án — xem
/// <see cref="ChecklistNoteStore"/>) — BA sẽ hỏi tới điểm đó ngay từ phỏng vấn ở các dự án cùng miền sau,
/// lỗi không lặp lại ở POC.
/// <para>
/// Con trỏ <see cref="Project.PocFeedbackHarvestedCount"/> (số ghi chú đã chắt lọc, xếp theo CreatedAt)
/// cho phép harvest nhiều vòng mà không gộp lặp; <b>fail-open</b> như các bộ nhớ khác: lời gọi lỗi thì
/// giữ checklist cũ + con trỏ đứng yên, vòng sau gộp bù.
/// </para>
/// </summary>
public class PocFeedbackMemoryService
{
    // Cùng trần với ChecklistGapMemoryService — hai đường cùng ghi vào Agent.LearnedChecklistNotes.
    private const int MaxNotesChars = 4000;

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

            // Bài học vào BUCKET đúng miền nghiệp vụ của dự án (bucket chung khi chưa phân loại) —
            // ghi chú POC của dự án kho không gây nhiễu phỏng vấn dự án nghỉ phép. Xem ChecklistNoteStore.
            var existingNotes = await _noteStore.LoadBucketAsync(ba, project.DomainKey, cancellationToken);
            var updated = await DistillAsync(existingNotes, delta, ba, ba.AiModel!, projectId, cancellationToken);
            if (updated == null)
                return; // fail-open: giữ checklist cũ + con trỏ đứng yên, vòng sau gộp bù.

            await _noteStore.SetBucketAsync(ba, project.DomainKey, updated, cancellationToken);
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

    private async Task<string?> DistillAsync(string? existingNotes, List<PocComment> comments, Agent ba, AiModel model, Guid projectId, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingNotes))
        {
            sb.AppendLine("## Checklist bổ sung hiện có (gộp/cập nhật cùng bài học mới bên dưới)");
            sb.AppendLine(existingNotes.Trim());
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
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/poc-feedback-gap.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var result = await _llm.ChatWithLogAsync(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAPocFeedbackGap"),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess || result.Content == null)
            return null;

        var notes = result.Content.Trim();
        return notes.Length > MaxNotesChars ? notes[..MaxNotesChars] : notes;
    }
}

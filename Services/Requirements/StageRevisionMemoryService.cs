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
/// Đóng vòng học cho các VAI KỸ THUẬT (Technical Lead / Developer / Tester) từ NHẬN XÉT người duyệt gõ ở
/// nút "Yêu cầu chỉnh sửa" của cổng duyệt: mỗi nhận xét là bằng chứng vai đó làm chưa đúng ngay lượt đầu.
/// Bài học rút ra được nạp vào system prompt của chính vai đó ở MỌI dự án sau (<see cref="Agents.AgentRunService"/>),
/// để lần sau họ làm đúng ngay mà không cần ai góp ý lại.
///
/// <para>
/// <b>Vì sao vẫn dùng chung kho của BA</b> (<see cref="ChecklistNoteStore"/> / <see cref="AgentChecklistItem"/>):
/// bài toán y hệt — mỗi bài học một dòng có định danh, kèm lý do + bằng chứng, bật/tắt được, và mục đã tắt
/// làm DANH SÁCH CẤM để không học lại. Khóa phân biệt là <see cref="AgentChecklistItem.AgentId"/>, nên
/// checklist của Developer không bao giờ lẫn vào prompt của Technical Lead.
/// </para>
///
/// <para>
/// <b>Bucket luôn là bucket CHUNG</b> (<c>DepartmentCode = null</c>), khác hẳn BA. BA gom theo phòng ban vì
/// câu hỏi phỏng vấn phụ thuộc nghiệp vụ — bài học của phòng kho làm nhiễu phỏng vấn phòng nhân sự. Bài học
/// kỹ thuật thì ngược lại: "mọi lời gọi ra ngoài phải có xử lý lỗi" đúng ở mọi phòng ban, và chẻ nó ra 15
/// bucket nghĩa là mỗi bucket phải tự học lại từ đầu, mỗi bucket vài mục — dưới xa ngưỡng
/// <see cref="ChecklistNoteStore.MaxActiveItemsPerBucket"/> mà bộ nhớ này cần để có ích.
/// </para>
///
/// <para>
/// <b>Chạy ở mốc người duyệt bấm DUYỆT bước đó</b> chứ không phải lúc họ gõ nhận xét: cờ
/// <see cref="AgentTask.PendingLessonHarvest"/> do <c>ApproveStageUseCase</c> bật, vòng harvest chạy nền
/// trong <see cref="RequirementMemoryHarvester"/>. Nhận xét gõ xong đã được đưa thẳng vào task chỉnh sửa
/// nên nó đã phục vụ dự án NÀY rồi; thứ còn thiếu là bài học cho dự án SAU, và chỉ tới lúc người duyệt bấm
/// duyệt mới biết nhận xét đó dẫn tới kết quả họ chấp nhận. Bước bị bỏ dở hoặc bị từ chối không bao giờ
/// được bật cờ. <b>Fail-open</b> như các bộ nhớ khác: lời gọi lỗi ⇒ giữ checklist cũ, cờ đứng yên, task
/// sau gộp bù.
/// </para>
/// </summary>
public class StageRevisionMemoryService
{
    /// <summary>
    /// Trần số nhận xét gửi cho MỘT lời gọi. Nhận xét ở cổng POC có thể kèm toàn bộ ghi chú ghim trên bản
    /// demo (xem <c>RequestStageRevisionUseCase.AppendPocComments</c>) nên dài bất thường; cắt để một dự án
    /// lắm vòng sửa không đẩy prompt harvest vượt cửa sổ ngữ cảnh.
    /// </summary>
    private const int MaxFeedbackChars = 6000;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly ChecklistNoteStore _noteStore;
    private readonly ILogger<StageRevisionMemoryService> _logger;

    public StageRevisionMemoryService(
        AppDbContext db,
        ILlmClient llm,
        PromptTemplateService prompts,
        ChecklistNoteStore noteStore,
        ILogger<StageRevisionMemoryService> logger)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _noteStore = noteStore;
        _logger = logger;
    }

    /// <summary>
    /// Chắt lọc các nhận xét đang chờ trong hàng đợi của project thành bài học cho vai đã chạy bước bị góp
    /// ý. Hàng đợi rỗng ⇒ no-op (một truy vấn). Mọi lỗi đều nuốt + log — đây là bước phụ trợ chạy nền,
    /// không được làm fail task đang chạy.
    /// </summary>
    public async Task TryHarvestAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = await _db.AgentTasks
                .Include(t => t.Agent)
                .ThenInclude(a => a!.AiModel)
                .Where(t => t.ProjectId == projectId && t.PendingLessonHarvest)
                .OrderBy(t => t.CreatedAt)
                .ThenBy(t => t.Id)
                .ToListAsync(cancellationToken);

            if (pending.Count == 0)
                return;

            // Gom theo AGENT: cùng một vai bị góp ý ở nhiều bước (Developer có POC, Implementation, Pull
            // Request) thì các nhận xét vào CHUNG một lời gọi và chung một bucket — bài học là của vai,
            // không phải của bước. Bước chỉ đi kèm làm ngữ cảnh trong phần bằng chứng.
            foreach (var group in pending.GroupBy(t => t.AgentId))
            {
                if (group.Key == null)
                    continue;

                await HarvestForAgentAsync(projectId, group.ToList(), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not harvest stage revision feedback for project {ProjectId}.", projectId);
        }
    }

    // Một vai, một lời gọi. Fail-open ở mức TỪNG VAI: Developer lỗi thì Technical Lead vẫn học được, và
    // cờ của Developer đứng yên để task sau gộp bù.
    private async Task HarvestForAgentAsync(Guid projectId, List<AgentTask> tasks, CancellationToken cancellationToken)
    {
        var agent = tasks[0].Agent;

        // Chưa gắn model ⇒ chưa gọi được: GIỮ cờ để lần drain sau (sau khi ai đó cấu hình model ở màn
        // Agents) học bù, thay vì đốt bằng chứng của người dùng.
        if (agent?.AiModel == null)
            return;

        // Bài học chỉ được ĐỌC LẠI ở đường agent chung (AgentRunService nạp checklist theo agent của task).
        // Bước TechnicalDocs do BA chạy qua RequirementDocsService — không đi qua đường đó, nên bài học học
        // được ở đấy sẽ không bao giờ tới tay ai. Hạ cờ và bỏ qua thay vì trả tiền cho một lời gọi vô ích;
        // khoảng trống của BA đã có ba đường harvest riêng lo (xem RequirementMemoryHarvester).
        var evidence = tasks.Where(t => t.Type != AgentTaskType.TechnicalDocs).ToList();
        if (evidence.Count == 0)
        {
            ClearQueue(tasks);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var existing = await _noteStore.LoadBucketAsync(agent, null, cancellationToken);
        var lessons = await DistillAsync(existing, evidence, agent, agent.AiModel, projectId, cancellationToken);
        if (lessons == null)
            return; // fail-open: giữ checklist cũ và cờ đứng yên, task sau gộp bù.

        _noteStore.MergeHarvest(agent, null, existing, lessons.Items, ChecklistItemSource.StageRevision, projectId);
        ClearQueue(tasks);
        await _db.SaveChangesAsync(cancellationToken);
    }

    // Hạ cờ cho CẢ nhóm, kể cả các task bị loại khỏi bằng chứng: chúng đã được cân nhắc xong ở vòng này.
    private static void ClearQueue(List<AgentTask> tasks)
    {
        foreach (var task in tasks)
            task.PendingLessonHarvest = false;
    }

    // Rút bài học MỚI từ nhận xét của người duyệt. Trả null khi lời gọi lỗi để caller fail-open (giữ
    // checklist cũ, cờ đứng yên); danh sách RỖNG nghĩa là "không rút được gì" — vẫn là thành công, hạ cờ.
    private async Task<ChecklistLessonSet?> DistillAsync(
        List<AgentChecklistItem> existing,
        List<AgentTask> tasks,
        Agent agent,
        AiModel model,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Vai đang rút kinh nghiệm: {agent.RoleKey.GetTitle()}");
        sb.AppendLine();

        var context = ChecklistNoteStore.RenderContextForHarvest(existing);
        if (context.Length > 0)
        {
            sb.AppendLine(context);
            sb.AppendLine();
        }

        sb.AppendLine("## Nhận xét của người duyệt ở các bước vai này đã chạy (bước đó sau đó ĐÃ được duyệt)");
        foreach (var task in tasks)
        {
            var feedback = (task.RevisionFeedback ?? string.Empty).Trim();
            if (feedback.Length == 0)
                continue;
            if (feedback.Length > MaxFeedbackChars)
                feedback = feedback[..MaxFeedbackChars];

            sb.AppendLine($"### Bước \"{task.Title}\"");
            sb.AppendLine(feedback);
            sb.AppendLine();
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("Shared/stage-revision-lesson.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var (result, structured) = await _llm.ChatStructuredAsync<ChecklistLessonSet>(
            model, messages, agent.Temperature, new ModelCallLogContext(projectId, agent, "StageRevisionLesson"),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess)
            return null;

        var lessons = structured ?? LlmJson.TryDeserialize<ChecklistLessonSet>(result.Content, requireKnownProperty: true);
        if (lessons != null)
            return lessons;

        // Gọi được nhưng phản hồi không đọc nổi: coi như không rút được gì và VẪN hạ cờ — các nhận xét này
        // đã tiêu một lời gọi, gộp lại ở vòng sau chỉ tốn thêm mà không khá hơn.
        _logger.LogWarning("Stage revision lesson harvest for project {ProjectId} returned unparseable output.", projectId);
        return new ChecklistLessonSet();
    }
}

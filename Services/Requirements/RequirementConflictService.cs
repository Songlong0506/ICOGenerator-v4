using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Cổng soát MÂU THUẪN ngay trước khi soạn Product Brief.
///
/// <para>
/// Hệ thống đã có cổng "ĐỦ" (<see cref="RequirementReadinessGate"/> trên bản đồ bao phủ): mọi nhóm thông
/// tin phải [RÕ] mới được viết tài liệu. Nhưng "rõ hết" không có nghĩa là "không chọi nhau" — người dùng
/// nói ở lượt 3 rằng quản lý duyệt xong là hết, tới lượt 12 lại kể thêm HR duyệt lần nữa. Bản đồ bao phủ
/// đánh dấu nhóm đó [RÕ] cả hai lần, và bước soạn tài liệu (bị CẤM tự giả định) sẽ chọn bừa một bên rồi
/// đóng băng nó thành yêu cầu — POC dựng đúng theo điều sai đó, và lỗi chỉ lộ ra khi người dùng xem demo.
/// </para>
///
/// <para>
/// FAIL-OPEN toàn phần như mọi bộ nhớ khác: chưa cấu hình BA, lời gọi LLM lỗi, model trả rác ⇒ không có
/// mâu thuẫn nào và người dùng đi tiếp bình thường. Cổng này chỉ được phép LÀM CHẬM một lần bấm khi nó
/// thật sự tìm thấy hai câu chọi nhau, không bao giờ được chặn đường vì lý do hạ tầng.
/// </para>
/// </summary>
public class RequirementConflictService
{
    /// <summary>Trần số mâu thuẫn hiển thị — prompt đã giới hạn 5; model trả nhiều hơn thì cắt.</summary>
    private const int MaxConflicts = 5;

    /// <summary>Số lượt hội thoại gần nhất đưa vào ngữ cảnh soát (nhật ký + ví dụ đã là bản đúc kết rồi).</summary>
    private const int RecentTurns = 24;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly ILogger<RequirementConflictService> _logger;

    public RequirementConflictService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts, ILogger<RequirementConflictService> logger)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _logger = logger;
    }

    /// <summary>
    /// Soát mâu thuẫn cho project và LƯU kết quả lên <see cref="Project.PendingConflicts"/> (kèm con trỏ
    /// lượt đã soát) để lần bấm sau — hoặc F5 — không phải gọi lại LLM.
    /// <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK.
    /// </summary>
    public async Task<IReadOnlyList<RequirementConflict>> DetectAndStoreAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        var turnCount = await _db.AgentConversations.CountAsync(c => c.ProjectId == project.Id, cancellationToken);

        // Đã soát ở đúng trạng thái hội thoại này rồi ⇒ dùng lại kết quả cũ. Không có việc này thì mỗi lần
        // người dùng bấm "Write Requirement" (kể cả sau khi vừa chốt xong) lại tốn một lời gọi LLM.
        if (project.ConflictCheckedTurnCount == turnCount)
            return Parse(project.PendingConflicts);

        var conflicts = await DetectAsync(project, ba, model, cancellationToken);

        // Lời gọi lỗi ⇒ null: KHÔNG dời con trỏ (lần sau soát lại), cũng không xoá kết quả cũ.
        if (conflicts == null)
            return Parse(project.PendingConflicts);

        project.PendingConflicts = conflicts.Count == 0 ? null : JsonSerializer.Serialize(conflicts);
        project.ConflictCheckedTurnCount = turnCount;
        await _db.SaveChangesAsync(cancellationToken);
        return conflicts;
    }

    /// <summary>
    /// Ghi nhận các lựa chọn của người dùng thành MỘT cặp lượt hội thoại (user chốt + BA xác nhận) rồi gỡ
    /// cổng. Phải ghi cả lượt assistant: một lượt user "cụt" ở cuối hội thoại làm màn hình chat kẹt ở
    /// "BA đang soạn câu trả lời…" (xem <see cref="BAChatService.GetReplyStateAsync"/>).
    /// </summary>
    public async Task ApplyResolutionsAsync(Project project, Agent ba, IReadOnlyList<ConflictResolution> resolutions, BAConversationLog log, CancellationToken cancellationToken = default)
    {
        var userMessage = new StringBuilder("Mình chốt lại các điểm còn mâu thuẫn như sau:");
        foreach (var r in resolutions)
            userMessage.Append($"\n- {r.Question} → {r.Choice}");

        await log.AppendAsync(project.Id, ba.Id, "user", userMessage.ToString(), cancellationToken: cancellationToken);
        await log.AppendAsync(project.Id, ba.Id, "assistant",
            "Đã ghi nhận các điểm anh/chị vừa chốt lại. Mình sẽ dùng đúng các phương án này khi soạn tài liệu.",
            cancellationToken: cancellationToken);

        project.PendingConflicts = null;
        // Con trỏ nhảy tới đúng số lượt SAU khi ghi (gồm cả hai lượt vừa thêm) để lần bấm ngay sau đó
        // không soát lại chính các câu vừa chốt — hội thoại mới phát sinh về sau thì cổng tự soát tiếp.
        project.ConflictCheckedTurnCount = await _db.AgentConversations.CountAsync(c => c.ProjectId == project.Id, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Đọc danh sách mâu thuẫn đang chờ của project (JSON hỏng/rỗng ⇒ danh sách rỗng).</summary>
    public static IReadOnlyList<RequirementConflict> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<RequirementConflict>();

        try
        {
            return JsonSerializer.Deserialize<List<RequirementConflict>>(json, JsonOptions) ?? new List<RequirementConflict>();
        }
        catch
        {
            return Array.Empty<RequirementConflict>();
        }
    }

    // null = KHÔNG kết luận được (lỗi gọi/parse) — khác hẳn danh sách rỗng (đã soát, không có mâu thuẫn).
    private async Task<List<RequirementConflict>?> DetectAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        try
        {
            var context = await BuildContextAsync(project, cancellationToken);
            if (string.IsNullOrWhiteSpace(context))
                return new List<RequirementConflict>();

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _prompts.Get("BusinessAnalyst/conflict-check.v1.md").Replace("{{input}}", context))
            };

            var (callResult, structured) = await _llm.ChatStructuredAsync<RequirementConflictSet>(
                model, messages, ba.Temperature, new ModelCallLogContext(project.Id, ba, "BAConflictCheck"),
                cancellationToken: cancellationToken);

            if (!callResult.IsSuccess)
            {
                _logger.LogWarning("Conflict check failed for project {ProjectId}: {Error}", project.Id, callResult.ErrorMessage ?? callResult.Content);
                return null;
            }

            var set = structured ?? ParseFallback(callResult.Content);
            return set == null ? null : Sanitize(set);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conflict check threw for project {ProjectId}.", project.Id);
            return null;
        }
    }

    // Ngữ cảnh soát = các bản ĐÚC KẾT (nhật ký chốt, ví dụ đã xác nhận, phạm vi, sơ đồ luồng đã trình bày)
    // + các lượt gần đây. Đúc kết cho độ chính xác (đã lọc nhiễu), lượt gần đây cho những điều vừa nói mà
    // chưa kịp vào đúc kết.
    private async Task<string> BuildContextAsync(Project project, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        var decisions = DecisionLogService.ParseItems(project.DecisionLog);
        if (decisions.Count > 0)
        {
            sb.AppendLine("## Nhật ký điều đã chốt");
            foreach (var d in decisions)
                sb.AppendLine($"- {d}");
            sb.AppendLine();
        }

        var examples = InterviewOutlookService.ParseItems(project.WorkedExamples);
        if (examples.Count > 0)
        {
            sb.AppendLine("## Ví dụ tính thử người dùng đã xác nhận");
            foreach (var e in examples)
                sb.AppendLine($"- {e}");
            sb.AppendLine();
        }

        var scope = InterviewOutlookService.ParseItems(project.PlannedScope);
        if (scope.Count > 0)
        {
            sb.AppendLine("## Màn hình/tính năng dự kiến");
            foreach (var s in scope)
                sb.AppendLine($"- {s}");
            sb.AppendLine();
        }

        var turns = await _db.AgentConversations
            .Where(c => c.ProjectId == project.Id)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(RecentTurns)
            .ToListAsync(cancellationToken);

        if (turns.Count > 0)
        {
            sb.AppendLine("## Các lượt hội thoại gần nhất (cũ → mới)");
            foreach (var turn in Enumerable.Reverse(turns))
                sb.AppendLine($"- {ConversationTurnRenderer.Render(turn)}");
        }

        // Không có gì để soát (dự án mới tinh) ⇒ chuỗi rỗng, caller trả danh sách rỗng mà không gọi LLM.
        return decisions.Count == 0 && turns.Count == 0 ? string.Empty : sb.ToString();
    }

    private static RequirementConflictSet? ParseFallback(string? raw)
    {
        try
        {
            var json = JsonExtractor.Extract(raw ?? string.Empty);
            return json.Length == 0 ? null : JsonSerializer.Deserialize<RequirementConflictSet>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    // Bỏ mục thiếu nội dung để panel không hiện thẻ trống, và luôn có ít nhất hai phương án để bấm.
    private static List<RequirementConflict> Sanitize(RequirementConflictSet set)
    {
        var conflicts = new List<RequirementConflict>();
        foreach (var c in set.Conflicts ?? new List<RequirementConflict>())
        {
            if (conflicts.Count >= MaxConflicts)
                break;
            if (string.IsNullOrWhiteSpace(c.Question) || string.IsNullOrWhiteSpace(c.SideA) || string.IsNullOrWhiteSpace(c.SideB))
                continue;

            var options = (c.Options ?? new List<string>())
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Select(o => o.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
            if (options.Count < 2)
                options = new List<string> { c.SideA.Trim(), c.SideB.Trim() };

            conflicts.Add(new RequirementConflict
            {
                Topic = (c.Topic ?? string.Empty).Trim(),
                SideA = c.SideA.Trim(),
                SideB = c.SideB.Trim(),
                Question = c.Question.Trim(),
                Options = options
            });
        }

        return conflicts;
    }
}

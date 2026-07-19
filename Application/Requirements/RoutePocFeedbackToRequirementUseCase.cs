using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Application.Requirements;

public enum RoutePocFeedbackResult
{
    Ok,               // đã gửi một correction về hội thoại + khởi động lại soạn draft
    NoOpenComments,   // không có ghi chú Open nào để xét
    NoRequirementIssue, // có ghi chú nhưng tất cả chỉ là thẩm mỹ/HTML — không đụng tài liệu
    ProjectNotFound,
    BaNotConfigured
}

/// <summary>
/// Đóng vòng POC → TÀI LIỆU của CHÍNH dự án: các ghi chú ghim trên POC phản ánh HIỂU SAI YÊU CẦU (thiếu
/// màn hình/bước, sai công thức, thiếu vai trò/trạng thái…) được lọc ra, diễn đạt lại thành MỘT lượt phản
/// hồi trong hội thoại BA rồi khởi động lại workflow soạn draft — Product Brief/AI Design Spec được sửa,
/// POC dựng lại từ tài liệu đã sửa. Khác với "Yêu cầu chỉnh sửa" ở cổng POC (vốn chỉ vá HTML): ở đó tài
/// liệu — nguồn sự thật của mọi bước sau (technical docs, architecture, implementation) — vẫn giữ cái sai.
/// Ghi chú thuần thẩm mỹ được BỎ QUA ở đây (giữ cho đường vá POC của Developer). Tái dùng đúng vòng
/// "Write Requirement" hiện có, không thêm đường sinh tài liệu song song.
/// </summary>
public class RoutePocFeedbackToRequirementUseCase
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly BAAgentResolver _agentResolver;
    private readonly BAConversationLog _conversationLog;
    private readonly GenerateRequirementDraftUseCase _generateDraft;

    public RoutePocFeedbackToRequirementUseCase(
        AppDbContext db,
        ILlmClient llm,
        PromptTemplateService prompts,
        BAAgentResolver agentResolver,
        BAConversationLog conversationLog,
        GenerateRequirementDraftUseCase generateDraft)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _agentResolver = agentResolver;
        _conversationLog = conversationLog;
        _generateDraft = generateDraft;
    }

    public async Task<RoutePocFeedbackResult> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return RoutePocFeedbackResult.ProjectNotFound;

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return RoutePocFeedbackResult.BaNotConfigured;

        var open = await _db.PocComments
            .Where(c => c.ProjectId == projectId && c.Status == PocCommentStatus.Open)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
            return RoutePocFeedbackResult.NoOpenComments;

        // Lọc + diễn đạt lại các ghi chú thuộc YÊU CẦU (bỏ ghi chú thẩm mỹ) thành một tin nhắn ngôi thứ nhất.
        var sb = new StringBuilder();
        sb.AppendLine("## Các ghi chú người dùng ghim trên POC khi review");
        foreach (var c in open)
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
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/poc-feedback-route.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var (callResult, routed) = await _llm.ChatStructuredAsync<PocFeedbackRouteResult>(
            ba.AiModel!, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAPocFeedbackRoute"),
            cancellationToken: cancellationToken);

        if (!callResult.IsSuccess || routed == null || !routed.HasRequirementIssue || string.IsNullOrWhiteSpace(routed.Message))
            return RoutePocFeedbackResult.NoRequirementIssue;

        // Lượt user này đi vào transcript → workflow "Write Requirement" soạn lại Brief có tính tới nó.
        await _conversationLog.AppendAsync(projectId, ba.Id, "user", routed.Message.Trim(), cancellationToken: cancellationToken);

        // Đánh dấu các ghi chú đã được gửi về Requirement để không xử lý lặp và tách khỏi đường vá POC.
        foreach (var c in open)
            c.Status = PocCommentStatus.RoutedToRequirement;
        await _db.SaveChangesAsync(cancellationToken);

        await _generateDraft.ExecuteAsync(projectId);
        return RoutePocFeedbackResult.Ok;
    }
}

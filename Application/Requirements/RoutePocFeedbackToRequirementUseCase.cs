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
    NoOpenComments,   // không ghi chú nào trong danh sách chọn còn ở trạng thái Open
    ComposeFailed,    // model không soạn được tin nhắn — KHÔNG đổi trạng thái ghi chú nào
    ProjectNotFound,
    BaNotConfigured
}

/// <summary>
/// Đóng vòng POC → TÀI LIỆU của CHÍNH dự án: các ghi chú ghim trên POC phản ánh HIỂU SAI YÊU CẦU (thiếu
/// màn hình/bước, sai công thức, thiếu vai trò/trạng thái…) được diễn đạt lại thành MỘT lượt phản hồi
/// trong hội thoại BA rồi khởi động lại workflow soạn draft — Product Brief/AI Design Spec được sửa, POC
/// dựng lại từ tài liệu đã sửa. Khác với "chỉnh bản demo" (vốn chỉ vá HTML): ở đó tài liệu — nguồn sự
/// thật của mọi bước sau (technical docs, architecture, implementation) — vẫn giữ cái sai. Tái dùng đúng
/// vòng "Write Requirement" hiện có, không thêm đường sinh tài liệu song song.
///
/// VIỆC CHỌN ghi chú nào đi đường này KHÔNG diễn ra ở đây. Trước đây use case này tự lọc bằng LLM rồi
/// đánh dấu TẤT CẢ ghi chú Open là RoutedToRequirement — kể cả những ghi chú thẩm mỹ mà chính nó vừa loại
/// khỏi tin nhắn gửi BA. Chúng biến mất khỏi đường vá POC và ReopenPocCommentUseCase cũng không mở lại
/// được, tức là mất trắng. Nay caller (DispatchPocFeedbackUseCase, sau khi người dùng xác nhận bảng phân
/// loại) truyền vào ĐÚNG tập ghi chú cần gửi, và chỉ tập đó đổi trạng thái.
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

    /// <param name="commentIds">
    /// Các ghi chú NGƯỜI DÙNG đã xác nhận là hiểu-sai-yêu-cầu. Chỉ những ghi chú còn Open và thuộc đúng
    /// project mới được xét; ghi chú Open khác của project KHÔNG bị đụng tới.
    /// </param>
    public async Task<RoutePocFeedbackResult> ExecuteAsync(
        Guid projectId, IReadOnlyCollection<Guid> commentIds, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return RoutePocFeedbackResult.ProjectNotFound;

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return RoutePocFeedbackResult.BaNotConfigured;

        if (commentIds.Count == 0)
            return RoutePocFeedbackResult.NoOpenComments;

        var ids = commentIds.Distinct().ToList();
        var selected = await _db.PocComments
            .Where(c => c.ProjectId == projectId && c.Status == PocCommentStatus.Open && ids.Contains(c.Id))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        if (selected.Count == 0)
            return RoutePocFeedbackResult.NoOpenComments;

        // Diễn đạt lại tập ghi chú ĐÃ CHỌN thành một tin nhắn ngôi thứ nhất. Prompt compose không lọc bỏ
        // gì — quyết định nhóm là của người dùng, không phải của model.
        var sb = new StringBuilder();
        sb.AppendLine("## Các ghi chú người dùng ghim trên POC và xác nhận là điểm hiểu sai yêu cầu");
        foreach (var c in selected)
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
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/poc-feedback-compose.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var (callResult, composed) = await _llm.ChatStructuredAsync<PocFeedbackComposeResult>(
            ba.AiModel!, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAPocFeedbackCompose"),
            cancellationToken: cancellationToken);

        // Soạn hụt ⇒ dừng hẳn, KHÔNG đổi trạng thái ghi chú nào: người dùng bấm lại là mất trắng phản hồi
        // nếu ở đây đã "tiêu" chúng.
        if (!callResult.IsSuccess || composed == null || string.IsNullOrWhiteSpace(composed.Message))
            return RoutePocFeedbackResult.ComposeFailed;

        // Lượt user này đi vào transcript → workflow "Write Requirement" soạn lại Brief có tính tới nó.
        await _conversationLog.AppendAsync(projectId, ba.Id, "user", composed.Message.Trim(), cancellationToken: cancellationToken);

        // Chỉ ĐÚNG các ghi chú đã gửi mới đổi trạng thái — ghi chú Open còn lại vẫn chờ đường của chúng.
        foreach (var c in selected)
            c.Status = PocCommentStatus.RoutedToRequirement;
        await _db.SaveChangesAsync(cancellationToken);

        await _generateDraft.ExecuteAsync(projectId);
        return RoutePocFeedbackResult.Ok;
    }
}

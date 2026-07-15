using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Bộ nhớ hội thoại cho BA chat — kết hợp hai tầng nhớ:
/// <list type="bullet">
/// <item><b>Ngắn hạn (working memory):</b> N lượt gần nhất luôn gửi NGUYÊN VĂN cho model.</item>
/// <item><b>Dài hạn:</b> các lượt CŨ rơi ra ngoài cửa sổ được <b>gộp dần</b> thành một đoạn tóm tắt bền
/// lưu trên <see cref="Project.ConversationSummary"/>.</item>
/// </list>
/// Nhờ vậy hội thoại dài vẫn không mất ngữ cảnh cũ mà prompt không phình token: thay vì gửi lại hàng
/// chục lượt cũ, chỉ gửi MỘT đoạn summary + cửa sổ lượt gần đây. Việc tóm tắt được <b>gom theo lô</b>
/// (chỉ gọi LLM khi đã đủ một nhúm lượt cũ) nên không tóm tắt trên mỗi lượt chat — đây mới là chỗ tiết
/// kiệm token thực sự.
/// </summary>
public class ConversationMemoryService
{
    // Số lượt gần nhất luôn gửi NGUYÊN VĂN cho model (short-term / working memory).
    public const int RecentWindowSize = 20;

    // Chỉ gọi LLM gộp khi đã có ÍT NHẤT chừng này lượt cũ (ngoài cửa sổ) chưa tóm tắt, để batch và đỡ
    // token. Trong lúc chờ đạt ngưỡng, các lượt đó VẪN được gửi nguyên văn nên không hề mất ngữ cảnh —
    // cửa sổ verbatim chỉ phình tạm tới RecentWindowSize + (ngưỡng - 1) rồi co lại sau mỗi lần gộp.
    public const int SummarizeBatchThreshold = 10;

    // Chặn trên độ dài summary để bộ nhớ dài hạn không tự phình vô hạn qua nhiều lần gộp.
    private const int MaxSummaryChars = 6000;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public ConversationMemoryService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>Summary dài hạn hiện hành + danh sách lượt gần đây cần gửi nguyên văn (đã bỏ phần đã gộp).</summary>
    public sealed record Memory(string? Summary, List<AgentConversation> RecentTurns);

    // Thứ tự ổn định cho Skip/Take: CreatedAt rồi Id để con trỏ "đã tóm tắt" và cửa sổ verbatim khớp
    // nhau một cách tất định (CreatedAt có thể trùng tới mili-giây giữa hai lượt liền nhau).
    private IOrderedQueryable<AgentConversation> Ordered(Guid projectId) =>
        _db.AgentConversations
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id);

    /// <summary>
    /// Cập nhật summary nếu vừa có đủ một lô lượt cũ rơi ra ngoài cửa sổ, rồi trả về (summary hiện hành +
    /// các lượt gần đây cần gửi nguyên văn). <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK —
    /// các cột bộ nhớ được ghi thẳng lên nó và lưu trong này. Fail-open: nếu lời gọi LLM tóm tắt lỗi thì
    /// GIỮ NGUYÊN summary cũ và KHÔNG dời con trỏ — các lượt chưa gộp vẫn nằm trong danh sách trả về (gửi
    /// nguyên văn) nên không mất ngữ cảnh.
    /// </summary>
    public async Task<Memory> LoadAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        var total = await _db.AgentConversations.CountAsync(c => c.ProjectId == project.Id, cancellationToken);
        var summarized = project.SummarizedTurnCount;

        // Số lượt đang gửi nguyên văn nhưng đã CŨ hơn cửa sổ. Đạt ngưỡng thì mới gộp (batch).
        var excess = (total - summarized) - RecentWindowSize;
        if (excess >= SummarizeBatchThreshold)
        {
            var toFold = await Ordered(project.Id).Skip(summarized).Take(excess).ToListAsync(cancellationToken);
            var updated = await SummarizeAsync(project.ConversationSummary, toFold, ba, model, project.Id, cancellationToken);
            if (updated != null)
            {
                project.ConversationSummary = updated;
                project.SummarizedTurnCount = summarized + toFold.Count;
                summarized = project.SummarizedTurnCount;
                await _db.SaveChangesAsync(cancellationToken);
            }
            // updated == null ⇒ tóm tắt lỗi: bỏ qua, các lượt cũ rơi vào danh sách "recent" dưới đây.
        }

        var recent = await Ordered(project.Id).Skip(summarized).ToListAsync(cancellationToken);
        return new Memory(project.ConversationSummary, recent);
    }

    // Gộp existingSummary + các lượt mới thành một summary duy nhất. Trả về null khi lời gọi lỗi/ rỗng để
    // caller fail-open (giữ summary cũ, không dời con trỏ).
    private async Task<string?> SummarizeAsync(string? existingSummary, List<AgentConversation> turns, Agent ba, AiModel model, Guid projectId, CancellationToken cancellationToken)
    {
        if (turns.Count == 0)
            return existingSummary;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            sb.AppendLine("## Tóm tắt hiện có (gộp/cập nhật cùng các lượt mới bên dưới)");
            sb.AppendLine(existingSummary.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("## Các lượt hội thoại cần gộp vào tóm tắt");
        foreach (var t in turns)
        {
            var who = t.Role == "assistant" ? "BA" : "Người dùng";
            sb.AppendLine($"- {who}: {(t.Message ?? string.Empty).Trim()}");
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/conversation-summary.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var result = await _llm.ChatWithLogAsync(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, "BAConversationSummary"),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Content))
            return null;

        var summary = result.Content.Trim();
        return summary.Length > MaxSummaryChars ? summary[..MaxSummaryChars] : summary;
    }
}

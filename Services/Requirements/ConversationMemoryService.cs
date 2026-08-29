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
/// <item><b>Ngắn hạn (working memory):</b> các lượt gần nhất luôn gửi NGUYÊN VĂN cho model.</item>
/// <item><b>Dài hạn:</b> các lượt CŨ rơi ra ngoài cửa sổ được <b>gộp dần</b> thành một đoạn tóm tắt bền
/// lưu trên <see cref="Project.ConversationSummary"/>.</item>
/// </list>
/// Nhờ vậy hội thoại dài vẫn không mất ngữ cảnh cũ mà prompt không phình token: thay vì gửi lại hàng
/// chục lượt cũ, chỉ gửi MỘT đoạn summary + cửa sổ lượt gần đây. Việc tóm tắt được <b>gom theo lô</b>
/// (chỉ gọi LLM khi đã đủ một nhúm token lượt cũ) nên không tóm tắt trên mỗi lượt chat.
/// <para>
/// ĐO BẰNG TOKEN, KHÔNG ĐẾM LƯỢT (đây là chỗ dễ hiểu nhầm nhất nếu sửa sau này). Lượt hội thoại lệch
/// nhau hàng trăm lần về độ dài: một lượt chốt bảng phân quyền dài bằng vài chục lượt gật đầu bằng
/// chip. Đếm lượt vì thế không chặn được thứ ta thực sự trả tiền, và đường soạn Product Brief đã phải
/// tự nhận ra điều đó một lần rồi (<see cref="BriefContextWindow.MaxVerbatimChars"/>). Trần lượt vẫn
/// còn, nhưng chỉ như cận trên thứ hai — cái nào chặt hơn thì cái đó thắng.
/// </para>
/// <para>
/// Token đo trên <see cref="ConversationTurnRenderer.Render"/> chứ KHÔNG dùng cột
/// <see cref="AgentConversation.TokenUsed"/>: cột đó chỉ ước lượng trên <c>Message</c>, trong khi thứ
/// thật sự đi vào ngữ cảnh còn có các bảng đã chốt (gợi ý, bảng cột, bảng phân quyền, bảng luồng…).
/// Tức là cột đó ước lượng thiếu đúng ở những lượt NẶNG NHẤT — chính các lượt mà một trần token sinh ra
/// để chặn.
/// </para>
/// </summary>
public class ConversationMemoryService
{
    // Trần SỐ LƯỢT của cửa sổ nguyên văn. Nới từ 20 lên 40 khi app chuẩn hóa về gpt-5.6-luna: với trần
    // giá/ngữ cảnh của model đó, chênh lệch chi phí giữa cửa sổ 20 và 40 lượt là vài xu cho cả một buổi
    // phỏng vấn, còn BA thấy nhiều lượt nguyên văn thì hỏi trúng hơn hẳn — nén sớm là đánh đổi sai chiều.
    public const int RecentWindowTurns = 40;

    // Trần TOKEN của cửa sổ nguyên văn khi model không nói gì khác (xem RecentWindowTokensFor).
    public const int DefaultRecentWindowTokens = 20_000;

    // Chỉ gọi LLM gộp khi phần lượt cũ (ngoài cửa sổ) chưa tóm tắt đã đạt chừng này TOKEN, để batch và đỡ
    // token. Trong lúc chờ đạt ngưỡng, các lượt đó VẪN được gửi nguyên văn nên không hề mất ngữ cảnh —
    // cửa sổ verbatim chỉ phình tạm rồi co lại sau mỗi lần gộp.
    public const int SummarizeBatchTokens = 5_000;

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

    /// <summary>
    /// Trần token của cửa sổ nguyên văn cho một model: phần hội thoại của ngân sách prompt
    /// (<see cref="PromptBudget.ConversationTokens"/>), nhưng không bao giờ rộng hơn
    /// <see cref="DefaultRecentWindowTokens"/> — ngân sách là cận trên AN TOÀN, không phải mục tiêu cần
    /// tiêu cho hết. Model context lớn thì phần dư dùng để KHÔNG bao giờ phải cắt, chứ không phải để
    /// đẩy thêm hàng trăm lượt cũ lên mỗi lời gọi.
    /// </summary>
    public static int RecentWindowTokensFor(AiModel model) =>
        Math.Min(DefaultRecentWindowTokens, PromptBudget.ConversationTokens(model));

    // Thứ tự ổn định cho Skip/Take: CreatedAt rồi Id để con trỏ "đã tóm tắt" và cửa sổ verbatim khớp
    // nhau một cách tất định (CreatedAt có thể trùng tới mili-giây giữa hai lượt liền nhau).
    private IOrderedQueryable<AgentConversation> Ordered(Guid projectId) =>
        _db.AgentConversations
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id);

    /// <summary>
    /// Cập nhật summary nếu vừa có đủ một lô token lượt cũ rơi ra ngoài cửa sổ, rồi trả về (summary hiện
    /// hành + các lượt gần đây cần gửi nguyên văn). <paramref name="project"/> phải là entity ĐANG ĐƯỢC
    /// TRACK — các cột bộ nhớ được ghi thẳng lên nó và lưu trong này. Fail-open: nếu lời gọi LLM tóm tắt
    /// lỗi thì GIỮ NGUYÊN summary cũ và KHÔNG dời con trỏ — các lượt chưa gộp vẫn nằm trong danh sách trả
    /// về (gửi nguyên văn) nên không mất ngữ cảnh.
    /// </summary>
    public async Task<Memory> LoadAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        var summarized = project.SummarizedTurnCount;
        // Một truy vấn duy nhất: phần CHƯA gộp. Trần token phải đo trên nội dung thật của từng lượt nên
        // không còn đường nào chỉ đếm số dòng.
        var unsummarized = await Ordered(project.Id).Skip(summarized).ToListAsync(cancellationToken);

        var excess = ComputeFoldableCount(unsummarized, RecentWindowTokensFor(model));
        if (excess > 0)
        {
            var toFold = unsummarized.Take(excess).ToList();
            var updated = await SummarizeAsync(project.ConversationSummary, toFold, ba, model, project.Id, cancellationToken);
            if (updated != null)
            {
                project.ConversationSummary = updated;
                project.SummarizedTurnCount = summarized + toFold.Count;
                await _db.SaveChangesAsync(cancellationToken);
                return new Memory(project.ConversationSummary, unsummarized.Skip(excess).ToList());
            }
            // updated == null ⇒ tóm tắt lỗi: bỏ qua, các lượt cũ ở lại danh sách "recent" bên dưới.
        }

        return new Memory(project.ConversationSummary, unsummarized);
    }

    /// <summary>
    /// Số lượt CŨ NHẤT (trong phần chưa gộp) được phép gộp lần này — 0 nghĩa là chưa đủ lô, đừng gọi LLM.
    /// Cửa sổ giữ lại là hậu tố dài nhất vừa lọt CẢ trần lượt lẫn trần token; phần dôi ra chỉ được gộp
    /// khi nó đã đạt <see cref="SummarizeBatchTokens"/>.
    /// </summary>
    /// <param name="unsummarized">Các lượt chưa gộp, theo đúng thứ tự thời gian.</param>
    /// <param name="windowTokens">Trần token của cửa sổ nguyên văn.</param>
    public static int ComputeFoldableCount(IReadOnlyList<AgentConversation> unsummarized, int windowTokens)
    {
        var tokens = unsummarized.Select(t => TokenEstimator.Estimate(ConversationTurnRenderer.Render(t))).ToList();

        // Đi ngược từ lượt mới nhất, gom vào cửa sổ chừng nào còn lọt cả hai trần. Luôn giữ ÍT NHẤT một
        // lượt: một lượt đơn lẻ dài hơn cả trần token không được phép làm cửa sổ rỗng — gộp sạch tới lượt
        // cuối là bỏ đi chính câu người dùng vừa nói.
        var kept = 0;
        long keptTokens = 0;
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (kept >= RecentWindowTurns || (kept > 0 && keptTokens + tokens[i] > windowTokens))
                break;
            keptTokens += tokens[i];
            kept++;
        }

        var foldable = tokens.Count - kept;
        if (foldable <= 0)
            return 0;

        long foldableTokens = 0;
        for (var i = 0; i < foldable; i++)
            foldableTokens += tokens[i];

        return foldableTokens >= SummarizeBatchTokens ? foldable : 0;
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
        // Render qua ConversationTurnRenderer: các bảng đã chốt (gợi ý, bảng cột, bảng phân quyền, bảng
        // luồng…) nằm ở các cột riêng chứ không ở Message. Gộp từ Message thuần là để chúng bốc hơi đúng
        // lúc chúng rời khỏi cửa sổ nguyên văn — mất luôn thứ người dùng đã tích tay.
        foreach (var t in turns)
            sb.AppendLine("- " + ConversationTurnRenderer.Render(t));

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

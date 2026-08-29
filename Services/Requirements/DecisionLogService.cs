using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// "Nhật ký điều đã chốt" của MỘT dự án — danh sách bullet các quyết định người dùng đã xác nhận trong
/// chat (vai trò, luồng, quy tắc, phương án đã "Đồng ý"), lưu trên <see cref="Project.DecisionLog"/>.
/// Khác <see cref="RequirementCoverageService"/> (bản đồ trạng thái theo NHÓM: đã rõ hết chưa), nhật ký
/// này giữ nguyên văn TỪNG điều đã chốt — thứ duy nhất đối chiếu được "lượt 3 nói A" với "lượt 12 nói B".
/// <para>
/// Ba đường tiêu thụ, không đường nào là panel để người dùng tự rà (panel sidebar đã gỡ):
/// <list type="number">
/// <item>Ngữ cảnh chat của BA (<see cref="BAChatService"/>) — BA đối chiếu câu vừa nghe với nhật ký và
/// hỏi lại NGAY khi thấy chọi nhau. Cần thiết vì các lượt cũ bị <see cref="ConversationMemoryService"/>
/// nén thành tóm tắt: chi tiết đã chốt bị bào mòn đúng ở hội thoại dài, nơi mâu thuẫn dễ xảy ra nhất.</item>
/// <item>Ngữ cảnh soát mâu thuẫn trước lúc soạn tài liệu (<see cref="RequirementConflictService"/>) — lưới
/// an toàn cho những gì lọt qua đường (1).</item>
/// <item>Cổng tổng kết cuối khung chat (<c>#summaryGate</c>) — lần DUY NHẤT người dùng đọc lại danh sách
/// này, khi phỏng vấn đã xong và họ rảnh trí để rà; sửa ở đó đi qua một lượt chat như mọi đính chính khác.</item>
/// </list>
/// </para>
/// <para>
/// Cùng pattern gộp-lũy-tiến theo con trỏ lượt (<see cref="Project.DecisionHarvestedTurnCount"/>) và
/// <b>fail-open</b> như bản đồ bao phủ: lời gọi LLM lỗi thì giữ nhật ký cũ + không dời con trỏ, lượt sau
/// gộp bù. Được gọi CUỐI lượt chat (sau khi đã lưu lượt trả lời của BA) để quyết định vừa chốt trong lượt
/// này vào ngay nhật ký của frame done. Khác bản đồ một điểm: lô gộp được CHỜM về trước con trỏ vài lượt
/// để câu trả lời mở đầu lô không mất câu hỏi của nó (xem <see cref="ContextTurnCount"/>).
/// </para>
/// </summary>
public class DecisionLogService
{
    // Chặn trên độ dài nhật ký để không tự phình vô hạn (prompt đã giới hạn 40 dòng; model trả dài hơn thì cắt).
    private const int MaxDecisionChars = 6000;

    // Số lượt ĐÃ GỘP được kéo lại làm ngữ cảnh cho lô mới (không chắt lại, không dời con trỏ). Lý do phải
    // có: hàm này chạy CUỐI lượt chat, sau khi lượt trả lời của BA đã lưu, nên mỗi lô là
    // [câu trả lời của người dùng, câu hỏi MỚI của BA] — còn câu hỏi mà người dùng ĐANG trả lời thì nằm ở
    // lô TRƯỚC. Người dùng nghiệp vụ trả lời chủ yếu bằng cách bấm chip, mà chip vốn chỉ có nghĩa khi dán
    // vào câu hỏi ("Chỉ Assistant HR", "Trên 100 người"). Thiếu câu hỏi đó, model không có gì để dựng thành
    // câu tự đứng được: nhật ký sinh ra một dòng cụt, và tệ hơn là dòng ĐỔI NGHĨA (câu trả lời cho "vai trò
    // nào nhận email" bị ghi thành "các vai trò gồm…" — đủ để RequirementConflictService bắt nhầm mâu thuẫn
    // rồi chất vấn người dùng một câu thừa). Hai lượt là đủ phủ [lượt user trước, câu hỏi của BA].
    private const int ContextTurnCount = 2;

    // Hai purpose TÁCH nhau trên màn AI Call Logs: lượt chắt thường, và lượt chắt lại do
    // DecisionUnderHarvestGuard nghi bỏ sót. Tách để đo được guard nổ bao nhiêu lần và có đáng giữ không —
    // một heuristic mới mà không đếm được thì không sửa được.
    private const string DistillPurpose = "BADecisionLog";
    private const string RecheckPurpose = "BADecisionLogRecheck";

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;

    public DecisionLogService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
    }

    /// <summary>
    /// Gộp các lượt chat mới (kể từ con trỏ) vào nhật ký rồi trả về nhật ký hiện hành.
    /// <paramref name="project"/> phải là entity ĐANG ĐƯỢC TRACK — nhật ký + con trỏ ghi thẳng lên nó.
    /// </summary>
    public async Task<string?> UpdateAndLoadAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        var harvested = project.DecisionHarvestedTurnCount;
        var contextStart = Math.Max(0, harvested - ContextTurnCount);

        // Lấy một cửa sổ CHỜM về trước con trỏ rồi mới cắt: các lượt trước con trỏ chỉ để đọc hiểu, phần
        // delta mới là phần được chắt và được con trỏ tính. Min với window.Count để đường "sửa lượt cuối"
        // (xoá lượt, con trỏ có thể lớn hơn số lượt còn lại) không cắt âm.
        var window = await _db.AgentConversations
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Skip(contextStart)
            .ToListAsync(cancellationToken);

        var contextCount = Math.Min(harvested - contextStart, window.Count);
        var context = window.Take(contextCount).ToList();
        var delta = window.Skip(contextCount).ToList();

        if (delta.Count == 0)
            return project.DecisionLog;

        var updated = await DistillAsync(project.DecisionLog, context, delta, ba, model, project.Id, DistillPurpose, null, cancellationToken);
        if (updated != null)
        {
            updated = await RecheckIfSuspectedMissAsync(project, context, delta, updated, ba, model, cancellationToken);
            project.DecisionLog = string.IsNullOrWhiteSpace(updated) ? null : updated;
            project.DecisionHarvestedTurnCount = harvested + delta.Count;
            await _db.SaveChangesAsync(cancellationToken);
        }
        // updated == null ⇒ gộp lỗi: fail-open, giữ nhật ký cũ + con trỏ cũ.

        return project.DecisionLog;
    }

    /// <summary>Tách nhật ký (text bullet) thành danh sách dòng cho UI; text rỗng → danh sách rỗng.</summary>
    public static IReadOnlyList<string> ParseItems(string? decisionLog)
    {
        if (string.IsNullOrWhiteSpace(decisionLog))
            return Array.Empty<string>();

        return decisionLog.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..].Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Phép thử chống BỎ SÓT, chạy bằng CODE (<see cref="DecisionUnderHarvestGuard"/>) chứ không nhờ model
    /// tự chấm: bản đồ bao phủ — một bộ đọc ĐỘC LẬP đọc đúng lô lượt này — đã trích được bằng chứng từ
    /// chính lời người dùng trong lô, mà nhật ký không dài thêm và không sửa dòng nào ⇒ nghi bỏ sót, chắt
    /// lại ĐÚNG MỘT lần với các trích dẫn đó làm chỉ dẫn.
    /// <para>
    /// Lần chắt lại chỉ được nhận khi nó THẬT SỰ đổi nhật ký; không đổi (hoặc lỗi) thì giữ nguyên kết quả
    /// lần đầu và dời con trỏ như thường — nghi ngờ không được phép biến thành vòng lặp. Lời gọi thêm mang
    /// purpose riêng (<c>BADecisionLogRecheck</c>) để đo được tần suất guard nổ trên màn AI Call Logs, và
    /// nó nằm trên đường HẬU KỲ (sau frame done) nên người dùng không chờ thêm.
    /// </para>
    /// </summary>
    private async Task<string> RecheckIfSuspectedMissAsync(Project project, List<AgentConversation> context, List<AgentConversation> delta, string firstPass, Agent ba, AiModel model, CancellationToken cancellationToken)
    {
        var userTurnTexts = delta
            .Where(t => !ConversationTurnRenderer.IsAssistant(t))
            .Select(t => t.Message ?? string.Empty);

        var check = DecisionUnderHarvestGuard.Check(project.RequirementCoverageMap, userTurnTexts, project.DecisionLog, firstPass);
        if (!check.SuspectsMiss)
            return firstPass;

        var second = await DistillAsync(project.DecisionLog, context, delta, ba, model, project.Id, RecheckPurpose, check.Evidence, cancellationToken);
        return second != null && !DecisionUnderHarvestGuard.Unchanged(project.DecisionLog, second) ? second : firstPass;
    }

    private async Task<string?> DistillAsync(string? existingLog, List<AgentConversation> context, List<AgentConversation> turns, Agent ba, AiModel model, Guid projectId, string purpose, IReadOnlyList<string>? missedEvidence, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingLog))
        {
            sb.AppendLine("## Nhật ký hiện có (gộp/cập nhật cùng các lượt mới bên dưới)");
            sb.AppendLine(existingLog.Trim());
            sb.AppendLine();
        }
        if (context.Count > 0)
        {
            // Nói rõ đây KHÔNG phải phần cần chắt: chắt lại lượt cũ là ghi trùng một quyết định đã có
            // trong nhật ký, còn bỏ hẳn thì mất câu hỏi mà lượt user đầu lô đang trả lời.
            sb.AppendLine("## Ngữ cảnh — các lượt TRƯỚC đó, đã gộp rồi (chỉ để hiểu câu trả lời mở đầu lô mới; KHÔNG chắt lại)");
            foreach (var t in context)
                sb.AppendLine($"- {ConversationTurnRenderer.Render(t)}");
            sb.AppendLine();
        }
        sb.AppendLine("## Các lượt hội thoại mới cần gộp vào nhật ký");
        foreach (var t in turns)
        {
            // Render chung (ConversationTurnRenderer): lượt BA kèm các đáp án gợi ý để câu trả lời tham
            // chiếu ("Cả hai mục trên") không trỏ vào khoảng không.
            sb.AppendLine($"- {ConversationTurnRenderer.Render(t)}");
        }

        if (missedEvidence is { Count: > 0 })
        {
            // Chỉ dẫn của lượt chắt LẠI: không phải "cố lên", mà là đúng các câu mà bộ đọc kia đã tìm thấy
            // trong lô này — model được chỉ thẳng vào chỗ nó vừa bỏ qua. Vẫn KHÔNG nới luật "chỉ ghi điều
            // người dùng thật sự nói": nếu rà lại mà đúng là không có quyết định nào thì xuất y như cũ.
            sb.AppendLine();
            sb.AppendLine("## RÀ LẠI — lô trên gần như chắc chắn có nội dung nghiệp vụ");
            sb.AppendLine("Một bộ đọc ĐỘC LẬP (bản đồ bao phủ yêu cầu) đọc đúng các lượt trên và đã trích các câu sau từ CHÍNH lời người dùng làm bằng chứng:");
            foreach (var quote in missedEvidence)
                sb.AppendLine($"- {quote}");
            sb.AppendLine();
            sb.AppendLine("Lần chắt trước không thêm và không sửa dòng nào — đó là dấu hiệu bỏ sót. Hãy rà lại lô và xuất nhật ký ĐẦY ĐỦ (giữ các dòng cũ, thêm dòng cho những điều người dùng đã chốt ở lô này). Vẫn giữ nguyên mọi luật ở trên: KHÔNG suy diễn, KHÔNG ghi điều BA mới chỉ hỏi, mỗi dòng phải TỰ ĐỨNG ĐƯỢC. Rà kỹ mà thật sự không có quyết định nào thì xuất lại nhật ký y như cũ.");
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/decision-log.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var result = await _llm.ChatWithLogAsync(
            model, messages, ba.Temperature, new ModelCallLogContext(projectId, ba, purpose),
            cancellationToken: cancellationToken);

        if (!result.IsSuccess || result.Content == null)
            return null;

        var log = result.Content.Trim();
        return log.Length > MaxDecisionChars ? log[..MaxDecisionChars] : log;
    }
}

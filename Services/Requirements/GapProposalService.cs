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
/// Soạn PHƯƠNG ÁN MẶC ĐỊNH cho mọi nhóm thông tin còn thiếu của bản đồ bao phủ, trong MỘT lời gọi LLM.
/// <para>
/// Lý do tồn tại: phỏng vấn được thiết kế "mỗi lượt một câu hỏi" và cổng "Write Requirement" chỉ mở khi
/// MỌI nhóm áp dụng đã [RÕ] — hai điều đúng về chất lượng nhưng cộng lại thành hàng chục lượt chat, và
/// người dùng nghiệp vụ bận thì rời bỏ giữa chừng chứ không có đường tăng tốc nào. Cổng này đổi N lượt
/// hỏi lẻ thành MỘT lượt duyệt: BA đề xuất sẵn, người dùng gật đầu từng dòng hoặc gõ đè.
/// </para>
/// <para>
/// Nguyên tắc "bước soạn tài liệu KHÔNG được tự giả định" giữ nguyên: phương án chỉ trở thành yêu cầu
/// khi người dùng bấm chốt, và lúc đó nó được ghi vào hội thoại như LỜI CỦA CHÍNH HỌ (xem
/// <c>ConfirmRemainingGapsUseCase</c>) — y hệt một câu "Đồng ý" bấm trên chip gợi ý, chỉ khác là chốt
/// nhiều điểm một lần.
/// </para>
/// </summary>
public class GapProposalService
{
    // Trần số nhóm gửi đi trong một lượt: bản đồ chỉ có 12 dòng nên đây chỉ là chặn phòng thủ.
    private const int MaxGroups = 12;
    // Hội thoại gửi kèm để phương án bám điều người dùng đã nói. Cắt từ CUỐI (lượt gần nhất quan trọng nhất).
    private const int MaxTranscriptChars = 12000;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly ILogger<GapProposalService>? _logger;

    public GapProposalService(
        AppDbContext db,
        ILlmClient llm,
        PromptTemplateService prompts,
        ILogger<GapProposalService>? logger = null)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _logger = logger;
    }

    /// <summary>
    /// Trả về một phương án cho mỗi nhóm <c>[CHƯA HỎI]</c>/<c>[MỘT PHẦN]</c> của bản đồ. Danh sách rỗng
    /// khi không còn nhóm nào thiếu, hoặc khi lời gọi LLM lỗi (fail-open — người dùng vẫn chat như cũ).
    /// Các mục model trả về mà không khớp nhóm nào đang thiếu đều bị loại: cổng này chỉ được lấp đúng
    /// những ô bản đồ đang trống, không phải mở đường cho model tự thêm chủ đề.
    /// </summary>
    public async Task<IReadOnlyList<GapProposal>> ProposeAsync(Project project, Agent ba, AiModel model, CancellationToken cancellationToken = default)
    {
        var pending = CoverageMapParser.Parse(project.RequirementCoverageMap)
            .Where(x => x.Status is "CHƯA HỎI" or "MỘT PHẦN")
            .OrderByDescending(x => x.IsCore)
            .Take(MaxGroups)
            .ToList();

        if (pending.Count == 0)
            return Array.Empty<GapProposal>();

        var conversations = await _db.AgentConversations
            .AsNoTracking()
            .Where(c => c.ProjectId == project.Id)
            .ToListAsync(cancellationToken);

        var transcript = ConversationTranscriptBuilder.Build(conversations);
        if (transcript.Length > MaxTranscriptChars)
            transcript = "…(đã lược phần đầu)\n" + transcript[^MaxTranscriptChars..];

        var sb = new StringBuilder();
        sb.AppendLine("## Các nhóm còn thiếu (soạn ĐÚNG một phương án cho MỖI nhóm dưới đây)");
        // Nhắc tại chỗ vì đây là nơi model đọc danh sách: nhãn nhóm là phần TRƯỚC dấu hai chấm. Thiếu câu
        // này, model yếu chép nguyên cả dòng ("… : [MỘT PHẦN] — đã biết/còn thiếu: …") vào trường group.
        sb.AppendLine("Nhãn nhóm là phần TRƯỚC dấu hai chấm ở mỗi dòng. Trường `group` chỉ chứa nhãn đó — "
            + "KHÔNG kèm `[MỘT PHẦN]`/`[CHƯA HỎI]`, không kèm phần \"đã biết/còn thiếu\".");
        foreach (var item in pending)
        {
            sb.Append("- ").Append(item.Label).Append(": [").Append(item.Status).Append(']');
            if (!string.IsNullOrWhiteSpace(item.Summary))
                sb.Append(" — đã biết/còn thiếu: ").Append(item.Summary);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(project.DecisionLog))
        {
            sb.AppendLine();
            sb.AppendLine("## Điều người dùng ĐÃ chốt (phương án phải nhất quán với các mục này)");
            sb.AppendLine(project.DecisionLog.Trim());
        }

        if (!string.IsNullOrWhiteSpace(project.PlannedScope))
        {
            sb.AppendLine();
            sb.AppendLine("## Màn hình/tính năng đã dự kiến");
            sb.AppendLine(project.PlannedScope.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("## Hội thoại phỏng vấn tới lúc này");
        sb.AppendLine(transcript);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/gap-proposals.v1.md")),
            new(ChatRole.User, sb.ToString())
        };

        var (callResult, structured) = await _llm.ChatStructuredAsync<GapProposalSet>(
            model, messages, ba.Temperature, new ModelCallLogContext(project.Id, ba, "BAGapProposals"),
            cancellationToken: cancellationToken);

        if (!callResult.IsSuccess)
            return Array.Empty<GapProposal>();

        // Structured output là TÙY CHỌN của từng model (AiModel.SupportsStructuredOutput, mặc định TẮT vì
        // nhiều server OpenAI-compatible tự host từ chối tham số đó) — model chưa bật thì ChatStructuredAsync
        // trả về value null và chỉ có text. Không có nhánh parse tay ở đây, cổng này sẽ không bao giờ chạy
        // được trên chính cấu hình mặc định của app. Cùng cách fallback mà BAChatReplyParser dùng.
        var set = structured ?? ParseFallback(callResult.Content);
        if (set == null)
        {
            _logger?.LogWarning(
                "Gap proposals: model {Model} không trả về JSON đọc được cho dự án {ProjectId}.",
                model.ModelId, project.Id);
            return Array.Empty<GapProposal>();
        }

        var aligned = Align(set.Proposals, pending);
        // Model trả phương án nhưng KHÔNG ghép được nhãn nào là lỗi câm: UI chỉ hiện "chưa soạn được
        // phương án" trong khi log lời gọi vẫn xanh "Success". Ghi lại nhãn để chẩn đoán được từ log.
        if (aligned.Count == 0 && set.Proposals.Count > 0)
            _logger?.LogWarning(
                "Gap proposals: model {Model} trả {Count} phương án nhưng không nhãn nào khớp nhóm còn "
                + "thiếu của dự án {ProjectId}. Nhãn model trả: {ModelGroups}. Nhóm còn thiếu: {PendingGroups}.",
                model.ModelId, set.Proposals.Count, project.Id,
                string.Join(" | ", set.Proposals.Select(p => p.Group)),
                string.Join(" | ", pending.Select(p => p.Label)));

        return aligned;
    }

    private static GapProposalSet? ParseFallback(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try
        {
            var json = JsonExtractor.Extract(raw);
            return string.IsNullOrEmpty(json)
                ? null
                : JsonSerializer.Deserialize<GapProposalSet>(json, JsonDefaults.CaseInsensitive);
        }
        catch
        {
            // Model trả văn xuôi / JSON hỏng: coi như không có phương án nào — người dùng vẫn còn đường
            // trả lời tiếp trong khung chat, y như trước khi có cổng này.
            return null;
        }
    }

    /// <summary>
    /// Ghép các mục model trả về vào ĐÚNG các nhóm đang thiếu: khớp nhãn đã chuẩn hoá (xem
    /// <see cref="LabelKey"/>) trước, rồi mới tới khớp lỏng "một bên chứa bên kia" cho các nhóm còn sót —
    /// vì model yếu hay viết nhãn dài/ngắn hơn bản đồ một chút. Mỗi mục model chỉ dùng cho MỘT nhóm.
    /// Nhóm không được đề xuất thì vắng mặt (cổng sẽ nói rõ còn nhóm nào chưa có phương án) — thà thiếu
    /// còn hơn bịa thêm một dòng để lấp chỗ.
    /// </summary>
    private static List<GapProposal> Align(List<GapProposal> raw, List<CoverageMapItem> pending)
    {
        var candidates = raw
            .Where(p => !string.IsNullOrWhiteSpace(p.Proposal))
            .Select(p => (Item: p, Key: LabelKey(p.Group)))
            .Where(x => x.Key.Length > 0)
            .ToList();

        var taken = new bool[candidates.Count];
        var matches = new GapProposal?[pending.Count];

        // Hai lượt tách rời (không phải "khớp chặt rồi lỏng" cho từng nhóm): mọi nhãn khớp chính xác phải
        // được nhận trước, nếu không một nhãn model viết chung chung có thể bị nhóm đứng trước cướp mất.
        for (var i = 0; i < pending.Count; i++)
        {
            var key = LabelKey(pending[i].Label);
            var index = FindUntaken(candidates, taken, k => k == key);
            if (index >= 0)
            {
                taken[index] = true;
                matches[i] = candidates[index].Item;
            }
        }

        for (var i = 0; i < pending.Count; i++)
        {
            if (matches[i] != null)
                continue;

            var key = LabelKey(pending[i].Label);
            var index = FindUntaken(candidates, taken,
                k => k.Contains(key, StringComparison.Ordinal) || key.Contains(k, StringComparison.Ordinal));
            if (index >= 0)
            {
                taken[index] = true;
                matches[i] = candidates[index].Item;
            }
        }

        var result = new List<GapProposal>();
        for (var i = 0; i < pending.Count; i++)
        {
            var match = matches[i];
            if (match == null)
                continue;

            result.Add(new GapProposal
            {
                // Nhãn LUÔN lấy từ bản đồ, không lấy bản model chép lại: UI ghép dòng tiến độ theo nhãn này.
                Group = pending[i].Label,
                Question = match.Question.Trim(),
                Proposal = match.Proposal.Trim()
            });
        }
        return result;
    }

    private static int FindUntaken(
        List<(GapProposal Item, string Key)> candidates, bool[] taken, Func<string, bool> isMatch)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!taken[i] && isMatch(candidates[i].Key))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Chuẩn hoá nhãn nhóm về dạng so sánh được: bỏ gạch đầu dòng/★, CẮT phần "…: [TRẠNG THÁI] tóm tắt"
    /// nếu model chép nguyên dòng của prompt (gpt-5-nano trả `"Quy trình hiện tại &amp; điểm khó: [MỘT PHẦN]"`
    /// và trước đây rơi hết — cổng báo "chưa soạn được phương án" trong khi lời gọi vẫn thành công), gộp
    /// khoảng trắng, bỏ phân biệt hoa/thường. Nhãn bản đồ không bao giờ chứa ':' hay '[' (xem
    /// <see cref="CoverageMapParser"/>) nên cắt ở dấu đầu tiên là an toàn.
    /// </summary>
    private static string LabelKey(string? label)
    {
        var text = (label ?? string.Empty).Replace("★", string.Empty).Replace('*', ' ').Trim();
        text = text.TrimStart('-', '–', '—', ' ', '\t');

        var cut = text.IndexOfAny([':', '[']);
        if (cut > 0)
            text = text[..cut];

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }
}

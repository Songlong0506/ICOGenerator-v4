using System.Text;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Soạn PHƯƠNG ÁN cho mọi nhóm thông tin còn thiếu của bản đồ bao phủ, trong MỘT lời gọi LLM.
/// <para>
/// Lý do tồn tại: phỏng vấn được thiết kế "mỗi lượt một câu hỏi" và cổng "Write Requirement" chỉ mở khi
/// MỌI nhóm áp dụng đã [RÕ] — hai điều đúng về chất lượng nhưng cộng lại thành hàng chục lượt chat, và
/// người dùng nghiệp vụ bận thì rời bỏ giữa chừng chứ không có đường tăng tốc nào. Cổng này đổi N lượt
/// hỏi lẻ thành MỘT lượt duyệt: BA đề xuất sẵn, người dùng gật đầu từng dòng hoặc đổi lại.
/// </para>
/// <para>
/// Bản đầu tiên của cổng có một lỗ hổng làm nó CHẬM HƠN chat: hỏi ngay sau câu trả lời đầu tiên, model
/// vẫn trả về 11 phương án nghe rất chắc chắn dù không có gì để dựa vào, UI chọn sẵn tất cả, và người
/// dùng phải gõ đè gần như từng dòng. Nay mỗi phương án phải tự khai <c>confidence</c> + <c>basis</c>
/// (xem <see cref="GapProposal"/>), và service này CHUẨN HOÁ lại lời khai đó bằng luật tất định
/// (<see cref="Ground"/>): không trích được căn cứ ⇒ hạ xuống phỏng đoán, bất kể model viết gì. UI chỉ
/// chọn sẵn phần suy ra, nên chi phí của một phương án bịa rơi về gần 0 — người dùng lướt qua, không
/// phải sửa.
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
    // Lựa chọn THAY THẾ: đúng 2, vì UI hiện MỘT câu hỏi chọn-một với ba gợi ý đồng hạng — phương án ở
    // `Proposal` là gợi ý đầu, hai mục này là hai gợi ý còn lại (rồi tới ô "Ý khác" để tự nhập). Nhiều
    // hơn thì người dùng phải ĐỌC một danh sách thay vì liếc rồi bấm, mất đúng cái lợi của cổng này.
    private const int MaxOptions = 2;
    private const int MaxOptionChars = 120;
    // Căn cứ ngắn hơn ngần này là chữ đệm ("người dùng đã nói", "theo hội thoại") chứ không phải trích dẫn.
    private const int MinBasisChars = 8;
    private const int MinBasisWords = 3;
    private const int MinEvidenceMatchPercent = 70;

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
    /// Trả về một phương án cho mỗi nhóm <c>[CHƯA HỎI]</c>/<c>[MỘT PHẦN]</c> của bản đồ, mỗi phương án
    /// đã được dán nhãn "suy ra" hay "phỏng đoán" theo luật tất định. Danh sách rỗng khi không còn nhóm
    /// nào thiếu, hoặc khi lời gọi LLM lỗi (fail-open — người dùng vẫn chat như cũ). Các mục model trả
    /// về mà không khớp nhóm nào đang thiếu đều bị loại: cổng này chỉ được lấp đúng những ô bản đồ đang
    /// trống, không phải mở đường cho model tự thêm chủ đề.
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

        // Nhắc TẤT ĐỊNH về lượng dữ kiện đang có: model không tự cảm được "hội thoại này mới có 1 lượt",
        // nó chỉ thấy một transcript và mặc định viết như thể đã hiểu dự án. Đưa thẳng con số vào prompt
        // là cách rẻ nhất kéo nó về đúng tỉ lệ suy-ra/phỏng-đoán ở lượt hỏi sớm — đúng lúc cổng này dễ
        // biến thành một trang toàn phương án bịa nhất.
        var userTurns = conversations.Count(c => c.Role == "user" && !string.IsNullOrWhiteSpace(c.Message));
        var allGroups = CoverageMapParser.Parse(project.RequirementCoverageMap);
        sb.AppendLine("## Lượng dữ kiện đang có");
        sb.AppendLine($"Người dùng mới nói {userTurns} lượt; bản đồ bao phủ có {allGroups.Count(x => x.Status == "RÕ")}/{allGroups.Count} nhóm đã [RÕ]. "
            + "Số mục bạn dán nhãn \"suy-ra\" không được vượt quá số điều họ THẬT SỰ đã nói — phần còn lại là \"phỏng-đoán\", và như vậy là đúng.");
        sb.AppendLine();

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
            new(ChatRole.System, _prompts.Get("BusinessAnalyst/gap-proposals.v2.md")),
            new(ChatRole.User, sb.ToString())
        };

        var (callResult, structured) = await _llm.ChatStructuredAsync<GapProposalSet>(
            model, messages, ba.Temperature, new ModelCallLogContext(project.Id, ba, "BAGapProposals"),
            cancellationToken: cancellationToken);

        if (!callResult.IsSuccess)
            return Array.Empty<GapProposal>();

        // Structured output là TÙY CHỌN của từng model (AiModel.StructuredOutputMode, mặc định None vì nhiều
        // server OpenAI-compatible tự host từ chối tham số response_format) — model để None thì
        // ChatStructuredAsync trả về value null và chỉ có text. Không có nhánh parse tay ở đây, cổng này sẽ
        // không bao giờ chạy được trên chính cấu hình mặc định của app. Cùng cách fallback mà BAChatReplyParser dùng.
        var set = structured ?? ParseFallback(callResult.Content);
        if (set == null)
        {
            _logger?.LogWarning(
                "Gap proposals: model {Model} không trả về JSON đọc được cho dự án {ProjectId}.",
                model.ModelId, project.Id);
            return Array.Empty<GapProposal>();
        }

        // Kho DỮ KIỆN dùng để chấm lại lời khai "suy-ra": đúng những gì model được đọc — hội thoại (đã
        // cắt như khi gửi đi), điều đã chốt, màn hình dự kiến. Một trích dẫn bịa gần như không dùng lại
        // được chữ của người dùng, nên phép đối chiếu này bắt được đúng ca nguy hiểm nhất.
        var evidence = WordsOf(transcript, project.DecisionLog, project.PlannedScope);
        var aligned = Align(set.Proposals, pending, evidence);
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

    // Model trả văn xuôi / JSON hỏng: coi như không có phương án nào — người dùng vẫn còn đường trả lời
    // tiếp trong khung chat, y như trước khi có cổng này.
    private static GapProposalSet? ParseFallback(string? raw) => LlmJson.TryDeserialize<GapProposalSet>(raw);

    /// <summary>
    /// Ghép các mục model trả về vào ĐÚNG các nhóm đang thiếu: khớp nhãn đã chuẩn hoá (xem
    /// <see cref="LabelKey"/>) trước, rồi mới tới khớp lỏng "một bên chứa bên kia" cho các nhóm còn sót —
    /// vì model yếu hay viết nhãn dài/ngắn hơn bản đồ một chút. Mỗi mục model chỉ dùng cho MỘT nhóm.
    /// Nhóm không được đề xuất thì vắng mặt (cổng sẽ nói rõ còn nhóm nào chưa có phương án) — thà thiếu
    /// còn hơn bịa thêm một dòng để lấp chỗ.
    /// </summary>
    private static List<GapProposal> Align(List<GapProposal> raw, List<CoverageMapItem> pending, HashSet<string> evidence)
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

            var basis = Ground(match, evidence, out var grounded);
            result.Add(new GapProposal
            {
                // Nhãn LUÔN lấy từ bản đồ, không lấy bản model chép lại: UI ghép dòng tiến độ theo nhãn này.
                Group = pending[i].Label,
                Question = match.Question.Trim(),
                Proposal = match.Proposal.Trim(),
                Confidence = grounded ? GapProposal.Grounded : GapProposal.Guess,
                Basis = grounded ? basis : string.Empty,
                Options = CleanOptions(match.Options, match.Proposal)
            });
        }
        return result;
    }

    /// <summary>
    /// Chốt TẤT ĐỊNH một phương án là "suy ra" hay "phỏng đoán" — không tin lời tự khai của model.
    /// "Suy ra" phải KIẾM ĐƯỢC bằng bốn điều kiện: model khai vậy, căn cứ đủ dài để là một trích dẫn chứ
    /// không phải chữ đệm ("theo hội thoại"), không phải bản chép lại chính phương án (mẹo vòng tròn quen
    /// thuộc khi model bị bắt điền một trường bắt buộc), và — điều thật sự chặn được bịa — phần lớn chữ
    /// trong căn cứ phải THẬT SỰ có trong hội thoại/điều đã chốt. Trích dẫn bịa hầu như không bao giờ
    /// dùng lại được chữ của người dùng.
    /// <para>
    /// Nghiêng hẳn về phía "phỏng đoán" là có chủ đích: một phương án ĐÚNG bị gắn nhãn phỏng đoán chỉ tốn
    /// của người dùng một cú bấm, còn một phương án BỊA đội lốt "suy ra" thì được chọn sẵn và đi thẳng
    /// vào hội thoại như lời của chính họ — hai sai lầm không cùng giá.
    /// </para>
    /// </summary>
    private static string Ground(GapProposal match, HashSet<string> evidence, out bool grounded)
    {
        var basis = Collapse(match.Basis);
        grounded = ClaimsGrounded(match.Confidence)
            && basis.Length >= MinBasisChars
            && !string.Equals(basis, Collapse(match.Proposal), StringComparison.OrdinalIgnoreCase)
            && EchoesEvidence(basis, evidence);
        return basis;
    }

    // Căn cứ phải vang lại chữ của người dùng: ít nhất MinBasisWords chữ có nghĩa và ít nhất
    // MinEvidenceMatchPercent% trong số đó xuất hiện trong kho dữ kiện. Ngưỡng đặt cao (không phải quá
    // bán) vì tiếng Việt đầy hư từ ("và", "của", "khi") vốn có mặt trong mọi hội thoại — quá bán là mức
    // mà một câu bịa vẫn dễ dàng đạt được.
    private static bool EchoesEvidence(string basis, HashSet<string> evidence)
    {
        var words = Tokenize(basis).Where(w => w.Length >= 2).ToList();
        if (words.Count < MinBasisWords)
            return false;

        var known = words.Count(evidence.Contains);
        return known * 100 >= words.Count * MinEvidenceMatchPercent;
    }

    private static HashSet<string> WordsOf(params string?[] sources)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var word in Tokenize(source))
                set.Add(word);
        }
        return set;
    }

    // Tách chữ theo ký tự chữ/số nên dấu tiếng Việt được giữ nguyên (chúng là chữ cái) — so trần sau khi
    // hạ hoa/thường. Đủ cho phép đối chiếu "chữ này có trong hội thoại không".
    private static IEnumerable<string> Tokenize(string? text)
    {
        var word = new StringBuilder();
        foreach (var ch in (text ?? string.Empty).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                word.Append(ch);
                continue;
            }

            if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
            yield return word.ToString();
    }

    // Lời tự khai được đọc lỏng: prompt yêu cầu đúng chữ "suy-ra" nhưng model viết "suy ra", "SUY-RA",
    // và bản tiếng Anh ("grounded"/"derived") khi bị đẩy sang ngôn ngữ khác. Nhận diện lỏng ở đây không
    // hạ chuẩn — căn cứ vẫn phải qua được vòng kiểm tất định ở Ground.
    private static bool ClaimsGrounded(string? confidence)
    {
        var text = (confidence ?? string.Empty).Trim().ToLowerInvariant();
        return text.Contains("suy", StringComparison.Ordinal)
            || text.Contains("ground", StringComparison.Ordinal)
            || text.Contains("derived", StringComparison.Ordinal);
    }

    // Lựa chọn bấm-một-cái: bỏ rỗng/trùng (kể cả trùng chính phương án — chọn nó thì chẳng đổi gì), cắt
    // dài, giữ tối đa MaxOptions. So trùng bỏ hoa/thường + khoảng trắng thừa vì model hay trả lại đúng
    // câu proposal chỉ khác dấu chấm cuối.
    private static List<string> CleanOptions(List<string>? options, string? proposal)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Collapse(proposal).TrimEnd('.') };

        foreach (var option in options ?? new List<string>())
        {
            var text = Collapse(option);
            if (text.Length == 0)
                continue;
            if (text.Length > MaxOptionChars)
                text = text[..MaxOptionChars].TrimEnd() + "…";
            if (!seen.Add(text.TrimEnd('.')))
                continue;

            result.Add(text);
            if (result.Count == MaxOptions)
                break;
        }
        return result;
    }

    private static string Collapse(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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

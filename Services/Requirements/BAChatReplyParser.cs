using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

// Biến raw text BA trả về thành (Message + Suggestions + Questions). BA được nhắc trả JSON
// {"message": "...", "suggestions": ["...", ...]} cho lượt hỏi MỘT câu và
// {"message": "...", "questions": [{...}, ...]} cho lượt hỏi GỘP, để UI render chip / thẻ nhiều dòng.
// Mô hình local không phải lúc nào cũng tuân thủ JSON, nên parser luôn fallback an toàn về text thuần
// (không chip) — đúng bằng hành vi cũ — thay vì ném lỗi.
public class BAChatReplyParser
{
    // Giữ số chip vừa phải để không tràn UI, và bỏ "gợi ý" quá dài (model lỡ nhét cả đoạn văn).
    private const int MaxSuggestions = 6;
    private const int MaxSuggestionLength = 200;

    // TRẦN CỨNG số câu hỏi một lượt gộp. Đây là cái phanh TẤT ĐỊNH của cả tính năng, không phải một con
    // số cho đẹp: prompt nói "chỉ gộp câu độc lập, tối đa 4", nhưng model luôn có xu hướng gộp tối đa để
    // "xong sớm" — mà một lượt 8 câu hỏi thì đúng bằng cổng "chốt nhanh" cũ đội lốt phỏng vấn, tức là
    // lấp đầy bản đồ bao phủ bằng một màn bấm chip. Prompt định hướng; con số này mới là thứ chặn.
    private const int MaxQuestions = 4;
    private const int MaxQuestionLength = 300;

    public BAChatReply Parse(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
            return new BAChatReply();

        // JSON hỏng/thiếu/không đúng dạng → parsed null → rơi xuống fallback text thuần bên dưới.
        if (LlmJson.TryDeserialize<RawReply>(text) is { } parsed)
        {
            var reply = new BAChatReply
            {
                Message = (parsed.Message ?? string.Empty).Trim(),
                Suggestions = CleanSuggestions(parsed.Suggestions),
                MultiSelect = parsed.MultiSelect == true,
                OpenEnded = parsed.OpenEnded == true,
                Questions = ToQuestions(parsed.Questions),
                FlowDiagram = parsed.FlowDiagram ?? new List<FlowStep>()
            };

            // Có cấu trúc rõ ràng (message, suggestions hoặc questions) → dùng kết quả parse.
            if (reply.Message.Length > 0 || reply.Suggestions.Count > 0 || reply.Questions.Count > 0)
                return Normalize(reply);
        }

        // Fallback: coi toàn bộ phản hồi là text hiển thị, không kèm chip (giống hành vi trước đây).
        return new BAChatReply { Message = text };
    }

    /// <summary>
    /// Áp MỌI trần và quy tắc chuẩn hoá lên một lượt trả lời đã có cấu trúc. Tách riêng khỏi
    /// <see cref="Parse"/> vì đường structured output KHÔNG đi qua parser: model trả thẳng
    /// <see cref="BAChatReply"/>, nên nếu chỉ chặn trong Parse thì trần "tối đa 4 câu hỏi một lượt" —
    /// cái phanh duy nhất giữ lượt gộp khỏi biến thành một màn bấm chip lấp bản đồ — sẽ vắng mặt ở đúng
    /// đường đi mặc định của các model tốt.
    /// </summary>
    public BAChatReply Normalize(BAChatReply reply)
    {
        reply.Message = (reply.Message ?? string.Empty).Trim();
        reply.Suggestions = CleanSuggestionTexts(reply.Suggestions);
        reply.Questions = CleanQuestions(reply.Questions);
        reply.FlowDiagram = CleanFlow(reply.FlowDiagram);

        // Model trả ĐÚNG MỘT câu trong `questions` (lẽ ra phải dùng đường một-câu): hạ về đường cũ thay
        // vì dựng một thẻ nhiều dòng chỉ có một dòng. Câu hỏi phải được NỐI vào message — message của
        // lượt gộp chỉ là câu dẫn, bỏ nó đi là mất luôn điều BA vừa hỏi.
        if (reply.Questions.Count == 1)
        {
            var only = reply.Questions[0];
            reply.Message = MergeSingleQuestion(reply.Message, only.Question);
            if (reply.Suggestions.Count == 0)
            {
                reply.Suggestions = only.Suggestions;
                reply.MultiSelect = only.MultiSelect;
                reply.OpenEnded = only.OpenEnded;
            }
            reply.Questions = new List<BAChatQuestion>();
        }

        // CÂU MỞ ⇒ KHÔNG chip. Áp SAU bước hạ lượt-gộp-một-câu ở trên để cờ vừa thừa kế từ câu hỏi đó
        // cũng đi qua đây, và TRƯỚC mọi xử lý multiSelect bên dưới (bộ chip đã rỗng thì không còn gì để
        // xét hình dạng). Xem BAChatQuestion.OpenEnded: ở lượt một câu, bấm chip là GỬI NGAY, nên một
        // hàng chip đặt dưới câu hỏi mở không phải lối tắt mà là lối cụt — người dùng bấm xong là mất
        // lượt kể, còn hệ thống thì ghi mẩu bốn chữ đó vào bản đồ bao phủ như câu trả lời thật.
        reply.OpenEnded = reply.Questions.Count == 0 && (reply.OpenEnded || LooksOpenEnded(reply.Message));
        if (reply.OpenEnded)
            reply.Suggestions = new List<string>();

        if (reply.Message.Length == 0 && (reply.Suggestions.Count > 0 || reply.Questions.Count > 0))
            reply.Message = "Đã ghi nhận. Bạn có thể chọn một gợi ý bên dưới hoặc tự nhập thêm.";

        // multiSelect chỉ có nghĩa khi thực sự có chip để chọn, VÀ bộ chip phải đúng hình dạng liệt kê.
        reply.MultiSelect = reply.Suggestions.Count > 0 && reply.MultiSelect && IsEnumerationSet(reply.Suggestions);

        // Lượt GỘP không dùng chip lượt-đơn: mỗi câu hỏi đã có hàng gợi ý riêng trên thẻ. Để cả hai cùng
        // sống thì màn hình có hai chỗ trả lời cho cùng một lượt, và chip lượt-đơn (bấm là GỬI NGAY) sẽ
        // cướp lượt trước khi người dùng kịp trả lời các câu còn lại.
        if (reply.Questions.Count > 0)
        {
            reply.Suggestions = new List<string>();
            reply.MultiSelect = false;
        }

        return reply;
    }

    // Nối câu hỏi vào câu dẫn khi hạ một lượt "gộp" một-câu về đường một-câu. Bỏ qua nếu câu dẫn đã chứa
    // sẵn câu hỏi (model hay lặp lại), để người dùng không đọc cùng một câu hai lần.
    private static string MergeSingleQuestion(string message, string question)
    {
        if (question.Length == 0)
            return message;
        if (message.Length == 0)
            return question;
        return message.Contains(question, StringComparison.OrdinalIgnoreCase)
            ? message
            : $"{message}\n\n{question}";
    }

    private static List<BAChatQuestion> ToQuestions(List<RawQuestion>? raw) =>
        (raw ?? new List<RawQuestion>())
            .Where(q => q != null)
            .Select(q => new BAChatQuestion
            {
                Group = q.Group ?? string.Empty,
                Question = q.Question ?? string.Empty,
                Suggestions = CleanSuggestions(q.Suggestions),
                MultiSelect = q.MultiSelect == true,
                OpenEnded = q.OpenEnded == true
            })
            .ToList();

    // Lọc danh sách câu hỏi gộp: bỏ câu rỗng/quá dài, khử trùng lặp, cắt ở trần cứng. Câu hỏi KHÔNG có
    // gợi ý vẫn giữ (UI luôn có ô tự nhập) — prompt bắt buộc kèm gợi ý, nhưng thiếu gợi ý thì hỏng một
    // tiện ích, còn bỏ cả câu hỏi thì mất một điểm khai thác.
    private static List<BAChatQuestion> CleanQuestions(List<BAChatQuestion>? raw)
    {
        var result = new List<BAChatQuestion>();
        if (raw == null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in raw)
        {
            if (item == null)
                continue;

            var question = (item.Question ?? string.Empty).Trim();
            if (question.Length == 0 || question.Length > MaxQuestionLength || !seen.Add(question))
                continue;

            // Câu MỞ trên thẻ gộp: bỏ chip, để UI mở sẵn ô tự nhập cho riêng dòng đó. Ở đây bấm chip
            // KHÔNG gửi ngay (thẻ gộp gom cả cụm rồi mới gửi) nên cái giá nhẹ hơn lượt một-câu, nhưng
            // vẫn là cái giá cũ: chip trả lời được một mẩu thì người dùng bấm mẩu đó rồi đi tiếp, và
            // phần còn lại của câu hỏi không bao giờ được hỏi lại — bản đồ bao phủ đã tính là đã hỏi.
            var suggestions = CleanSuggestionTexts(item.Suggestions);
            var openEnded = item.OpenEnded || LooksOpenEnded(question);
            if (openEnded)
                suggestions = new List<string>();

            result.Add(new BAChatQuestion
            {
                Group = (item.Group ?? string.Empty).Trim(),
                Question = question,
                Suggestions = suggestions,
                MultiSelect = suggestions.Count > 0 && item.MultiSelect && IsEnumerationSet(suggestions),
                OpenEnded = openEnded
            });

            if (result.Count >= MaxQuestions)
                break;
        }

        return result;
    }

    private static List<string> CleanSuggestions(List<JsonElement>? raw) =>
        CleanSuggestionTexts((raw ?? new List<JsonElement>()).Select(ExtractText));

    private static List<string> CleanSuggestionTexts(IEnumerable<string?>? raw)
    {
        var result = new List<string>();
        if (raw == null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in raw)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var text = value.Trim();
            if (text.Length > MaxSuggestionLength || !seen.Add(text))
                continue;

            result.Add(text);
            if (result.Count >= MaxSuggestions)
                break;
        }

        return result;
    }

    // ==== HÌNH DẠNG BỘ CHIP phải khớp với cờ multiSelect ====
    // Một bộ gợi ý chỉ thuộc đúng MỘT trong hai kiểu, và cờ multiSelect là thứ nói người dùng đang ở kiểu
    // nào:
    //   - PHƯƠNG ÁN THAY THẾ — mỗi chip là một câu trả lời trọn vẹn, chọn cái này là loại cái kia
    //     ("Nhân viên sửa rồi gửi lại" / "Hủy hẳn đơn") ⇒ chọn MỘT.
    //   - LIỆT KÊ THÀNH PHẦN — câu trả lời thật là một danh sách, mỗi chip là MỘT MẢNH của danh sách đó
    //     ("Nhân viên" / "Manager orgUnit" / "HoD phòng ban") ⇒ chọn NHIỀU.
    //
    // Lỗi model hay mắc là trộn hai kiểu: giữ chip dạng GÓI (kiểu một) nhưng bật multiSelect (kiểu hai) —
    // vd hỏi "gồm những vai trò nào?" với chip ["Nhân viên và HR/đào tạo", "Nhân viên, quản lý và HR",
    // "Thêm HoD phòng ban", "Chỉ bộ phận HR/đào tạo"]. Bốn chip đó là bốn gói LỒNG NHAU và phủ định nhau,
    // nên UI cho tích ô 1 + ô 4 cùng lúc và cái gửi đi là một câu trả lời tự mâu thuẫn — rồi được chắt
    // thẳng vào bản đồ bao phủ và "Điều đã chốt", nơi không tầng nào phía sau còn phân biệt được nữa.
    //
    // Ba dấu hiệu dưới đây đều nói "chip này là một PHƯƠNG ÁN, không phải một mảnh", và đều nhận diện
    // được bằng máy. Prompt dạy cách viết chip nguyên tử; hàm này là cái phanh khi prompt bị trượt.
    //
    // Hướng sửa CHỈ MỘT CHIỀU — hạ multiSelect về false, không bao giờ tự bật lên. Hạ nhầm thì người dùng
    // mất tiện ích tích nhiều ô (vẫn bấm được một chip, vẫn tự nhập được); bật nhầm thì sinh ra một câu
    // trả lời vô nghĩa mà mọi bước sau tin là điều người dùng đã nói. Hai cái giá không cùng hạng.
    private static bool IsEnumerationSet(IReadOnlyList<string> suggestions)
    {
        // Một chip thì không có gì để "chọn nhiều"; kiểu liệt kê cần ít nhất hai mảnh.
        if (suggestions.Count < 2)
            return false;

        foreach (var suggestion in suggestions)
        {
            var text = suggestion.Trim().ToLowerInvariant();

            // (1) Chip LOẠI TRỪ: tự nó đã bao hàm hoặc phủ định phần còn lại, nên tích kèm chip khác là
            //     mâu thuẫn ("Chỉ bộ phận HR", "Tất cả nhân viên", "Không cần thông báo").
            if (ExclusiveChipPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)))
                return false;

            // (2) Chip KHÔNG TỰ ĐỨNG: chỉ có nghĩa khi đọc kèm chip khác ("Thêm HoD phòng ban" — thêm vào
            //     cái gì?). Tích riêng nó thì câu gửi đi không đủ nghĩa.
            if (DependentChipPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)))
                return false;

            // (3) Chip GÓI: nêu từ hai thứ trở lên trong một dòng ⇒ đó là một phương án đã lắp sẵn, không
            //     phải một mảnh. Dấu "/" KHÔNG tính (thường là một tên: "HR/đào tạo", "TEF3.3/LL06").
            if (BundleSeparators.Any(s => text.Contains(s, StringComparison.Ordinal)))
                return false;
        }

        return true;
    }

    private static readonly string[] ExclusiveChipPrefixes =
        { "chỉ ", "duy nhất ", "tất cả ", "toàn bộ ", "mọi ", "không ", "chưa " };

    private static readonly string[] DependentChipPrefixes =
        { "thêm ", "cả ", "kèm ", "ngoài ra", "như trên", "và ", "hoặc ", "vừa " };

    private static readonly string[] BundleSeparators = { " và ", " & ", " + ", ", ", "; " };

    // ==== CÂU HỎI MỞ: cái phanh khi prompt bị trượt ====
    // Prompt dạy BA tự đánh dấu `openEnded` cho câu xin lời kể/mô tả. Hàm này bắt đúng ca mà model trượt
    // nhiều nhất và cũng đắt nhất: nó XIN một câu chuyện rồi vẫn kèm hàng chip, vì luật cũ bắt "mọi câu
    // hỏi đều phải có gợi ý". Chip lúc đó chỉ trả lời được một mẩu, mà bấm chip ở lượt một-câu là gửi
    // ngay ⇒ câu chuyện không bao giờ được kể, còn bản đồ bao phủ thì tính là nhóm đó đã hỏi xong.
    //
    // Nhận diện bằng CỤM TỪ, không bằng từ đơn: "kể" đứng một mình còn nằm trong "kể cả", "kể từ", và
    // "thế nào"/"ra sao" thì phần lớn là câu đóng có phương án rõ ("nếu đơn bị từ chối thì xử lý thế
    // nào?" — chip ở đó là các phương án trọn vẹn, rất đáng giữ). Danh sách dưới đây cố tình HẸP: nó
    // chặn ca chắc chắn sai, phần còn lại để prompt lo.
    //
    // Hướng sửa CHỈ MỘT CHIỀU — chỉ bật `openEnded` lên, không bao giờ tắt cờ BA đã đặt. Bật nhầm thì
    // người dùng mất tiện ích bấm chip ở một câu (vẫn trả lời được, chỉ phải gõ); bỏ sót thì sinh ra một
    // câu trả lời cụt mà mọi tầng sau tin là lời người dùng. Hai cái giá không cùng hạng — đúng cùng
    // nguyên tắc với việc hạ `multiSelect` ở trên.
    private static bool LooksOpenEnded(string? text)
    {
        var value = (text ?? string.Empty).ToLowerInvariant();
        if (value.Length == 0)
            return false;

        // Không có dấu hỏi thì lượt này nhiều khả năng không phải câu hỏi (tóm tắt, lời mời bấm nút) —
        // đánh dấu "câu mở" ở đó chỉ làm UI mời người dùng kể vào chỗ không ai hỏi gì.
        if (!value.Contains('?', StringComparison.Ordinal))
            return false;

        return NarrativeCues.Any(cue => value.Contains(cue, StringComparison.Ordinal));
    }

    private static readonly string[] NarrativeCues =
    {
        "kể giúp", "kể cho", "kể lại", "kể một", "kể qua", "kể xem",
        "mô tả", "nói rõ hơn", "giải thích giúp", "diễn giải",
        "walk me through", "tell me about", "describe "
    };

    // Chấp nhận cả ["a","b"] lẫn [{"label":"a"},{"text":"b"}] để bền với cách model trả khác nhau.
    private static string? ExtractText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.ToString(),
        JsonValueKind.Object => FirstStringProperty(element, "label", "text", "value", "title", "option"),
        _ => null
    };

    private static string? FirstStringProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return null;
    }

    // Trần số bước để một model "hào phóng" không đổ cả kịch bản dài vào sơ đồ.
    private const int MaxFlowSteps = 12;

    private static List<FlowStep> CleanFlow(List<FlowStep>? raw)
    {
        var result = new List<FlowStep>();
        if (raw == null)
            return result;

        foreach (var step in raw)
        {
            var action = (step.Action ?? string.Empty).Trim();
            if (action.Length == 0)
                continue; // bước không có hành động thì vô nghĩa.
            result.Add(new FlowStep
            {
                Actor = (step.Actor ?? string.Empty).Trim(),
                Action = action,
                Outcome = (step.Outcome ?? string.Empty).Trim()
            });
            if (result.Count >= MaxFlowSteps)
                break;
        }
        return result;
    }

    private class RawReply
    {
        public string? Message { get; set; }
        public List<JsonElement>? Suggestions { get; set; }
        public bool? MultiSelect { get; set; }
        public bool? OpenEnded { get; set; }
        public List<RawQuestion>? Questions { get; set; }
        public List<FlowStep>? FlowDiagram { get; set; }
    }

    // Shape thô của một câu hỏi trong lượt gộp. Suggestions để JsonElement như RawReply để dùng chung
    // CleanSuggestions — model trả cả ["a"] lẫn [{"label":"a"}] đều nuốt được.
    private class RawQuestion
    {
        public string? Group { get; set; }
        public string? Question { get; set; }
        public List<JsonElement>? Suggestions { get; set; }
        public bool? MultiSelect { get; set; }
        public bool? OpenEnded { get; set; }
    }
}

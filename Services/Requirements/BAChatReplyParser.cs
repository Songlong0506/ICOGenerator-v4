using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

// Biến raw text BA trả về thành (Message + Suggestions + Questions). BA được nhắc trả JSON
// {"message": "...", "suggestions": ["...", ...]} cho lượt hỏi MỘT câu và
// {"message": "...", "questions": [{...}, ...]} cho lượt hỏi GỘP, để UI render chip / thẻ nhiều dòng.
// Mô hình local không phải lúc nào cũng tuân thủ JSON, nên parser luôn fallback an toàn về text thuần
// (không chip) — đúng bằng hành vi cũ — thay vì ném lỗi.
//
// RANH GIỚI: parser KHÔNG phán đoán hình dạng bộ chip. `suggestions` và `multiSelect` của model lên thẳng
// màn hình; thứ còn lại ở đây chỉ là các phép dọn không đoán ngữ nghĩa (chip rỗng/trùng/quá dài, trần 6
// chip, trần 4 câu, phép đếm "dưới hai chip thì không chọn-nhiều") cộng đúng MỘT phép xoá — chip "khác"
// trần, xoá được vì thứ bị xoá đã có sẵn ở ô "Ý khác" ngay dưới hàng chip. Trước đây có thêm `ShapeAnswer`
// đọc câu hỏi bằng các bảng cụm từ tiếng Việt rồi tự bật/hạ `multiSelect`, và xoá sạch hàng chip khi cho
// là không render đúng được, và `LooksOpenEnded` đọc câu hỏi bằng một bảng cụm từ khác rồi tự bật
// `openEnded` (cũng là xoá chip); cả hai đã bị gỡ vì đoán sai là mất trắng chip của một lượt — xem
// docs/requirement-flow.md, mục "Hình dạng bộ chip do PROMPT giữ", và ghi chú "CÂU HỎI MỞ" ở cuối file.
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
                SummaryCheck = parsed.SummaryCheck == true,
                Questions = ToQuestions(parsed.Questions),
                // Bảng phân quyền đi thẳng, KHÔNG cắt gọt ở đây: bản chuẩn hoá của nó cần biết phạm vi đã
                // chắt của dự án (để loại màn hình bịa và bù màn hình bị bỏ quên) mà parser thì không có —
                // xem PermissionMatrixBuilder.Build, gọi từ BAChatService.
                PermissionMatrix = parsed.PermissionMatrix ?? new List<PermissionMatrixRow>()
            };

            // Có cấu trúc rõ ràng (message, suggestions hoặc questions) → dùng kết quả parse.
            if (reply.Message.Length > 0 || reply.Suggestions.Count > 0 || reply.Questions.Count > 0)
                return Normalize(reply);
        }

        // Phản hồi CÓ HÌNH DẠNG JSON mà không đọc nổi (dãy thoát hỏng mà LlmJson cũng chữa không xong,
        // chuỗi bị cắt giữa chừng vì chạm trần token, dấu " không escape...): vớt lấy phần `message` rồi
        // đi tiếp như một lượt bình thường. Nhánh này KHÔNG bao giờ được rơi về "coi cả khối là text":
        // ca thật (dự án JD Libary, lượt 6) là nguyên khối `{"message":"C\u1EA3m \u01A1n…","ready":false}`
        // hiện lên khung chat như một lượt trả lời của BA — người dùng đọc phải sổ sách của hệ thống, còn
        // các tầng sau (chắt bản đồ bao phủ, nhật ký điều đã chốt) thì đọc nó như lời BA nói ra.
        // Vớt không được thì trả lượt RỖNG: BAChatService có sẵn chốt chặn cho lượt câm (nó thay bằng
        // bước kế tiếp tất định suy từ bản đồ bao phủ), và một câu hỏi khô cứng vẫn hơn một khối JSON.
        if (LooksLikeJsonObject(text))
            return Normalize(new BAChatReply { Message = SalvageMessage(text) });

        // Fallback: coi toàn bộ phản hồi là text hiển thị, không kèm chip (giống hành vi trước đây).
        return new BAChatReply { Message = text };
    }

    private static bool LooksLikeJsonObject(string text)
        => text.StartsWith('{') || text.StartsWith("```", StringComparison.Ordinal);

    /// <summary>
    /// Đọc phần GIÁ TRỊ của trường <c>message</c> ra khỏi một khối JSON hỏng, khoan dung hết mức: bỏ hàng
    /// rào ```, unescape được tới đâu hay tới đó, dãy `\u` hỏng thì bỏ qua, chuỗi chưa đóng thì lấy tới
    /// hết. Chuỗi rỗng nghĩa là không vớt được gì.
    ///
    /// <para>
    /// Dùng lại <see cref="BAChatTokenFilter"/> chứ không viết máy trạng thái thứ hai: nó vốn đã làm đúng
    /// việc này để stream "BA đang gõ", và nó cố ý khoan dung (không nhận ra định dạng thì im lặng, không
    /// bao giờ ném). Một bộ luật, một chỗ sửa — và bản xem trước lúc gõ với bản chốt lúc lưu không thể
    /// nói hai điều khác nhau về cùng một phản hồi hỏng.
    /// </para>
    /// </summary>
    private static string SalvageMessage(string text)
    {
        var salvaged = new StringBuilder();
        new BAChatTokenFilter(chunk => salvaged.Append(chunk)).Feed(text);
        return salvaged.ToString().Trim();
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

        // CÂU BẮT BUỘC HỎI MỘT MÌNH lỡ bị gộp ⇒ lượt này chỉ còn ĐÚNG câu đó, các câu đi kèm bị bỏ.
        // Bỏ đi là bỏ đúng những câu RẺ nhất: chúng thuộc các nhóm rời nhau, bản đồ bao phủ chưa nhúc
        // nhích vì chúng, nên lượt sau hỏi lại không mất gì. Còn câu đào ngoại lệ thì ngược lại — mỗi câu
        // trả lời của nó mở ra một nhánh mới mà BA phải nghe xong mới biết hỏi tiếp gì, và ở lượt gộp nó
        // luôn bị rút gọn thành một cặp chip có/không đóng luôn cả nhóm (xem InterviewQuestionRules).
        // Giữ câu đắt, bỏ câu rẻ: lượt tự rơi về đường một-câu ở bước hạ cấp ngay bên dưới.
        if (reply.Questions.Count > 1
            && reply.Questions.FirstOrDefault(q => InterviewQuestionRules.MustAskAlone(q.Group)) is { } alone)
            reply.Questions = new List<BAChatQuestion> { alone };

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

        // CÂU MỞ ⇒ KHÔNG chip. Cờ do MODEL đặt (xem ghi chú "CÂU HỎI MỞ" ở cuối file: parser không còn tự
        // đoán). Áp SAU bước hạ lượt-gộp-một-câu ở trên để cờ vừa thừa kế từ câu hỏi đó cũng đi qua đây,
        // và TRƯỚC mọi xử lý multiSelect bên dưới (bộ chip đã rỗng thì không còn gì để xét hình dạng).
        // Xem BAChatQuestion.OpenEnded: ở lượt một câu, bấm chip là GỬI NGAY, nên một hàng chip đặt dưới
        // câu hỏi mở không phải lối tắt mà là lối cụt — người dùng bấm xong là mất lượt kể, còn hệ thống
        // thì ghi mẩu bốn chữ đó vào bản đồ bao phủ như câu trả lời thật.
        reply.OpenEnded = reply.Questions.Count == 0 && reply.OpenEnded;
        if (reply.OpenEnded)
            reply.Suggestions = new List<string>();

        if (reply.Message.Length == 0 && (reply.Suggestions.Count > 0 || reply.Questions.Count > 0))
            reply.Message = "Đã ghi nhận. Bạn có thể chọn một gợi ý bên dưới hoặc tự nhập thêm.";

        // Cờ chọn-nhiều đi thẳng từ model. Chỉ còn một phép kiểm ĐẾM, không đoán ngữ nghĩa: dưới hai chip
        // thì không có gì để "chọn nhiều", và bật cờ ở đó dựng ra một hàng tick kèm nút "Gửi các lựa chọn"
        // cho đúng một ô.
        if (reply.Suggestions.Count < 2)
            reply.MultiSelect = false;

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
            var openEnded = item.OpenEnded;

            // Câu ĐÓNG NHÓM BẰNG MỘT CÚ BẤM (ngoại lệ / báo cáo hỏi bằng cặp chip có-không) ⇒ bỏ chip,
            // chuyển thành câu MỞ. Vế "Không" của cặp đó đưa thẳng dòng bản đồ tới [KHÔNG ÁP DỤNG] — trạng
            // thái không có đường quay lại — còn vế "Có" thì không chở nội dung nào. Xem
            // InterviewQuestionRules cho ca thật và cho lý do phép thử chỉ bắt đúng hình dạng có/không.
            if (InterviewQuestionRules.IsGroupClosingYesNo(item.Group, suggestions))
                openEnded = true;

            if (openEnded)
                suggestions = new List<string>();

            result.Add(new BAChatQuestion
            {
                Group = (item.Group ?? string.Empty).Trim(),
                Question = question,
                Suggestions = suggestions,
                MultiSelect = item.MultiSelect && suggestions.Count >= 2,
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

    // ==== CHIP "KHÁC" TRẦN: parser KHÔNG đoán ====
    // Ở đây từng có `DropBareOtherChips`: sáu bảng cụm tiếng Việt (đầu mê-ta + đuôi "khác", ngôi thứ nhất +
    // động từ diễn đạt…) xoá các chip không chở câu trả lời — "Khác", "Quy tắc khác", "Mình mô tả cụ thể
    // hơn". Nó đã được gỡ, cùng lý do và cùng đường đi với `ShapeAnswer`/`LooksOpenEnded`: "chip này có chở
    // câu trả lời không" là một câu hỏi về NGHĨA, bảng cụm phải cố ý hẹp để khỏi xoá oan nên lọt lưới
    // thường xuyên, và nó câm hẳn với "Other" / "Something else". Việc đó nay do lượt trọng tài phán
    // (InterviewJudge) và BAChatService.ApplyEmptyChipVerdict thi hành — xem ghi chú ở đó cho hai chốt
    // hình dạng còn giữ lại (khớp nguyên văn, xoá xong còn ≥ 2 chip).

    // ==== CÂU HỎI MỞ: cờ `openEnded` do PROMPT giữ, parser KHÔNG đoán ====
    // Ở đây từng có `LooksOpenEnded`: một bảng cụm từ tiếng Việt ("kể giúp", "mô tả", "nói rõ hơn"…) tự
    // bật `openEnded` cho lượt nào có dấu hỏi và chạm một cụm — tức tự XOÁ hàng chip của lượt đó. Nó đã
    // được gỡ, cùng lý do và cùng đường đi với `ShapeAnswer` (xem ghi chú đầu file): đoán ngữ nghĩa bằng
    // `Contains` thì không bao giờ phủ hết, mỗi lần đoán sai là người dùng MẤT TRẮNG hàng chip, nên bảng
    // buộc phải chính xác — thứ không đạt được — và nó lớn dần theo từng ca lọt lưới. Hai ca thật, hai
    // vòng vá, cùng đúng hai chữ "mô tả": *"Cảm ơn anh/chị đã mô tả."* (cụm chỉ nhắc lại lời người dùng)
    // rồi *"…thông tin gì về vai trò không (ví dụ: mô tả vai trò, phòng ban áp dụng)…"* (cụm là tên một
    // cột dữ liệu). Cả hai đều là câu hỏi ĐÓNG có đủ chip trong AI Call Logs mà lên màn hình thì không
    // còn nút nào để bấm. Vòng vá thứ ba đằng nào cũng tới, và mỗi ngôn ngữ mới lại là một bảng nữa.
    //
    // Nay `openEnded` đi THẲNG từ model, như `suggestions` và `multiSelect`. Chỗ dạy quyết định đóng/mở là
    // prompt (`requirement-chat.v4.md`, mục "CÂU ĐÓNG hay CÂU MỞ" + "Đừng lạm dụng openEnded"), và cái giá
    // của việc bỏ phanh — một câu xin lời kể mà model quên đánh dấu nên vẫn còn chip — là cái giá đã biết
    // và đã chấp nhận: lưới an toàn của nó là điểm chấm trong golden set, không phải một bảng cụm từ.
    //
    // Luật "cờ ⇒ xoá chip" thì GIỮ NGUYÊN, ở cả hai đường vào và cả hai loại lượt: nó không đoán gì, chỉ
    // giữ cho một câu hỏi không bao giờ có hai chỗ trả lời.

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

    private class RawReply
    {
        public string? Message { get; set; }
        public List<JsonElement>? Suggestions { get; set; }
        public bool? MultiSelect { get; set; }
        public bool? OpenEnded { get; set; }
        public bool? SummaryCheck { get; set; }
        public List<RawQuestion>? Questions { get; set; }
        public List<PermissionMatrixRow>? PermissionMatrix { get; set; }
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

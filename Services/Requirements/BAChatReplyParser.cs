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

        // Hình dạng câu trả lời do CÂU HỎI quyết định — xem ShapeAnswer.
        var shaped = ShapeAnswer(reply.Message, reply.Suggestions, reply.MultiSelect);
        reply.Suggestions = shaped.Suggestions;
        reply.MultiSelect = shaped.MultiSelect;
        reply.OpenEnded = reply.OpenEnded || shaped.OpenEnded;

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

            // Câu ĐÓNG NHÓM BẰNG MỘT CÚ BẤM (ngoại lệ / báo cáo hỏi bằng cặp chip có-không) ⇒ bỏ chip,
            // chuyển thành câu MỞ. Vế "Không" của cặp đó đưa thẳng dòng bản đồ tới [KHÔNG ÁP DỤNG] — trạng
            // thái không có đường quay lại — còn vế "Có" thì không chở nội dung nào. Xem
            // InterviewQuestionRules cho ca thật và cho lý do phép thử chỉ bắt đúng hình dạng có/không.
            if (InterviewQuestionRules.IsGroupClosingYesNo(item.Group, suggestions))
                openEnded = true;

            if (openEnded)
                suggestions = new List<string>();

            var shaped = ShapeAnswer(question, suggestions, item.MultiSelect);

            result.Add(new BAChatQuestion
            {
                Group = (item.Group ?? string.Empty).Trim(),
                Question = question,
                Suggestions = shaped.Suggestions,
                MultiSelect = shaped.MultiSelect,
                OpenEnded = openEnded || shaped.OpenEnded
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

    // ==== HÌNH DẠNG CÂU TRẢ LỜI: CÂU HỎI là thứ quyết định, không phải cờ của model ====
    // Câu hỏi "gồm những vai trò nào?" có câu trả lời là một DANH SÁCH. Đó là thuộc tính của CÂU HỎI, không
    // phải một lựa chọn giao diện: không có cách đặt cờ nào biến nó thành câu chọn-một, và ngược lại.
    //
    // Bản trước chỉ đọc HAI tín hiệu — cờ `multiSelect` của model và hình dạng bộ chip — rồi khi hai cái
    // chỏi nhau thì luôn hy sinh cờ. Chỗ đó bỏ sót đúng tín hiệu quyết định là câu hỏi, nên nó chữa được
    // triệu chứng "tích ô 1 + ô 4 mâu thuẫn" mà không chữa được bệnh: một câu hỏi liệt kê vẫn lên màn hình
    // dưới dạng chọn-một. Lúc đó model gần như luôn kèm thêm một chip CHỐT HẠ ("Tất cả các việc trên") để
    // vá chỗ mà chọn-một không diễn đạt nổi, người dùng bấm đúng cái chip đó cho nhanh, và bản đồ bao phủ
    // ghi lại một cụm mờ thay vì bốn trách nhiệm rời — cùng một thiệt hại, đi bằng cửa khác.
    //
    // Nay thứ tự quyết định là: đọc CÂU HỎI trước, rồi bắt bộ chip phải theo.
    //   - Câu KHÔNG phải liệt kê ⇒ giữ nguyên luật cũ: cờ do BA đặt, bị HẠ nếu bộ chip sai hình dạng, chip
    //     giữ nguyên. BA vẫn được quyền chọn-một trên một bộ chip nguyên tử (vd bắt chọn ra phương án
    //     quan trọng nhất) — đó là một phán đoán nghiệp vụ hợp lệ.
    //   - Câu LIỆT KÊ ⇒ bỏ chip chốt hạ (chúng chỉ tồn tại để vá chế độ chọn-một, xem IsEscapeHatchChip),
    //     rồi:
    //       • phần còn lại nguyên tử và còn ≥ 2 chip ⇒ multiSelect = true, KỂ CẢ khi model để false. Hai
    //         tín hiệu độc lập (câu hỏi + hình dạng chip) cùng nói "danh sách" là bằng chứng mạnh hơn cờ,
    //         và ở bộ chip nguyên tử-rời nhau thì MỌI tổ hợp tích đều là câu trả lời có nghĩa — tức cái
    //         giá của việc bật nhầm ở đây bằng không, khác hẳn ca bật bừa trên bộ chip dạng gói.
    //       • phần còn lại vẫn sai hình dạng ⇒ BỎ HẲN chip, chuyển thành CÂU MỞ. Máy không được tự tách
    //         hay viết lại chip: tách "Tham gia và cập nhật kết quả" thành hai mảnh là bịa từ thay BA, còn
    //         xóa nó là làm mất một trách nhiệm khỏi bản đồ bao phủ mà không ai biết. Để người dùng tự kể
    //         thì chỉ tốn công gõ — mất tiện ích, không mất dữ liệu — nên đó là cái giá đúng phải trả. Nó
    //         cũng là thứ duy nhất tạo áp lực ngược lên prompt: chip viết sai kiểu thì mất luôn hàng chip.
    //
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
    /// <summary>
    /// Chốt hình dạng cuối cùng của một cặp (câu hỏi, bộ chip): chọn một / chọn nhiều / câu mở. Dùng chung
    /// cho chip lượt-đơn và chip của từng câu trong lượt gộp — hai đường phải cho ra cùng một màn hình.
    /// </summary>
    private static (List<string> Suggestions, bool MultiSelect, bool OpenEnded) ShapeAnswer(
        string? question, List<string> suggestions, bool modelFlag)
    {
        if (suggestions.Count == 0)
            return (suggestions, false, false);

        // Chip "khác" trần thừa ở MỌI câu, không riêng câu liệt kê ⇒ lọc trước khi xét hình dạng.
        suggestions = DropBareOtherChips(suggestions);

        // Câu không phải liệt kê: luật cũ nguyên vẹn — cờ do BA đặt, hạ nếu bộ chip không đúng hình dạng.
        if (!LooksEnumerationQuestion(question))
            return (suggestions, modelFlag && IsEnumerationSet(suggestions), false);

        var members = suggestions.Where(s => !IsEscapeHatchChip(s)).ToList();

        // Bộ chip đã đúng hình dạng danh sách ⇒ bật chọn nhiều, bất kể model để cờ gì.
        if (IsEnumerationSet(members))
            return (members, true, false);

        // Còn dưới hai chip thì không đủ cơ sở nói bộ này sai hình dạng (một chip lẻ có thể chỉ là gợi ý
        // mồi). Giữ nguyên như cũ thay vì xoá — xoá ở đây là mất chip mà chẳng đổi lại được gì.
        if (members.Count < 2)
            return (suggestions, false, false);

        // Câu hỏi đòi một danh sách nhưng chip vẫn là các phương án lắp sẵn ⇒ không có hình dạng nào đúng
        // để render. Mời người dùng tự kể còn hơn dựng một màn chọn-một cho câu hỏi vốn không chọn-một.
        return (new List<string>(), false, true);
    }

    // ==== CÂU HỎI LIỆT KÊ ====
    // Nhận diện bằng CỤM TỪ và cố tình HẸP, đúng tinh thần NarrativeCues: chỉ bắt những câu mà đọc lên ai
    // cũng thấy đáp án là một danh sách ("gồm những vai trò nào?", "chịu trách nhiệm những việc gì?"). Câu
    // nào lọt lưới thì rơi về luật cũ — mất tiện ích, không sinh dữ liệu sai.
    //
    // Hai cái bẫy phải gỡ trước khi xét:
    //   - "thế nào" / "như thế nào" chứa " nào" nhưng là câu hỏi CÁCH THỨC, đáp án là một phương án trọn
    //     vẹn ("Nếu đơn bị từ chối thì xử lý thế nào?"). Cắt đi trước khi tìm " nào".
    //   - Câu ép chọn ĐÚNG MỘT vẫn có thể mang dạng số nhiều ("trong những cách sau, cách nào phù hợp
    //     nhất?"). Có dấu hiệu ép-chọn-một thì trả false ngay, để BA giữ quyền bắt chọn một.
    private static bool LooksEnumerationQuestion(string? text)
    {
        var value = (text ?? string.Empty).ToLowerInvariant();
        if (value.Length == 0)
            return false;

        value = value.Replace("như thế nào", " ", StringComparison.Ordinal)
                     .Replace("thế nào", " ", StringComparison.Ordinal);

        if (SinglePickCues.Any(cue => value.Contains(cue, StringComparison.Ordinal)))
            return false;

        if (ListingCues.Any(cue => value.Contains(cue, StringComparison.Ordinal)))
            return true;

        var plural = value.Contains("những ", StringComparison.Ordinal) || value.Contains("các ", StringComparison.Ordinal);
        var asking = value.Contains(" nào", StringComparison.Ordinal) || value.Contains(" gì", StringComparison.Ordinal);
        return plural && asking;
    }

    private static readonly string[] ListingCues = { "gồm những", "bao gồm", "liệt kê" };

    private static readonly string[] SinglePickCues =
    {
        "chọn một", "một trong", "cách nào", "phương án nào", "cái nào",
        "quan trọng nhất", "phù hợp nhất", "ưu tiên nhất"
    };

    // ==== CHIP CHỐT HẠ ====
    // "Tất cả các việc trên", "Cả hai bên trên", "Như trên" — chip TỰ THAM CHIẾU: nội dung của nó chính là
    // các chip còn lại, nó không mang thêm thông tin nào. Nó chỉ sinh ra vì chế độ chọn-một không diễn đạt
    // được "nhiều cái cùng lúc"; ở chế độ chọn nhiều thì tích hết các ô ĐÃ là "tất cả", nên giữ nó lại chỉ
    // tạo ra một ô mâu thuẫn với chính phần còn lại. Đây là chip DUY NHẤT được phép xoá, và xoá được vì
    // không mất gì.
    //
    // Nhận diện phải bắt CẢ HAI đầu — mở đầu bằng từ chỉ toàn thể VÀ kết bằng một tham chiếu ngược. Chỉ xét
    // một đầu là xoá nhầm chip thật: "Cấp trên" kết bằng " trên", còn "Tất cả nhân viên nhà máy" mở đầu
    // bằng "tất cả" nhưng là một nhóm người có thật.
    private static bool IsEscapeHatchChip(string suggestion)
    {
        var text = suggestion.Trim().ToLowerInvariant();
        return TotalityChipPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal))
            && BackReferenceSuffixes.Any(s => text.EndsWith(s, StringComparison.Ordinal));
    }

    private static readonly string[] TotalityChipPrefixes =
        { "tất cả", "toàn bộ", "cả ", "mọi ", "như trên", "các ý", "những ý" };

    private static readonly string[] BackReferenceSuffixes =
        { " trên", " đã nêu", " vừa nêu", " đã kể", " above" };

    // ==== CHIP "KHÁC" TRẦN ====
    // "Khác", "Tự nhập", "Quy tắc khác", "Trạng thái khác", "Cách xử lý khác", "Mình mô tả cụ thể hơn" —
    // chip mà toàn bộ nội dung chỉ là "không phải mấy cái kia" hoặc "để tôi tự nói". Nó nói ĐÚNG BẰNG ô
    // "Ý khác" nằm ngay dưới mọi hàng chip (mở sẵn ở cả hàng chip lượt-đơn lẫn từng dòng của thẻ gộp),
    // chỉ thiếu đúng phần đắt nhất: NỘI DUNG. Mà ở lượt một câu, bấm chip là GỬI NGAY — nên cú bấm đó gửi
    // đi một lượt user rỗng ("Quy tắc khác", quy tắc gì thì không ai biết), trong khi bản đồ bao phủ vẫn
    // tính là nhóm đó đã được hỏi VÀ đã trả lời. Đúng ca
    // "câu trả lời rỗng" mà prompt cảnh báo, chỉ khác là lần này chính bộ chip bày sẵn cái bẫy.
    //
    // Prompt cấm chip này từ lâu, nhưng cấm theo MẶT CHỮ ("Khác", "Tự nhập") nên model né được chỉ bằng
    // cách thêm một danh từ vào trước — "Quy tắc khác" lọt qua sạch sẽ, và đó là ca đã gặp trên màn hình.
    // Hàm này cấm theo HÌNH DẠNG, và bắt HAI hình dạng của cùng một lối thoát:
    //   - Đuôi là "khác" + phần đầu là một danh từ MÊ-TA (không chở nội dung nghiệp vụ nào).
    //   - Chip TỰ-MÔ-TẢ: ngôi thứ nhất + động từ diễn đạt ("Mình mô tả cụ thể hơn", "Để tôi kể rõ hơn").
    //     Nó không có chữ "khác" nào nên lọt hình dạng trên, nhưng nội dung của nó là HÀNH ĐỘNG TRẢ LỜI
    //     chứ không phải một câu trả lời — tức đúng cái ô "Ý khác", viết bằng một mặt chữ khác. Ca thật
    //     trên màn hình đến từ chính ví dụ JSON mẫu trong prompt, chỗ model chép nhiều hơn đọc luật.
    // Prompt vẫn là chỗ dạy viết chip cho đúng; đây là cái phanh khi prompt bị trượt.
    //
    // Xoá được vì không mất gì — cùng lý lẽ với chip chốt hạ, và đây là chip THỨ HAI cũng là cuối cùng
    // được phép xoá. Hai chốt giữ cho nó không xoá quá tay:
    //   - Danh sách đầu MÊ-TA cố tình HẸP. "Chuyển sang phòng ban khác", "Theo quy trình khác" chở nội
    //     dung thật ⇒ giữ. Lọt lưới thì mất tiện ích, không mất dữ liệu — cùng chiều đánh đổi với
    //     NarrativeCues và ListingCues. Chip tự-mô-tả cũng vậy: BẮT BUỘC có ngôi thứ nhất mở đầu, nên
    //     "Mô tả công việc theo vai trò" — một câu trả lời thật trong nghiệp vụ JD — không bị đụng tới.
    //   - Xoá xong phải còn ≥ 2 chip. Bộ HAI chip mà prompt kê sẵn ở lượt xin chốt (["Đồng ý", "Tôi muốn
    //     khác"], ["Đúng rồi", "Không, tính khác"]) thì vế "khác" KHÔNG phải lối thoát mà là một trong hai
    //     nhánh trả lời của chính câu hỏi; xoá nó đi là biến một câu hỏi thành cái gật bắt buộc. Ở đúng bộ
    //     đó, việc mở ô nhập tại chỗ là của giao diện (requirements.js: isDissentChip), không phải của
    //     parser — hai tầng chia nhau đúng một bài toán, ai không xử lý được thì tầng kia đỡ.
    private static List<string> DropBareOtherChips(List<string> suggestions)
    {
        var kept = suggestions.Where(s => !IsBareOtherChip(s)).ToList();
        return kept.Count >= 2 ? kept : suggestions;
    }

    private static bool IsBareOtherChip(string suggestion)
    {
        var text = suggestion.Trim().ToLowerInvariant();

        // Bỏ phần chú trong ngoặc trước khi so: "Khác (tự nhập)" là đúng một chip đó, viết dài ra thôi.
        var paren = text.IndexOf('(');
        if (paren >= 0)
            text = text[..paren];

        text = text.Trim(ChipTrimChars);
        if (StandaloneOtherChips.Contains(text))
            return true;

        if (IsSelfDescribeChip(text))
            return true;

        if (!text.EndsWith("khác", StringComparison.Ordinal))
            return false;

        return MetaChipHeads.Contains(text[..^"khác".Length].Trim(ChipTrimChars));
    }

    // Chip TỰ-MÔ-TẢ: "Mình mô tả cụ thể hơn", "Để tôi kể rõ hơn", "Mình tự nhập".
    //
    // Phép thử phải HẸP, vì "chung chung" không phải thứ máy đo được: chip mơ hồ mà vẫn chở dữ kiện
    // ("Chưa có quy trình cố định") là câu trả lời thật, nuốt nó đi là mất dữ liệu. Thứ đo được là chip
    // mô tả HÀNH ĐỘNG TRẢ LỜI của người dùng thay vì chở câu trả lời — nên đòi ĐỦ HAI vế: mở đầu bằng
    // ngôi thứ nhất VÀ hứa một câu trả lời ở chỗ khác (động từ diễn đạt kèm dấu hiệu nói-thêm, hoặc một
    // động từ tự-nhập). Thiếu vế ngôi thứ nhất thì "Mô tả công việc theo vai trò" rơi vào lưới; thiếu vế
    // sau thì "Mình tự đăng ký khóa học" và "Mình mô tả công việc trong JD" rơi vào.
    //
    // "Tôi muốn sửa lại" / "Tôi muốn khác" của bộ HAI chip xin chốt có ngôi thứ nhất nhưng không có động
    // từ diễn đạt nào, nên không chạm hàm này; và kể cả có chạm thì ràng buộc "còn ≥ 2 chip" ở
    // DropBareOtherChips vẫn giữ nguyên bộ đó — hai chốt độc lập cho cùng một chỗ không được phép hỏng.
    private static bool IsSelfDescribeChip(string text)
    {
        var hasFirstPerson = FirstPersonHeads.Any(head =>
            text.Equals(head, StringComparison.Ordinal) ||
            text.StartsWith(head + " ", StringComparison.Ordinal));
        if (!hasFirstPerson)
            return false;

        // "Mình tự nhập" tự nó đã nói hết: không có nghiệp vụ nào để lẫn vào.
        if (SelfInputVerbs.Any(verb => text.Contains(verb, StringComparison.Ordinal)))
            return true;

        // Còn động từ diễn đạt thì PHẢI đi kèm một dấu hiệu NÓI THÊM. Thiếu vế đó, "Mình mô tả công việc
        // trong JD" — một câu trả lời thật — trông y hệt lối thoát.
        return DescribeVerbs.Any(verb => text.Contains(verb, StringComparison.Ordinal))
            && ElaborationCues.Any(cue => text.Contains(cue, StringComparison.Ordinal));
    }

    // Ngôi thứ nhất mở đầu — chip được BA viết bằng giọng của người dùng, nên lối thoát này luôn bắt đầu
    // bằng chính họ. Đòi nó ĐỨNG ĐẦU chứ không chỉ xuất hiện đâu đó: "Quản lý mô tả lại quy trình cho
    // nhân viên" nói về một người khác và chở một câu trả lời thật.
    private static readonly string[] FirstPersonHeads =
        { "mình", "tôi", "em", "để mình", "để tôi", "để em" };

    private static readonly string[] SelfInputVerbs =
        { "tự nhập", "tự gõ", "tự viết", "tự điền", "tự ghi" };

    // Động từ DIỄN ĐẠT: nói về việc trả lời, không phải về nghiệp vụ. Cố tình dùng cụm nhiều từ ("nói rõ")
    // thay vì từ trần ("nói") — từ trần quét trúng quá nhiều câu trả lời thật.
    private static readonly string[] DescribeVerbs =
    {
        "mô tả", "kể", "trình bày", "giải thích", "diễn giải", "nói rõ", "nói cụ thể", "nói thêm"
    };

    // Dấu hiệu NÓI THÊM: chip hứa một câu trả lời ở chỗ khác thay vì đưa ra câu trả lời ngay tại chip.
    private static readonly string[] ElaborationCues =
        { "cụ thể", "rõ hơn", "chi tiết", "kỹ hơn", "thêm", "lại", "ở dưới", "bên dưới", "ở ô" };

    private static readonly char[] ChipTrimChars = { ' ', '.', ',', ';', ':', '!', '?', '…', '-', '–', '"', '\'' };

    private static readonly HashSet<string> StandaloneOtherChips = new(StringComparer.Ordinal)
        { "khác", "tự nhập", "nhập tay", "tự điền", "tự ghi" };

    // Đầu MÊ-TA: danh từ chỉ CHỖ của câu trả lời chứ không chở câu trả lời nào. Thêm vào đây thì phải chắc
    // rằng "<đầu> khác" đứng một mình vẫn không nói được điều gì người dùng chưa nói.
    private static readonly HashSet<string> MetaChipHeads = new(StringComparer.Ordinal)
    {
        "ý", "ý kiến", "quy tắc", "quy định", "quy trình", "trạng thái", "cách", "cách xử lý",
        "cách làm", "hướng xử lý", "phương án", "lựa chọn", "tùy chọn", "tuỳ chọn", "đáp án",
        "câu trả lời", "trường hợp", "tình huống", "hình thức", "kiểu", "loại", "mục",
        "tôi muốn", "muốn", "cái"
    };

    // Hàm này chỉ trả lời ĐÚNG MỘT câu — "bộ chip này có đúng hình dạng danh sách không?" — và không tự ý
    // quyết định gì thêm. Việc dùng câu trả lời đó ra sao là của ShapeAnswer, nơi có thêm tín hiệu câu hỏi.
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

        return NarrativeCues.Any(cue => AsksWith(value, cue));
    }

    /// <summary>
    /// Cụm này có xuất hiện như một LỜI XIN không — hay mọi lần nó xuất hiện đều chỉ NHẮC LẠI điều người
    /// dùng vừa nói ("cảm ơn anh/chị đã mô tả", "như anh/chị vừa kể", "theo mô tả của anh/chị")?
    ///
    /// <para>
    /// <b>Ca thật.</b> BA trả về đúng một câu hỏi ĐÓNG — *"…ứng dụng phục vụ những vai trò nào trong nhà
    /// máy?"* kèm bốn chip vai trò — nhưng mở đầu bằng *"Cảm ơn anh/chị đã mô tả."*. Phép thử cũ quét cả
    /// lượt nên thấy "mô tả" + một dấu hỏi ở đâu đó là kết luận câu mở, xoá sạch chip; trên màn hình hiện
    /// ra một câu hỏi đóng KHÔNG có nút nào để bấm, trong khi AI Call Logs vẫn ghi đủ bốn gợi ý model trả
    /// về — người đọc log không hiểu chip biến đi đâu.
    /// </para>
    ///
    /// <para>
    /// Xét theo TỪNG LẦN xuất hiện chứ không theo cả lượt, và chỉ bỏ qua lần nào đứng ngay sau một dấu
    /// hiệu nhắc-chuyện-cũ. Nhờ vậy một lời cảm ơn đứng trước một lời xin lời kể ("Cảm ơn anh/chị đã mô
    /// tả. Anh/chị kể giúp mình…") vẫn là câu mở: lần thứ hai không mang dấu hiệu nào. Cùng chiều thận
    /// trọng với cả guard — thu hẹp đúng ca chắc chắn sai, không nới cho mọi câu có chữ "đã".
    /// </para>
    /// </summary>
    private static bool AsksWith(string value, string cue)
    {
        for (var from = 0; from <= value.Length - cue.Length;)
        {
            var index = value.IndexOf(cue, from, StringComparison.Ordinal);
            if (index < 0)
                return false;
            if (!IsLookingBack(value, index))
                return true;
            from = index + cue.Length;
        }

        return false;
    }

    // Ngay trước cụm là một từ NHẮC LẠI (bỏ qua khoảng trắng): "đã mô tả", "vừa kể", "như mô tả",
    // "theo mô tả". Tiếng Việt viết rời từng âm tiết nên EndsWith ở đây đúng bằng "âm tiết cuối là".
    private static bool IsLookingBack(string value, int cueIndex)
    {
        var before = value[..cueIndex].TrimEnd();
        return before.Length > 0
            && LookBackMarkers.Any(marker => before.EndsWith(marker, StringComparison.Ordinal));
    }

    private static readonly string[] LookBackMarkers = { "đã", "vừa", "như", "theo" };

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

    private class RawReply
    {
        public string? Message { get; set; }
        public List<JsonElement>? Suggestions { get; set; }
        public bool? MultiSelect { get; set; }
        public bool? OpenEnded { get; set; }
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

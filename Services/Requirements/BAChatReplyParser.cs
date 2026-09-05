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
// là không render đúng được; nó đã bị gỡ vì đoán sai là mất trắng chip của một lượt — xem
// docs/requirement-flow.md, mục "Hình dạng bộ chip do PROMPT giữ".
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
            var openEnded = item.OpenEnded || LooksOpenEnded(question);

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

        // Chip "khác" trần bị xoá ở ĐÂY, trên cả hai đường vào (chip lượt-đơn và chip của từng câu trong
        // lượt gộp). Trước kia nó đi nhờ ShapeAnswer; ShapeAnswer đã bỏ nên nó đứng ở đúng chỗ của mình —
        // đây là một phép dọn chip THỪA, không phải một phán đoán về hình dạng bộ chip.
        return DropBareOtherChips(result);
    }

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
    // Xoá được vì không mất gì, và đây là chip DUY NHẤT parser được phép xoá. Hai chốt giữ cho nó không
    // xoá quá tay:
    //   - Danh sách đầu MÊ-TA cố tình HẸP. "Chuyển sang phòng ban khác", "Theo quy trình khác" chở nội
    //     dung thật ⇒ giữ. Lọt lưới thì mất tiện ích, không mất dữ liệu — cùng chiều đánh đổi với
    //     NarrativeCues. Chip tự-mô-tả cũng vậy: BẮT BUỘC có ngôi thứ nhất mở đầu, nên
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

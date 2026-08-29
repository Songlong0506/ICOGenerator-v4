using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Cổng readiness DUY NHẤT và TẤT ĐỊNH quyết định "đã đủ thông tin cốt lõi để soạn tài liệu chưa":
/// suy trực tiếp từ "Bản đồ bao phủ yêu cầu" (<see cref="Project.RequirementCoverageMap"/>, do
/// <see cref="RequirementCoverageService"/> duy trì) — sẵn sàng ⇔ mọi dòng áp dụng đã <c>[RÕ]</c>.
/// Bản đồ là nguồn chân lý duy nhất nên panel "Tiến độ khai thác" trên UI, lời mời bấm
/// "Write Requirement" của BA và cổng lúc bấm nút KHÔNG THỂ vênh nhau — cả ba đọc cùng một dữ liệu.
/// (Trước đây cổng là một lời gọi LLM riêng chấm lại transcript: hai "giám khảo" lệch nhau tạo cảnh
/// panel báo 9/12 nhưng BA vẫn mời bấm nút, và gate lỗi thì fail-open thành ready.) Không sẵn sàng ⇒
/// trả về CÂU HỎI dựng sẵn cho đúng chỗ còn thiếu theo bản đồ. Bản đồ chưa có/lỗi gộp ⇒ CHƯA
/// sẵn sàng (fail-closed): distiller giữ con trỏ cũ và gộp bù ở lượt sau nên trạng thái tự lành.
/// </summary>
public static class RequirementReadinessGate
{
    /// <summary>
    /// Xét độ sẵn sàng từ bản đồ bao phủ: ready ⇔ bản đồ đã có, không còn dòng áp dụng nào
    /// [CHƯA HỎI]/[MỘT PHẦN], và có ít nhất một dòng [RÕ] (bản đồ toàn [KHÔNG ÁP DỤNG] là bản đồ hỏng,
    /// không phải dự án đã rõ). Khi chưa sẵn sàng, Message là CÂU HỎI dựng sẵn cho đúng chỗ còn thiếu —
    /// dùng được ngay như một lượt BA trong khung chat.
    ///
    /// <para>
    /// <paramref name="turns"/> là các lượt hội thoại gần đây, dùng để KHÔNG phát lại đúng câu chặn vừa
    /// phát: cổng dò chính CÂU HỎI mà nó sắp phát (xem <see cref="LastAskedAt"/>) trong các lượt BA đã lưu,
    /// rồi chuyển sang một chỗ còn thiếu khác trước khi quay lại chỗ cũ. Bỏ trống ⇒ giữ nguyên thứ tự cũ
    /// (★ cốt lõi trước) — đúng cho những caller chỉ cần cờ <c>Ready</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Vì sao cổng phải tự nhớ.</b> Phanh chống hỏi lại dùng chung
    /// (<see cref="AskedQuestionHistory.Collect"/>) chỉ trả lời được MỘT câu: "câu này hỏi rồi hay chưa".
    /// Cổng cần thứ khác — THỨ TỰ: nó phải dựng câu hỏi của MỌI dòng còn thiếu rồi xếp theo lần cuối mỗi
    /// câu được phát, để chỗ chưa hỏi đi trước và chỗ vừa hỏi lùi lại một vòng. Sổ chung không mang vị
    /// trí, nên cổng dò bằng chính câu hỏi nó sắp phát trong các lượt BA đã lưu.</para>
    ///
    /// <para>
    /// Lượt chặn của cổng KHÔNG có chip (nó là câu MỞ) nhưng luôn kết bằng dấu hỏi, nên nó vẫn vào sổ
    /// chung — model đọc "các câu BẠN ĐÃ HỎI" sẽ không phát lại nó bằng lời của mình.
    /// </para>
    /// </summary>
    public static RequirementReadiness Evaluate(string? coverageMap, IEnumerable<AgentConversation>? turns = null)
    {
        var items = CoverageMapParser.Parse(coverageMap);
        if (items.Count == 0)
        {
            return new RequirementReadiness
            {
                Ready = false,
                Message = "Mình chưa tổng hợp được bản đồ khai thác yêu cầu cho dự án này, nên chưa thể viết tài liệu. Bạn trao đổi thêm một lượt trong khung chat rồi thử lại nhé.",
                OpenEnded = true
            };
        }

        var pending = items.Where(x => x.Status is "MỘT PHẦN" or "CHƯA HỎI").ToList();

        if (pending.Count == 0)
            return items.Any(x => x.Status == "RÕ")
                ? new RequirementReadiness { Ready = true }
                // Bản đồ toàn [KHÔNG ÁP DỤNG] — bản đồ hỏng, không có nhóm nào để hỏi cụ thể.
                : new RequirementReadiness
                {
                    Ready = false,
                    Message = "Bản đồ khai thác yêu cầu đang trống thông tin đã rõ, nên chưa thể viết tài liệu. Bạn mô tả thêm về dự án trong khung chat giúp mình nhé.",
                    OpenEnded = true
                };

        // Chỗ CHƯA từng bị cổng hỏi đi trước, rồi mới tới chỗ bị hỏi lâu nhất; trong cùng một bậc thì ★
        // cốt lõi trước — đúng thứ tự ưu tiên mà prompt chat hướng dẫn BA chọn câu hỏi kế tiếp.
        //
        // Vì sao "đã hỏi" thắng cả cờ ★: bản đồ không nhúc nhích thì mọi lượt chặn tiếp theo chọn lại đúng
        // dòng cốt lõi đó và phát lại nguyên văn một câu người dùng vừa không trả lời được. Ca thật đã ghi ở
        // CoverageDeadQuestionLoopTests: ba lượt liên tiếp giống hệt nhau, người dùng đáp "mình không hiểu
        // câu hỏi của bạn" hai lần. Đổi chỗ hỏi thì lượt sau còn cơ hội gỡ, mà chỗ cũ không mất đi đâu — nó
        // quay lại ngay khi các chỗ khác đã được hỏi một vòng.
        //
        // Sổ này dò bằng CHÍNH CÂU HỎI sắp phát, không bằng nhãn nhóm: câu chặn không còn đọc nhãn nhóm ra
        // màn hình nữa (xem BuildPendingQuestion), nên nhãn không còn nằm trong lượt đã lưu để đọc lại. Đổi
        // sang so bằng câu hỏi còn đúng hơn ở đúng chỗ phải đúng: bản đồ nhúc nhích thì mẩu "còn thiếu:"
        // đổi, câu hỏi đổi theo — và một câu hỏi KHÁC thì đáng hỏi ngay, không phải đợi hết một vòng.
        var candidates = pending.Select(item => (Item: item, Question: AskFor(item))).ToList();
        var chosen = candidates
            .OrderBy(x => LastAskedAt(turns, x.Question))
            .ThenByDescending(x => x.Item.IsCore)
            .First();

        return new RequirementReadiness
        {
            Ready = false,
            Message = BuildPendingQuestion(chosen.Question, LastAskedAt(turns, chosen.Question) >= 0),
            OpenEnded = true
        };
    }

    /// <summary>
    /// Vị trí lượt CUỐI mà <paramref name="question"/> đã được phát trong <paramref name="turns"/>, hoặc
    /// <c>-1</c> khi chưa phát lần nào. Sắp TĂNG theo giá trị này cho ra đúng thứ tự cần: chưa hỏi (-1)
    /// trước, rồi tới câu bị hỏi lâu nhất.
    ///
    /// <para>
    /// Đây là sổ RIÊNG của cổng, không dùng <see cref="AskedQuestionHistory.Collect"/>: sổ chung chỉ nói
    /// "đã hỏi hay chưa", còn cổng cần VỊ TRÍ để xoay vòng (xem <see cref="Evaluate"/>). Dò trên VẾ CÂU HỎI nên cả
    /// hai biến thể của câu chặn đều được đọc ra — lượt "quay lại" chỉ thêm một câu dẫn ở ĐẦU, vế hỏi phía
    /// sau giữ nguyên. Chuẩn hóa hoa/thường + khoảng trắng để một lượt bị xuống dòng khác đi không làm sổ
    /// này câm trong im lặng.
    /// </para>
    /// </summary>
    public static int LastAskedAt(IEnumerable<AgentConversation>? turns, string question)
    {
        var needle = Normalize(question);
        if (needle.Length == 0)
            return -1;

        var last = -1;
        var index = 0;
        foreach (var turn in turns ?? Enumerable.Empty<AgentConversation>())
        {
            if (ConversationTurnRenderer.IsAssistant(turn)
                && Normalize(turn.Message).Contains(needle, StringComparison.Ordinal))
                last = index;
            index++;
        }

        return last;
    }

    private static string Normalize(string? text)
        => string.Join(' ', (text ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    // Câu hỏi dựng sẵn khi chưa đủ. Đây là lượt BA mà người dùng THẬT SỰ đọc trên màn hình, nên nó phải
    // là một câu hỏi TRẢ LỜI ĐƯỢC, không phải một bản tin trạng thái:
    //
    // - Hỏi ĐÚNG phần "còn thiếu: …" mà distiller ghi trên dòng [MỘT PHẦN] — đó là thứ duy nhất bước soạn
    //   tài liệu còn phải tự đoán.
    // - KHÔNG đọc cả tóm tắt máy vào câu hỏi khi đã có mẩu "còn thiếu": tóm tắt là ghi chép của hệ thống về
    //   điều người dùng đã nói, phát lại nguyên khối trong khi đã hỏi được đúng chỗ hụt chỉ làm người đọc
    //   tưởng bị hỏi lại điều họ vừa trả lời.
    // - KHÔNG nói ra nhãn nhóm, cũng không đếm số nhóm còn lại. Bản trước mở đầu bằng *"Trước khi viết tài
    //   liệu, mình còn một chỗ chưa đủ thông tin để khỏi phải tự đoán (nhóm «Đối tượng người dùng & vai
    //   trò», còn 3 nhóm — mình hỏi từng nhóm một)"* rồi mới tới câu hỏi thật. Cả cụm đó là SỔ SÁCH của hệ
    //   thống đọc ra màn hình: «Dữ liệu / danh mục chính» là từ vựng nội bộ của bản đồ, còn "còn 3 nhóm"
    //   thì báo cho người dùng biết họ còn phải chịu bao nhiêu lượt nữa — không giúp họ trả lời câu đang
    //   hỏi, chỉ làm lượt đó đọc như một bản tin tiến độ. Người dùng của repo yêu cầu đúng điều này: "BA có
    //   câu hỏi nào thì cứ hỏi thẳng luôn, không cần phải nói nhóm gì hết". Vì vậy lượt chặn chỉ chở CÂU
    //   HỎI; nhãn nhóm vẫn ở nguyên panel "Tiến độ khai thác" bên cạnh cho ai muốn xem.
    // - Kết thúc bằng dấu hỏi và chỉ hỏi MỘT chỗ, không hỏi dồn.
    //
    // BỐN NHÁNH, hẹp dần theo lượng thông tin bản đồ cho — và không nhánh nào được rơi về một câu trống
    // nghĩa. Bản trước chỉ có hai nhánh, nhánh dự phòng phát MỘT câu duy nhất cho cả 12 nhóm
    // (*"Anh/chị kể giúp mình phần này…"*): nó không nói được đang hỏi cái gì và trỏ tới "phần này" — đúng
    // cụm tham chiếu suông mà prompt cấm BA dùng. Ca thật ở dự án JD Library lượt 76: người dùng đáp
    // *"mình chưa hiểu câu hỏi, hãy hỏi rõ hơn"*, mất trắng một vòng ở cuối buổi phỏng vấn thứ 78. Nhánh đó
    // reachable với BẤT KỲ nhóm nào, chỉ cần lượt distill quên viết cụm "còn thiếu:" đúng một lần — nó là
    // định dạng do LLM xuất, không phải bất biến của code.
    /// <summary>
    /// Câu dẫn DUY NHẤT mà cổng được phép thêm vào trước vế hỏi: nó chỉ dùng khi cổng quay lại một câu đã
    /// phát. Phát lại y nguyên câu cũ đọc lên như thể cổng không nhớ mình vừa hỏi gì — mà đúng cái đó là thứ
    /// làm người dùng thôi trả lời. Đứng ở ĐẦU nên <see cref="LastAskedAt"/> (dò trên vế hỏi phía sau) vẫn
    /// nhận ra cả hai biến thể.
    /// </summary>
    private const string ComingBackLead = "Mình quay lại chỗ này một chút. ";

    // Lần đầu thì KHÔNG có câu dẫn nào: câu hỏi đứng một mình đúng như một lượt BA bình thường.
    private static string BuildPendingQuestion(string question, bool askedBefore)
        => askedBefore ? ComingBackLead + question : question;

    /// <summary>
    /// Vế câu hỏi thật cho một dòng còn thiếu, thử bốn nhánh theo đúng thứ tự thông tin đáng tin cậy giảm
    /// dần. Tách khỏi <see cref="BuildPendingQuestion"/> vì nó còn là KHÓA của sổ "đã hỏi" (xem
    /// <see cref="LastAskedAt"/>): cổng phải dựng được câu hỏi của MỌI dòng còn thiếu để so, rồi mới chọn
    /// dòng nào để phát — nên vế hỏi không được dính câu dẫn của lượt phát.
    /// </summary>
    private static string AskFor(CoverageMapItem item)
    {
        // 1. Mẩu "còn thiếu: …" — thứ duy nhất bước soạn tài liệu còn phải tự đoán, nên hỏi thẳng nó.
        var missing = ExtractMissingPart(item.Summary);
        if (!string.IsNullOrWhiteSpace(missing))
            return $"Anh/chị cho mình hỏi thêm: {ToQuestion(missing)}";

        // 2. [MỘT PHẦN] mà không có mẩu nào ⇒ PHÁT LẠI phần đã ghi nhận rồi hỏi còn hụt gì. Không được rơi
        //    xuống câu mở đầu của nhóm ở ca này: prompt chat cấm tuyệt đối việc phát lại câu mở đầu cho một
        //    nhóm [MỘT PHẦN] — người dùng đã kể phần đó rồi, nghe lại đúng câu cũ là mất lòng tin vào cả
        //    buổi phỏng vấn. Phát lại lời họ thì ngược lại: nó miễn cho họ việc phải cuộn ngược lên tìm.
        var recorded = ExtractRecordedPart(item.Summary);
        if (recorded.Length > 0)
            return $"Mình đang ghi nhận: {recorded}. Phần này còn chỗ nào chưa đúng hoặc còn thiếu mà "
                + "anh/chị muốn bổ sung không?";

        // 3. [CHƯA HỎI] (và [MỘT PHẦN] rỗng ruột — dòng chỉ còn ghi chú máy đã bị lược sạch) ⇒ câu mở đầu
        //    THẬT của nhóm, bằng ngôn ngữ công việc của người dùng.
        var opener = CoverageGroupOpeners.Find(item.Label);
        if (opener != null)
            return opener;

        // 4. Nhãn không khớp nhóm nào (model tự nghĩ ra một tên) ⇒ không bịa một câu hỏi khai thác về thứ
        //    không có trong checklist, nhưng cũng KHÔNG được trỏ tới "phần này" suông. Nhãn được đọc vào câu
        //    như một cụm chủ đề bình thường ("Về tích hợp hệ thống ngoài, …") — đó là ngôn ngữ tự nhiên, khác
        //    hẳn cái ngoặc sổ sách "(nhóm «…»)" mà bản trước in ra.
        var topic = item.Label.Trim();
        return topic.Length == 0
            ? "Anh/chị kể giúp mình chỗ này trong công việc thực tế hiện đang diễn ra thế nào?"
            : $"Về {char.ToLowerInvariant(topic[0])}{topic[1..]}, hiện trong công việc thực tế của anh/chị "
              + "đang diễn ra thế nào?";
    }

    // Phần "còn thiếu: …" trên một dòng [MỘT PHẦN] — ghi chú của distiller về đúng mẩu còn hụt. Định dạng
    // do prompt requirement-coverage.v3 ghim; không có thì trả rỗng để caller hỏi câu mở đầu của nhóm.
    private static string ExtractMissingPart(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return string.Empty;

        var at = summary.IndexOf("còn thiếu:", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return string.Empty;

        var missing = summary[(at + "còn thiếu:".Length)..].Trim();
        // Ghi chú tái mở của một dòng bị người dùng đính chính kèm "(ghi nhận trước đó: …)" — phần trong
        // ngoặc là ghi chép cũ của hệ thống, không phải điều cần hỏi.
        var note = missing.IndexOf("(ghi nhận trước đó:", StringComparison.OrdinalIgnoreCase);
        if (note >= 0)
            missing = missing[..note].Trim();

        missing = StripReopenMarker(missing).TrimEnd('.', ';', ',');

        // Mẩu RỖNG NGHĨA thì coi như không có: caller rơi về nhánh PHÁT LẠI, một câu hỏi đóng lại được.
        return IsHollowGap(missing) ? string.Empty : missing;
    }

    /// <summary>
    /// Mẩu <c>còn thiếu:</c> không nói được đang hỏi cái gì — *"các quy tắc khác (nếu có)"*, *"thông tin
    /// bổ sung"*, *"các điểm còn lại"*. Nó là một CHỖ TRỐNG chứ không phải một câu hỏi: distiller viết ra
    /// để dòng trông "chưa xong", nhưng cổng thì phát nguyên văn nó lên màn hình.
    ///
    /// <para>
    /// Ca thật (dự án JD Libary 5, lượt 26 — lượt CUỐI của buổi phỏng vấn): người dùng nhận
    /// *"Anh/chị cho mình hỏi thêm: các quy tắc khác (nếu có) — anh/chị cho mình xin thông tin này nhé?"*.
    /// Câu đó không trả lời được bằng một điều cụ thể nào, và tệ hơn: một tiếng *"không có"* sẽ lật dòng
    /// «Quy tắc nghiệp vụ &amp; ràng buộc» lên <c>[RÕ]</c> mà không thêm được một quy tắc nào — cổng mở ra
    /// bằng một câu hỏi rỗng. Nhánh phát lại (*"Mình đang ghi nhận: … còn chỗ nào chưa đúng hoặc còn thiếu
    /// không?"*) nói đúng bằng ấy ý nhưng chở theo điều đã ghi nhận, nên người dùng đọc là trả lời được.
    /// </para>
    ///
    /// <para>
    /// Nhận diện theo HÌNH DẠNG, cùng cách với chip "khác" trần ở <see cref="BAChatReplyParser"/>: bỏ phần
    /// trong ngoặc, bỏ từ chỉ số nhiều ở đầu, rồi hỏi phần còn lại có phải một danh từ MÊ-TA gắn đuôi
    /// "khác / còn lại / bổ sung" hay không. Danh sách đầu mê-ta cố ý HẸP — một mẩu chở danh từ nghiệp vụ
    /// thật (*"các trạng thái khác của JD"*) không lọt vào đây, và lọt lưới thì chỉ mất một lượt.
    /// </para>
    /// </summary>
    private static bool IsHollowGap(string missing)
    {
        var text = missing.ToLowerInvariant().Trim();

        // "(nếu có)", "(nếu cần)" — phần chú không bao giờ là nội dung cần hỏi.
        var paren = text.IndexOf('(');
        if (paren >= 0)
            text = text[..paren];

        text = text.Trim(GapTrimChars);
        foreach (var prefix in PluralPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                text = text[prefix.Length..].Trim();
        }

        if (text.Length == 0)
            return true;

        foreach (var suffix in HollowSuffixes)
        {
            if (!text.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var head = text[..^suffix.Length].Trim(GapTrimChars);
            if (MetaGapHeads.Contains(head))
                return true;
        }

        return MetaGapHeads.Contains(text);
    }

    private static readonly char[] GapTrimChars = { ' ', '.', ',', ';', ':', '-', '–', '…' };

    private static readonly string[] PluralPrefixes = { "các ", "những ", "một số " };

    private static readonly string[] HollowSuffixes = { "khác", "còn lại", "bổ sung", "chưa nêu" };

    // Danh từ chỉ CHỖ của câu trả lời chứ không chở câu trả lời nào — cùng vai trò với
    // BAChatReplyParser.MetaChipHeads, và cũng phải hẹp vì lý do y hệt.
    private static readonly HashSet<string> MetaGapHeads = new(StringComparer.Ordinal)
    {
        "quy tắc", "quy tắc nghiệp vụ", "quy định", "ràng buộc", "thông tin", "yêu cầu", "nội dung",
        "chi tiết", "điểm", "mục", "phần", "dữ liệu", "vấn đề", "ý"
    };

    /// <summary>Trần độ dài phần phát lại — câu hỏi, không phải biên bản. Cùng hạng với
    /// <c>CoveragePendingGuard.MaxGapChars</c>.</summary>
    private const int MaxRecordedChars = 200;

    // Phần ĐÃ GHI NHẬN của một dòng [MỘT PHẦN]: mọi thứ đứng TRƯỚC cụm "còn thiếu:". Dùng để phát lại theo
    // "QUY TẮC PHÁT LẠI" của prompt chat khi distiller không viết được mẩu còn hụt — người dùng chỉ thấy ô
    // chat cuối trên màn hình, nên một câu hỏi bổ sung không kèm phần phát lại là câu hỏi họ phải cuộn
    // ngược lên mới trả lời được, và phần lớn sẽ không cuộn.
    //
    // Ghi chú máy bị lược SẠCH trước khi phát: cụm ReopenNote và mẩu "(ghi nhận trước đó: …)" là ghi chép
    // của hệ thống dành cho BA, đọc lên là xưng "người dùng" ở ngôi thứ ba với chính người đang đọc. Lược
    // hết mà không còn gì ⇒ trả rỗng để caller rơi về câu mở đầu của nhóm.
    private static string ExtractRecordedPart(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return string.Empty;

        var at = summary.IndexOf("còn thiếu:", StringComparison.OrdinalIgnoreCase);
        var recorded = (at < 0 ? summary : summary[..at]).Trim();

        var note = recorded.IndexOf("(ghi nhận trước đó:", StringComparison.OrdinalIgnoreCase);
        if (note >= 0)
            recorded = recorded[..note].Trim();

        recorded = StripReopenMarker(recorded).Trim().TrimEnd('.', ';', ',', '—', '-');
        return recorded.Length > MaxRecordedChars ? recorded[..MaxRecordedChars].TrimEnd() + "…" : recorded;
    }

    // Cụm <see cref="AskedQuestionHistory.ReopenNote"/> mở đầu phần "còn thiếu" của một dòng vừa bị người
    // dùng đính chính. Nó là TÍN HIỆU MÁY ĐỌC (miễn phanh chống-hỏi-lại cho nhóm đó), KHÔNG phải điều cần
    // hỏi — đọc nguyên văn ra màn hình thì lượt gate thành một câu rỗng nghĩa: *"người dùng báo phần này
    // chưa đúng — cần hỏi lại và chốt lại — anh/chị cho mình xin thông tin này nhé?"*, xưng "người dùng" ở
    // ngôi thứ ba với chính người đang đọc và không hỏi gì cả. Ca thật đã gặp trên màn hình (dự án
    // JD Library, lượt 34).
    //
    // Cắt trọn CÂU chứa cụm đó và giữ phần distiller viết thêm sau nó — prompt requirement-coverage.v3
    // § "Người dùng đính chính một nhóm" bắt buộc viết tiếp đúng mẩu cần hỏi lại. Không còn gì ⇒ trả rỗng
    // để caller rơi về câu mở đầu của nhóm: một câu hỏi rộng vẫn trả lời được, còn cụm tín hiệu thì không.
    private static string StripReopenMarker(string missing)
    {
        var at = missing.IndexOf(AskedQuestionHistory.ReopenNote, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return missing;

        var sentenceEnd = missing.IndexOf('.', at);
        var tail = sentenceEnd >= 0 ? missing[(sentenceEnd + 1)..] : string.Empty;
        return (missing[..at] + " " + tail).Trim();
    }

    private static string ToQuestion(string missing)
        => missing.EndsWith('?') ? missing : missing + " — anh/chị cho mình xin thông tin này nhé?";

    // Lượt BA "mời bấm Write Requirement" — cùng tín hiệu mà UI dùng để làm nổi nút (Index.cshtml đọc
    // Contains tương tự trên lượt BA mới nhất) và BuildAssistantContext dùng để echo cờ ready. Từ khi
    // cổng tất định chạy ngay trong lượt chat, một lời mời được LƯU đồng nghĩa bản đồ bao phủ đã đủ
    // (mọi dòng áp dụng [RÕ]) tại thời điểm đó.
    public static bool IsWriteRequirementInvite(string? message) =>
        message?.Contains("Write Requirement", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// Lượt BA sắp được lưu có phải lượt "cổng readiness đã PASS tại đây" không — tức là nó MỜI bấm
    /// "Write Requirement" VÀ bản đồ bao phủ hiện hành đủ để lời mời đó hợp lệ. Kết quả được đóng dấu
    /// vào <see cref="AgentConversation.ReadinessVerified"/> của chính lượt đó.
    ///
    /// <para>
    /// Phép dò chuỗi nằm ở ĐÂY và chỉ ở đây: "lượt này có mời bấm nút không" là một tính chất của CHỮ mà
    /// model vừa sinh ra, không có tín hiệu nào khác để đọc. Cái đã bỏ đi là việc các tầng SAU (bước soạn
    /// tài liệu) phải suy lại kết luận của cổng bằng cách đọc lại transcript: quyết định được ra MỘT LẦN,
    /// ở nơi biết đủ dữ kiện, rồi được ghi lại.
    /// </para>
    /// </summary>
    public static bool IsReadinessVerifiedTurn(string? message, string? coverageMap)
        => IsWriteRequirementInvite(message) && Evaluate(coverageMap).Ready;

    /// <summary>
    /// Hội thoại đang ĐỨNG trên một lượt đã được cổng verify ⇒ bước soạn tài liệu được phép bỏ qua lần xét
    /// lại (không có thông tin mới nào kể từ lúc cổng cho qua). Thứ tự CreatedAt rồi Id — như
    /// <c>ConversationTranscriptBuilder</c> — vì CreatedAt có thể trùng.
    ///
    /// <para>
    /// Đọc CỜ chứ không đọc nội dung lượt cuối, và không lọc lượt rỗng: mọi đường ghi thêm một lượt đều
    /// mặc định <c>false</c>, nên bất kỳ thứ gì chen vào sau lời mời (một lượt chat mới, một file vừa đính
    /// kèm, một lượt ⚠️ lỗi LLM) đều tự động đóng đường tắt lại — trừ đúng những đường TỰ KHẲNG ĐỊNH rằng
    /// mình không mang thông tin mới và chép cờ sang lượt của mình
    /// (<see cref="RequirementConflictService.ApplyResolutionsAsync"/>).
    /// </para>
    /// </summary>
    public static bool IsReadinessVerifiedLatestTurn(IEnumerable<AgentConversation> conversations)
        => conversations
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .LastOrDefault()?.ReadinessVerified == true;
}

using System.Text;
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
/// trả về câu hỏi dựng sẵn nêu đúng các nhóm còn thiếu theo bản đồ. Bản đồ chưa có/lỗi gộp ⇒ CHƯA
/// sẵn sàng (fail-closed): distiller giữ con trỏ cũ và gộp bù ở lượt sau nên trạng thái tự lành.
/// </summary>
public static class RequirementReadinessGate
{
    /// <summary>
    /// Xét độ sẵn sàng từ bản đồ bao phủ: ready ⇔ bản đồ đã có, không còn dòng áp dụng nào
    /// [CHƯA HỎI]/[MỘT PHẦN], và có ít nhất một dòng [RÕ] (bản đồ toàn [KHÔNG ÁP DỤNG] là bản đồ hỏng,
    /// không phải dự án đã rõ). Khi chưa sẵn sàng, Message là câu hỏi dựng sẵn nêu nhóm còn thiếu —
    /// dùng được ngay như một lượt BA trong khung chat.
    ///
    /// <para>
    /// <paramref name="turns"/> là các lượt hội thoại gần đây, dùng để KHÔNG phát lại đúng câu chặn vừa
    /// phát: cổng nhận ra các lượt chặn CỦA CHÍNH NÓ (xem <see cref="AskedGroups"/>) rồi chuyển sang một
    /// nhóm còn thiếu khác trước khi quay lại nhóm cũ. Bỏ trống ⇒ giữ nguyên thứ tự cũ (★ cốt lõi trước) —
    /// đúng cho những caller chỉ cần cờ <c>Ready</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Vì sao cổng phải tự nhớ.</b> Phanh chống hỏi lại dùng chung
    /// (<see cref="AskedQuestionHistory.Collect"/>) chỉ nhận một lượt assistant là "câu hỏi" khi lượt đó có
    /// GỢI Ý — mà lượt chặn của cổng cố tình không có chip nào (nó là câu MỞ). Nghĩa là câu của cổng vô hình
    /// với đúng cái phanh dựng ra để chặn hỏi lại. Nới luật của <c>Collect</c> thì mọi lượt tóm tắt/thông
    /// báo cũng thành "câu hỏi" và chặn oan các lượt xác nhận về sau, nên cổng giữ sổ RIÊNG của mình, nhận
    /// diện bằng chính câu dẫn nó viết ra.
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

        // Nhóm CHƯA từng bị cổng hỏi đi trước, rồi mới tới nhóm bị hỏi lâu nhất; trong cùng một bậc thì ★
        // cốt lõi trước — đúng thứ tự ưu tiên mà prompt chat hướng dẫn BA chọn câu hỏi kế tiếp.
        //
        // Vì sao "đã hỏi" thắng cả cờ ★: bản đồ không nhúc nhích thì mọi lượt chặn tiếp theo chọn lại đúng
        // dòng cốt lõi đó và phát lại nguyên văn một câu người dùng vừa không trả lời được. Ca thật đã ghi ở
        // CoverageDeadQuestionLoopTests: ba lượt liên tiếp giống hệt nhau, người dùng đáp "mình không hiểu
        // câu hỏi của bạn" hai lần. Đổi nhóm thì lượt sau còn cơ hội gỡ, mà nhóm cũ không mất đi đâu — nó
        // quay lại ngay khi các nhóm khác đã được hỏi một vòng.
        var askedGroups = AskedGroups(turns);
        var chosen = pending
            .OrderBy(x => LastAskedAt(askedGroups, x.Label))
            .ThenByDescending(x => x.IsCore)
            .First();

        return new RequirementReadiness
        {
            Ready = false,
            Message = BuildPendingQuestion(chosen, pending.Count, LastAskedAt(askedGroups, chosen.Label) >= 0),
            OpenEnded = true
        };
    }

    /// <summary>
    /// Vị trí lần CUỐI cổng hỏi nhóm này, hoặc <c>-1</c> khi chưa hỏi lần nào. Sắp TĂNG theo giá trị này cho
    /// ra đúng thứ tự cần: chưa hỏi (-1) trước, rồi tới nhóm bị hỏi lâu nhất.
    /// </summary>
    private static int LastAskedAt(List<string> askedGroups, string label)
    {
        for (var i = askedGroups.Count - 1; i >= 0; i--)
        {
            if (SameGroup(askedGroups[i], label))
                return i;
        }

        return -1;
    }

    // So khớp hai chiều bằng TIỀN TỐ, cùng lý do với CoveragePendingGuard.FindGap: nhãn trong bản đồ và
    // nhãn cổng đã in ra ở lượt trước có thể dài ngắn khác nhau ("Luồng ngoại lệ" / "Luồng ngoại lệ & trường
    // hợp đặc biệt"), và một phép so nguyên văn sẽ làm cái sổ này câm trong im lặng.
    private static bool SameGroup(string asked, string label)
        => asked.Length > 0 && label.Length > 0
           && (asked.StartsWith(label, StringComparison.OrdinalIgnoreCase)
               || label.StartsWith(asked, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Các nhóm mà CỔNG đã hỏi trong <paramref name="turns"/>, theo đúng thứ tự hỏi (một nhóm bị hỏi hai
    /// lần thì có mặt hai lần). Nhận diện bằng <see cref="PendingMarker"/> — cụm chữ mà mọi câu chặn của
    /// cổng đều chở — rồi lấy nhãn trong cặp «…» ngay sau nó.
    ///
    /// <para>
    /// Đây là sổ RIÊNG của cổng, không dùng <see cref="AskedQuestionHistory.Collect"/>: lượt chặn không có
    /// chip nào nên <c>Collect</c> cố tình bỏ qua nó (xem <see cref="Evaluate"/>). Nhận diện bằng câu chữ là
    /// một giao ước code↔code mà compiler không kiểm được, nên <c>CoverageDeadQuestionLoopTests</c> chốt
    /// rằng CẢ HAI biến thể câu dẫn (hỏi lần đầu và quay lại) đều được đọc ra.
    /// </para>
    /// </summary>
    public static List<string> AskedGroups(IEnumerable<AgentConversation>? turns)
    {
        var groups = new List<string>();
        foreach (var turn in turns ?? Enumerable.Empty<AgentConversation>())
        {
            if (!ConversationTurnRenderer.IsAssistant(turn))
                continue;

            var message = turn.Message ?? string.Empty;
            var at = message.IndexOf(PendingMarker, StringComparison.Ordinal);
            if (at < 0)
                continue;

            var open = message.IndexOf('«', at);
            var close = open < 0 ? -1 : message.IndexOf('»', open);
            if (close < 0)
                continue;

            var label = message[(open + 1)..close].Trim();
            if (label.Length > 0)
                groups.Add(label);
        }

        return groups;
    }

    // Câu hỏi dựng sẵn khi chưa đủ. Đây là lượt BA mà người dùng THẬT SỰ đọc trên màn hình, nên nó phải
    // là một câu hỏi TRẢ LỜI ĐƯỢC, không phải một bản tin trạng thái:
    //
    // - Hỏi ĐÚNG phần "còn thiếu: …" mà distiller ghi trên dòng [MỘT PHẦN] — đó là thứ duy nhất bước soạn
    //   tài liệu còn phải tự đoán.
    // - KHÔNG đọc cả tóm tắt máy vào câu hỏi khi đã có mẩu "còn thiếu": tóm tắt là ghi chép của hệ thống về
    //   điều người dùng đã nói, phát lại nguyên khối trong khi đã hỏi được đúng chỗ hụt chỉ làm người đọc
    //   tưởng bị hỏi lại điều họ vừa trả lời.
    // - Nhãn nhóm chỉ được đứng làm CHỦ ĐỀ trong ngoặc, không được làm chính câu hỏi. «Dữ liệu / danh mục
    //   chính» là từ vựng nội bộ của bản đồ; người dùng nghiệp vụ chưa từng thấy nó. Ca thật đã gặp: câu
    //   này phát ba lần liên tiếp cho cùng một nhóm, người dùng đáp "mình không hiểu câu hỏi của bạn" hai
    //   lần rồi tự dán lại câu trả lời họ đã gõ từ 60 lượt trước.
    // - Kết thúc bằng dấu hỏi và chỉ hỏi MỘT nhóm; các nhóm còn lại chỉ được đếm để người dùng biết còn
    //   bao nhiêu việc, không hỏi dồn.
    //
    // BỐN NHÁNH, hẹp dần theo lượng thông tin bản đồ cho — và không nhánh nào được rơi về một câu trống
    // nghĩa. Bản trước chỉ có hai nhánh, nhánh dự phòng phát MỘT câu duy nhất cho cả 12 nhóm
    // (*"Anh/chị kể giúp mình phần này…"*): nó không nói được đang hỏi cái gì và trỏ tới "phần này" — đúng
    // cụm tham chiếu suông mà prompt cấm BA dùng. Ca thật ở dự án JD Library lượt 76: người dùng đáp
    // *"mình chưa hiểu câu hỏi, hãy hỏi rõ hơn"*, mất trắng một vòng ở cuối buổi phỏng vấn thứ 78. Nhánh đó
    // reachable với BẤT KỲ nhóm nào, chỉ cần lượt distill quên viết cụm "còn thiếu:" đúng một lần — nó là
    // định dạng do LLM xuất, không phải bất biến của code.
    /// <summary>
    /// Cụm chữ mà MỌI câu chặn của cổng đều chở, ở cả hai biến thể câu dẫn — đây là thứ
    /// <see cref="AskedGroups"/> dò để nhận ra lượt chặn của chính cổng. Sửa câu dẫn thì phải giữ nguyên cụm
    /// này, nếu không cổng mất sổ và quay về phát lại một câu ba lượt liền.
    /// </summary>
    private const string PendingMarker = "chưa đủ thông tin để khỏi phải tự đoán (nhóm «";

    private static string BuildPendingQuestion(CoverageMapItem item, int pendingCount, bool askedBefore)
    {
        var sb = new StringBuilder();
        // Quay lại một nhóm đã hỏi thì phải NÓI RA. Phát lại y nguyên câu dẫn cũ đọc lên như thể cổng không
        // nhớ mình vừa hỏi gì — mà đúng cái đó là thứ làm người dùng thôi trả lời.
        sb.Append(askedBefore
            ? "Mình quay lại một chỗ vẫn "
            : "Trước khi viết tài liệu, mình còn một chỗ ");
        sb.Append($"{PendingMarker}{item.Label}»");
        sb.Append(pendingCount == 1 ? "). " : $", còn {pendingCount} nhóm — mình hỏi từng nhóm một). ");
        sb.Append(AskFor(item));

        return sb.ToString();
    }

    /// <summary>
    /// Vế câu hỏi thật cho một dòng còn thiếu, thử bốn nhánh theo đúng thứ tự thông tin đáng tin cậy giảm
    /// dần. Tách khỏi <see cref="BuildPendingQuestion"/> để câu dẫn (nhãn nhóm + số nhóm còn lại) chỉ được
    /// dựng ở MỘT chỗ: bốn nhánh mà mỗi nhánh tự ghép lại câu dẫn là bốn chỗ để một nhánh quên nhãn nhóm.
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

        // 4. Nhãn không khớp nhóm nào (model tự nghĩ ra một tên) ⇒ không bịa câu hỏi về một nhóm không có
        //    trong checklist. Nhãn đã nằm trong câu dẫn nên câu này vẫn có chủ đề để bám.
        return "Anh/chị kể giúp mình phần này trong công việc thực tế hiện đang diễn ra thế nào?";
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

        return StripReopenMarker(missing).TrimEnd('.', ';', ',');
    }

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

    // Lượt CÓ NỘI DUNG mới nhất của hội thoại là lời mời bấm "Write Requirement" của BA ⇒ cổng đã pass
    // trên bản đồ tại bước chat và chưa có thông tin nào mới kể từ đó (người dùng gõ thêm thì lượt chat
    // luôn lưu một lượt BA mới đè lên vị trí cuối). Lượt lỗi LLM không bao giờ chứa lời mời nên không
    // cần lọc riêng. Thứ tự CreatedAt rồi Id — như ConversationTranscriptBuilder — vì CreatedAt có thể trùng.
    public static bool IsVerifiedInviteLatestTurn(IEnumerable<AgentConversation> conversations)
    {
        var lastTurn = conversations
            .Where(c => !string.IsNullOrWhiteSpace(c.Message))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .LastOrDefault();
        return lastTurn?.Role == "assistant" && IsWriteRequirementInvite(lastTurn.Message);
    }
}

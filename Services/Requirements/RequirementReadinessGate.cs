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
    /// </summary>
    public static RequirementReadiness Evaluate(string? coverageMap)
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

        // ★ cốt lõi hỏi trước — cùng thứ tự ưu tiên mà prompt chat hướng dẫn BA chọn câu hỏi kế tiếp.
        var pending = items
            .Where(x => x.Status is "MỘT PHẦN" or "CHƯA HỎI")
            .OrderByDescending(x => x.IsCore)
            .ToList();

        if (pending.Count == 0 && items.Any(x => x.Status == "RÕ"))
            return new RequirementReadiness { Ready = true };

        return new RequirementReadiness
        {
            Ready = false,
            Message = BuildPendingQuestion(pending),
            OpenEnded = true
        };
    }

    // Câu hỏi dựng sẵn khi chưa đủ. Đây là lượt BA mà người dùng THẬT SỰ đọc trên màn hình, nên nó phải
    // là một câu hỏi TRẢ LỜI ĐƯỢC, không phải một bản tin trạng thái:
    //
    // - Hỏi ĐÚNG phần "còn thiếu: …" mà distiller ghi trên dòng [MỘT PHẦN] — đó là thứ duy nhất bước soạn
    //   tài liệu còn phải tự đoán. Không có phần đó (dòng [CHƯA HỎI]) thì mới hỏi câu mở đầu của nhóm.
    // - KHÔNG đọc cả tóm tắt máy vào câu hỏi: tóm tắt là ghi chép của hệ thống về điều người dùng đã nói,
    //   phát lại nguyên khối chỉ làm người đọc tưởng bị hỏi lại điều họ vừa trả lời.
    // - Nhãn nhóm chỉ được đứng làm CHỦ ĐỀ trong ngoặc, không được làm chính câu hỏi. «Dữ liệu / danh mục
    //   chính» là từ vựng nội bộ của bản đồ; người dùng nghiệp vụ chưa từng thấy nó. Ca thật đã gặp: câu
    //   này phát ba lần liên tiếp cho cùng một nhóm, người dùng đáp "mình không hiểu câu hỏi của bạn" hai
    //   lần rồi tự dán lại câu trả lời họ đã gõ từ 60 lượt trước.
    // - Kết thúc bằng dấu hỏi và chỉ hỏi MỘT nhóm; các nhóm còn lại chỉ được đếm để người dùng biết còn
    //   bao nhiêu việc, không hỏi dồn.
    private static string BuildPendingQuestion(List<CoverageMapItem> pending)
    {
        // pending rỗng chỉ xảy ra khi bản đồ toàn [KHÔNG ÁP DỤNG] — bản đồ hỏng, không có gì để hỏi cụ thể.
        if (pending.Count == 0)
            return "Bản đồ khai thác yêu cầu đang trống thông tin đã rõ, nên chưa thể viết tài liệu. Bạn mô tả thêm về dự án trong khung chat giúp mình nhé.";

        var first = pending[0];
        var missing = ExtractMissingPart(first.Summary);

        var sb = new StringBuilder();
        sb.Append($"Trước khi viết tài liệu, mình còn một chỗ chưa đủ thông tin để khỏi phải tự đoán (nhóm «{first.Label}»");
        sb.Append(pending.Count == 1 ? "). " : $", còn {pending.Count} nhóm — mình hỏi từng nhóm một). ");

        sb.Append(string.IsNullOrWhiteSpace(missing)
            ? "Anh/chị kể giúp mình phần này trong công việc thực tế hiện đang diễn ra thế nào?"
            : $"Anh/chị cho mình hỏi thêm: {ToQuestion(missing)}");

        return sb.ToString();
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

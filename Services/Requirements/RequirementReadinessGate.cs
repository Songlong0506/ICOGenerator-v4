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
                Message = "Mình chưa tổng hợp được bản đồ khai thác yêu cầu cho dự án này, nên chưa thể viết tài liệu. Bạn trao đổi thêm một lượt trong khung chat rồi thử lại nhé."
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
            Message = BuildPendingQuestion(pending)
        };
    }

    // Câu hỏi dựng sẵn khi chưa đủ: nêu các nhóm còn thiếu rồi hỏi vào nhóm đầu tiên (kèm phần
    // "còn thiếu: …" mà distiller đã ghi trên dòng [MỘT PHẦN], nếu có) — người dùng biết chính xác
    // phải bổ sung gì thay vì một câu chặn chung chung.
    private static string BuildPendingQuestion(List<CoverageMapItem> pending)
    {
        // pending rỗng chỉ xảy ra khi bản đồ toàn [KHÔNG ÁP DỤNG] — bản đồ hỏng, không có gì để hỏi cụ thể.
        if (pending.Count == 0)
            return "Bản đồ khai thác yêu cầu đang trống thông tin đã rõ, nên chưa thể viết tài liệu. Bạn mô tả thêm về dự án trong khung chat giúp mình nhé.";

        var first = pending[0];
        var sb = new StringBuilder();
        sb.Append("Trước khi viết tài liệu, mình cần làm rõ thêm ");
        sb.Append(pending.Count == 1
            ? $"nhóm thông tin «{first.Label}»"
            : $"{pending.Count} nhóm thông tin: {string.Join(", ", pending.Select(x => $"«{x.Label}»"))}");
        sb.Append(". ");

        sb.Append($"Trước tiên về «{first.Label}»");
        sb.Append(string.IsNullOrWhiteSpace(first.Summary) ? "" : $" ({first.Summary})");
        sb.Append(" — bạn chia sẻ giúp mình nhé.");
        return sb.ToString();
    }

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

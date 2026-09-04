using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH của bất biến trung tâm phía yêu cầu: một nhóm KHÔNG được đứng ở <c>[RÕ]</c> trong
/// khi danh sách câu hỏi vẫn còn một mục MỞ gắn đúng nhóm đó. Chạy CUỐI chuỗi guard của đường ghi, sau khi
/// các guard trên đã dọn xong danh sách câu hỏi.
///
/// <para>
/// <b>Vì sao phải là máy chứ không chỉ là luật trong prompt.</b> <c>requirement-coverage.v5.md</c> đã ghi
/// luật này, và từ khi bản đồ với danh sách câu hỏi ra đời trong CÙNG một lời gọi thì model không còn bị
/// hai nguồn tin xung khắc nữa. Nhưng nó vẫn tự mâu thuẫn được trong chính một tài liệu: chấm một dòng
/// <c>[RÕ]</c> rồi vẫn để lại một câu hỏi của nhóm ấy. Cái giá của lần lỡ tay đó không đối xứng —
/// <c>[RÕ]</c> là lệnh CẤM BA hỏi lại nhóm đó (<c>requirement-chat.v4.md</c>), nên câu hỏi còn treo ấy
/// vĩnh viễn không bao giờ được lấy, và bước soạn tài liệu — vốn bị cấm giả định — nhận một khoảng trống
/// mà không cổng nào báo.
/// </para>
///
/// <para>
/// <b>Một chiều, không bao giờ nâng cấp.</b> Guard chỉ hạ <c>[RÕ]</c> xuống <c>[MỘT PHẦN]</c>. Hạ nhầm thì
/// BA hỏi thêm một câu và người dùng trả lời lần nữa; bỏ sót thì sinh ra một khoảng trống mà mọi tầng sau
/// tin là đã đủ — hai cái giá không cùng hạng. Cùng luật với các chốt chặn của
/// <see cref="BAChatReplyParser"/>.
/// </para>
///
/// <para>
/// <b>Không còn phải đối chiếu hai nhịp.</b> Khi câu hỏi còn được chắt bởi một lời gọi RIÊNG chạy ở hậu kỳ,
/// danh sách luôn cũ hơn bản đồ đúng một lượt: guard phải nhận thêm bản đồ TRƯỚC lượt distill và bỏ qua
/// mọi dòng vừa đổi, nếu không nó biến câu người dùng vừa trả lời thành câu chặn của cổng (ca thật JD
/// Libary 5, lượt 3→4: ba lượt bị đốt). Nay hai thứ ra cùng một lượt nên độ trễ ấy không tồn tại, và cả
/// tầng so-thân-dòng đi cùng nó đã được gỡ.
/// </para>
///
/// <para>
/// <b>Chạy ở đường GHI, không ở đường đọc.</b> Bản đồ là "nguồn chân lý duy nhất" mà cổng readiness, panel
/// tiến độ và bốn cổng bảng cùng đọc (<see cref="RequirementReadinessGate"/>,
/// <see cref="InterviewTableGate"/>). Lọc lúc đọc ở MỘT chỗ là để các tầng khác thấy một sự thật khác —
/// nên bản đã hạ cấp là bản được LƯU, và mọi consumer thấy cùng một trạng thái.
/// </para>
/// </summary>
public static class CoveragePendingGuard
{
    /// <summary>
    /// Hạ cấp mọi dòng <c>[RÕ]</c> còn câu hỏi MỞ gắn đúng nhóm đó. Sửa <paramref name="items"/> tại chỗ;
    /// danh sách câu hỏi chỉ được ĐỌC — quyền xoá một câu hỏi thuộc về các guard đứng trước.
    /// </summary>
    public static void Apply(IReadOnlyList<CoverageMapItem> items, IReadOnlyList<OpenQuestionEntry> questions)
    {
        if (items.Count == 0 || questions.Count == 0)
            return;

        // Mục không gắn được nhóm nào (model viết một tên lạ, đã bị đường ghi xoá về rỗng) đứng ngoài:
        // guard fail-open, nó không được phép hạ một dòng vì một cái nhãn vô nghĩa. IsSameGroup lo phần đó.
        var open = questions.Where(q => q.IsOpen && !string.IsNullOrWhiteSpace(q.Text)).ToList();
        if (open.Count == 0)
            return;

        foreach (var item in items)
        {
            if (!"RÕ".Equals(item.Status, StringComparison.Ordinal))
                continue;

            if (!open.Any(q => CoverageMapParser.IsSameGroup(item.Label, q.Group)))
                continue;

            Downgrade(item);
        }
    }

    // Hạ dòng xuống [MỘT PHẦN]. Phần đã ghi nhận và bằng chứng giữ NGUYÊN: chúng là căn cứ cho điều đã
    // biết, không phải cho phần còn thiếu, và xoá đi là làm panel tiến độ mất lý do vì sao nhóm này từng
    // được chấm [RÕ]. Câu hỏi thì không phải chép vào đâu cả — nó đã nằm sẵn ở danh sách riêng, và cổng
    // readiness đọc thẳng từ đó.
    private static void Downgrade(CoverageMapItem item)
    {
        var body = item.Known.Trim();
        if (body.Length > 0 && !body.EndsWith('.') && !body.EndsWith(';'))
            body += ".";

        item.Status = "MỘT PHẦN";
        item.Known = body;
    }
}

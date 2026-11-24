using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH cho các nhóm mà lượt distill không được tự chấm: nhóm không-hỏi
/// (<see cref="CoverageGroupKey.Unasked"/> — hiện chỉ có «Thông báo / nhắc nhở», KHÔNG chốt ở buổi phỏng vấn).
/// Chạy sau lượt distill và sau <see cref="CoverageQuestionGuard"/>, trước <see cref="CoveragePendingGuard"/>
/// và trước khi bản đồ được lưu.
///
/// <para>
/// Nhóm không-hỏi LUÔN ra khỏi guard ở trạng thái không chặn cổng readiness — <c>[KHÔNG ÁP DỤNG]</c> nếu
/// distiller đã chấm vậy, còn lại là <c>[RÕ]</c> giữ nguyên điều người dùng TỰ KỂ (phần <c>known</c> của
/// distiller). Mọi câu hỏi của nhóm bị xóa. Guard không ghi thêm chữ nào vào <c>known</c>: lời giải thích
/// "người nhận email chưa chốt" là chữ của GIAO DIỆN (resource <c>CoverageGroup.UnaskedNote.&lt;khoá&gt;</c>,
/// panel tiến độ hiện nó theo khoá), không phải dữ liệu của từng dự án — <c>known</c> chỉ chở lời người dùng.
/// </para>
///
/// <para>
/// <b>Vì sao nhóm thông báo không được chặn.</b> Sản phẩm người dùng nhận ở cuối buổi là một POC chạy
/// trên trình duyệt — không gửi email nào. Đòi người dùng quyết định ai nhận email lúc này là đòi một quyết
/// định họ không thấy tác dụng, ở đúng lúc họ mệt nhất. Hỏi lẻ thì còn tệ hơn: câu hỏi tự nhiên tách "ai
/// nhận" khỏi "khi nào gửi", và một danh sách vai trò trần đóng băng tài liệu thành "mọi thay đổi trạng
/// thái gửi cho cả bốn nhóm". Nên nhóm chỉ GHI NHẬN, và spec nói thẳng là người nhận chưa chốt
/// (<c>RequirementPromptBuilder.NotificationSection</c>).
/// </para>
///
/// <para>
/// <b>Vì sao prompt không đủ.</b> Lượt distill nhận BẢN ĐỒ HIỆN CÓ, và bản đồ ấy thường đã mang sẵn một
/// mẩu <c>còn thiếu: …</c> hay một câu hỏi cũ. Model cập nhật phần tóm tắt nhưng GIỮ NGUYÊN mẩu cũ — và
/// <see cref="RequirementReadinessGate"/> lấy nguyên mẩu đó làm câu chặn, tức một câu hỏi BA bị cấm hỏi,
/// nút "Write Requirement" khóa vĩnh viễn. Ca thật (JD Libary 7): dòng thông báo vừa nói "đã chốt To/CC"
/// vừa nói "còn thiếu: chưa rõ người nhận". Guard không đoán thêm gì — nó chỉ thi hành một quyết định
/// thiết kế thay vì trông chờ model đọc hộ.
/// </para>
///
/// <para>
/// Lời đính chính của người dùng về nhóm này đi vào <c>known</c> như mọi điều họ tự kể — không có đường hỏi
/// nào để mở lại.
/// </para>
/// </summary>
public static class CoverageUnaskedGroupGuard
{
    /// <summary>
    /// Đặt mọi dòng không-hỏi về trạng thái không chặn (xem doc class) và XOÁ mọi câu hỏi còn gắn vào chúng.
    /// </summary>
    public static void Apply(IReadOnlyList<CoverageMapItem> items, IList<OpenQuestionEntry> questions)
    {
        foreach (var item in items.Where(x => CoverageGroupKey.IsUnasked(x.Key)))
        {
            // Nhóm này không bao giờ được hỏi ⇒ mọi câu hỏi của nó là câu hỏi CHẾT, kể cả câu mang cụm đính
            // chính: không có lượt nào được phép hỏi nó, nên giữ lại là khóa nút "Write Requirement".
            RemoveQuestions(item, questions);

            // Distiller chấm [KHÔNG ÁP DỤNG] (ứng dụng một người dùng, người dùng nói rõ không cần báo ai) ⇒
            // giữ: đó là một quyết định, không phải một chỗ trống.
            if (item.Status == CoverageStatus.NotApplicable)
                continue;

            item.Status = CoverageStatus.Clear;
            item.Known = item.Known.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        }
    }

    private static void RemoveQuestions(CoverageMapItem item, IList<OpenQuestionEntry> questions)
    {
        for (var i = questions.Count - 1; i >= 0; i--)
        {
            if (CoverageMapParser.IsSameGroup(item.Label, questions[i].Group))
                questions.RemoveAt(i);
        }
    }
}

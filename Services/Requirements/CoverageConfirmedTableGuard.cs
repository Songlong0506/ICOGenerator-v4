using ICOGenerator.Localization;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH cho nhóm mà lượt distill không được tự chấm: «Thông báo / nhắc nhở» (KHÔNG chốt ở buổi
/// phỏng vấn — xem <see cref="NotificationDeferredNote"/>). Chạy sau lượt distill và sau
/// <see cref="CoveragePendingGuard"/>, trước khi bản đồ được lưu.
///
/// <para>
/// Nhóm này LUÔN ra khỏi guard ở trạng thái không chặn cổng readiness — <c>[KHÔNG ÁP
/// DỤNG]</c> nếu distiller đã chấm vậy, còn lại là <c>[RÕ]</c> kèm điều người dùng TỰ KỂ (phần
/// <c>known</c> của distiller) và câu ghim <see cref="NotificationDeferredNote"/>. Mọi câu hỏi của nhóm bị
/// xóa. Dự án cũ đã chốt bảng thông báo đầy đủ thì tóm tắt dựng từ bảng đó như trước.
/// </para>
///
/// <para>
/// <b>Vì sao nhóm thông báo không được chặn.</b> Sản phẩm người dùng nhận ở cuối buổi là một POC chạy
/// trên trình duyệt — không gửi email nào. Trước đây nhóm này chốt bằng một bảng cuối buổi (mỗi sự kiện
/// một dòng, To/CC chọn từ danh sách), tức đòi người dùng quyết định ai nhận email ở đúng lúc họ mệt nhất,
/// cho một thứ họ không thấy tác dụng trên bản demo. Hỏi lẻ thì còn tệ hơn: câu hỏi tự nhiên tách "ai
/// nhận" khỏi "khi nào gửi", và một danh sách vai trò trần đóng băng tài liệu thành "mọi thay đổi trạng
/// thái gửi cho cả bốn nhóm". Nên nhóm chỉ GHI NHẬN, và tài liệu nói thẳng là người nhận chưa chốt — các
/// tầng sau thấy một chỗ trống có tên, thay vì tự điền mặc định "gửi cho tất cả".
/// </para>
///
/// <para>
/// <b>Vì sao prompt không đủ.</b> Lượt distill nhận BẢN ĐỒ HIỆN CÓ, và bản đồ ấy thường đã mang sẵn một
/// mẩu <c>còn thiếu: …</c> hay một câu hỏi cũ. Model cập nhật phần tóm tắt nhưng GIỮ NGUYÊN mẩu cũ — và
/// <see cref="RequirementReadinessGate"/> lấy nguyên mẩu đó làm câu chặn, tức một câu hỏi BA bị cấm hỏi,
/// nút "Write Requirement" khóa vĩnh viễn. Ca thật (JD Libary 7): dòng thông báo vừa nói "đã chốt To/CC"
/// vừa nói "còn thiếu: chưa rõ người nhận". Guard không đoán thêm gì — nó chỉ đọc thẳng một dữ kiện tất
/// định thay vì trông chờ model đọc hộ.
/// </para>
///
/// <para>
/// Lời đính chính của người dùng về nhóm này đi vào <c>known</c> như mọi điều họ tự kể — không có đường hỏi
/// nào để mở lại.
/// </para>
/// </summary>
public static class CoverageConfirmedTableGuard
{
    /// <summary>
    /// Bằng chứng ghim cho dòng thông báo của dự án CŨ đã chốt bảng thông báo — một GIAO ƯỚC với prompt
    /// <c>requirement-coverage.v5.md</c>: guard này ghi cụm ấy, lượt distill kế tiếp đọc lại chính nó.
    /// </summary>
    public const string NotificationEvidence = "bảng thông báo người dùng đã chốt";

    /// <summary>
    /// Câu ghim cuối dòng «Thông báo / nhắc nhở» khi không có bảng thông báo đã chốt. Đi thẳng vào ngữ cảnh
    /// sinh Product Brief, nên đây là câu mà tài liệu và mọi tầng sau đọc được: chỗ trống CÓ TÊN, kèm ai
    /// sẽ lấp nó. Cũng là giao ước với <c>requirement-coverage.v5.md</c> (distiller không được tự thêm câu
    /// nào khác về người nhận).
    /// </summary>
    public const string NotificationDeferredNote =
        "người nhận email của từng sự kiện CHƯA CHỐT ở buổi phỏng vấn — đội Dev xác nhận với người dùng sau POC";

    /// <summary>
    /// Đặt dòng thông báo về trạng thái không chặn (xem doc class) và XOÁ mọi câu hỏi còn gắn vào nhóm ấy.
    /// </summary>
    public static void Apply(
        IReadOnlyList<CoverageMapItem> items,
        IList<OpenQuestionEntry> questions,
        string? notificationMapJson)
    {
        foreach (var item in items)
        {
            if (item.Key == CoverageGroupKey.Notifications)
                SettleNotifications(item, questions, notificationMapJson);
        }
    }

    private static void SettleNotifications(
        CoverageMapItem item, IList<OpenQuestionEntry> questions, string? notificationMapJson)
    {
        // Nhóm này không bao giờ được hỏi ⇒ mọi câu hỏi của nó là câu hỏi CHẾT, kể cả câu mang cụm đính
        // chính: không có lượt nào được phép hỏi nó, nên giữ lại là khóa nút "Write Requirement".
        RemoveQuestions(item, questions);

        // Distiller chấm [KHÔNG ÁP DỤNG] (ứng dụng một người dùng, người dùng nói rõ không cần báo ai) ⇒ giữ:
        // đó là một quyết định, không phải một chỗ trống.
        if (item.Status == CoverageStatus.NotApplicable)
            return;

        // Dự án CŨ đã chốt bảng thông báo đầy đủ ⇒ tóm tắt theo bảng, như trước khi bỏ bảng. Dòng đã [RÕ]
        // thì giữ chữ của nó (không chặn cổng, không có gì để sửa).
        if (IsNotificationTableComplete(notificationMapJson))
        {
            if (item.Status != CoverageStatus.Clear)
            {
                item.Status = CoverageStatus.Clear;
                item.Known = new List<string> { NotificationSummary(notificationMapJson), NotificationEvidence };
            }
            return;
        }

        // Giữ ĐÚNG điều người dùng tự kể (phần known của distiller), bỏ các bản cũ của hai câu ghim máy để
        // không nhân bản qua mỗi lượt distill, rồi ghim câu "chưa chốt" ở cuối.
        var known = item.Known
            .Where(k => !string.IsNullOrWhiteSpace(k)
                && !k.Contains(NotificationDeferredNote, StringComparison.OrdinalIgnoreCase)
                && !k.Contains(NotificationEvidence, StringComparison.OrdinalIgnoreCase))
            .ToList();
        known.Add(NotificationDeferredNote);

        item.Status = CoverageStatus.Clear;
        item.Known = known;
    }

    private static void RemoveQuestions(CoverageMapItem item, IList<OpenQuestionEntry> questions)
    {
        for (var i = questions.Count - 1; i >= 0; i--)
        {
            if (CoverageMapParser.IsSameGroup(item.Label, questions[i].Group))
                questions.RemoveAt(i);
        }
    }

    /// <summary>
    /// Bảng thông báo (dự án cũ) đã chốt VÀ còn nguyên bất biến của nó: không dòng tích "Cần" nào trống
    /// người nhận. Thiếu một trong hai thì dòng đi đường "chưa chốt" như mọi dự án mới.
    /// </summary>
    private static bool IsNotificationTableComplete(string? json)
    {
        var rows = NotificationMapBuilder.Parse(json);
        return rows.Count > 0 && NotificationMapBuilder.MissingRecipients(rows).Count == 0;
    }

    // "Đã chốt bảng thông báo: 4 sự kiện gửi email kèm người nhận riêng; 1 sự kiện người dùng chọn không
    // gửi." — số đếm lấy từ chính bảng vừa lưu, nên người dùng kiểm lại được bằng cách đếm dòng.
    private static string NotificationSummary(string? json)
    {
        var rows = NotificationMapBuilder.Parse(json);
        var sent = rows.Count(r => r.Needed && r.To.Count > 0);
        var silent = rows.Count(r => !r.Needed);

        var parts = new List<string>();
        if (sent > 0)
            parts.Add(Loc.T("CoverageConfirmedTableGuard.NotificationSummary", sent));
        if (silent > 0)
            parts.Add(Loc.T("CoverageConfirmedTableGuard.NotificationSummary.2", silent));

        return parts.Count == 0
            ? Loc.T("CoverageConfirmedTableGuard.NotificationSummary.3")
            : Loc.T("CoverageConfirmedTableGuard.NotificationSummary.4", string.Join("; ", parts));
    }
}

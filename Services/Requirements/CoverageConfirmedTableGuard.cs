using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH cho HAI nhóm chốt-bằng-bảng: «Phân quyền theo nghiệp vụ» và «Thông báo / nhắc nhở».
/// Bảng của nhóm đã được chốt trong DB ⇒ dòng bản đồ của nhóm đó phải đứng ở <c>[RÕ]</c>, và phần
/// <c>còn thiếu:</c> — nếu còn sót lại — bị xóa. Chạy sau lượt distill và sau
/// <see cref="CoveragePendingGuard"/>, trước khi bản đồ được lưu.
///
/// <para>
/// <b>Vì sao prompt không đủ.</b> <c>requirement-coverage.v3.md</c> đã ghi luật một chiều cho cả hai nhóm
/// ("có khối bảng đã chốt ⇒ <c>[RÕ]</c>, <b>không có ngoại lệ nào</b>"), và
/// <see cref="RequirementCoverageService"/> đính đúng khối đó vào mọi lượt distill. Nhưng lượt distill
/// nhận thêm BẢN ĐỒ HIỆN CÓ, và bản đồ ấy thường đã mang sẵn một mẩu <c>còn thiếu: …</c> từ lúc bảng chưa
/// chốt (do chính distiller viết, hoặc do <see cref="CoveragePendingGuard"/> ghi vào từ một điểm tồn
/// đọng). Model cập nhật phần tóm tắt theo bảng mới nhưng GIỮ NGUYÊN mẩu cũ — dòng thành ra tự mâu thuẫn.
/// </para>
///
/// <para>
/// Ca thật (dự án JD Libary 7, lượt 100–102): người dùng vừa gửi bảng thông báo với đủ To/CC cho cả 4 sự
/// kiện cần gửi và tắt sự kiện thứ 5, bảng đã lưu, khối "đã chốt" đã đính vào lượt distill — mà dòng bản
/// đồ vẫn là
/// <c>«Thông báo / nhắc nhở: [MỘT PHẦN] … đã chốt To/CC riêng từng sự kiện … còn thiếu: Chưa rõ người nhận
/// cho từng sự kiện thông báo»</c>. Cùng một dòng vừa nói đã chốt vừa nói chưa rõ.
/// </para>
///
/// <para>
/// <b>Vì sao đó là vòng lặp CHẾT chứ không phải một dòng xấu.</b>
/// <see cref="RequirementReadinessGate"/> lấy nguyên mẩu <c>còn thiếu:</c> làm câu chặn, nên lượt kế tiếp
/// của BA là *"Chưa rõ người nhận cho từng sự kiện thông báo — anh/chị cho mình xin thông tin này nhé?"*
/// — hỏi lại đúng thứ người dùng vừa trả lời. Và không đường nào thoát: <see cref="NotificationMapGate"/>
/// không bao giờ bày lại một bảng đã chốt, còn khối "đã chốt" cấm BA hỏi lẻ nhóm này. Người dùng trả lời
/// bao nhiêu lần thì cũng nhận lại đúng câu đó, nút "Write Requirement" khóa vĩnh viễn.
/// </para>
///
/// <para>
/// <b>Vì sao được phép NÂNG cấp, khác luật một chiều của <see cref="CoveragePendingGuard"/>.</b> Guard kia
/// hạ <c>[RÕ]</c> vì nó đối chiếu hai bản chắt của LLM với nhau — không bên nào là sự thật, nên nó chọn bên
/// an toàn. Ở đây bằng chứng KHÔNG do LLM chắt: nó là bảng người dùng tự tay bấm từng ô, nằm sẵn trong DB,
/// và đường gửi bảng đã bảo đảm nó ĐỦ (<see cref="NotificationMapBuilder.MissingRecipients"/> chặn mọi lần
/// lưu còn dòng tích "Cần" mà chưa chọn người nhận). Guard này không đoán thêm gì — nó chỉ đọc thẳng một
/// dữ kiện tất định thay vì trông chờ model đọc hộ.
/// </para>
///
/// <para>
/// <b>Tóm tắt của dòng được dựng lại TỪ BẢNG, không giữ chữ của model.</b> Đây là hai nhóm mà một câu tóm
/// tắt sai gây thiệt hại nặng nhất ("mọi thay đổi trạng thái gửi cho cả bốn nhóm" — xem
/// <see cref="NotificationMapRow"/>), và dòng bản đồ thì đi thẳng vào ngữ cảnh mọi lượt chat sau. Dựng
/// bằng số đếm lấy từ chính bảng vừa lưu thì dòng không bao giờ nói được điều bảng không chứa, và người
/// dùng kiểm được nó bằng cách đếm lại.
/// </para>
///
/// <para>
/// <b>Hai chỗ guard cố ý KHÔNG đụng vào:</b> dòng đã <c>[RÕ]</c>/<c>[KHÔNG ÁP DỤNG]</c> (không dòng nào
/// trong hai trạng thái đó chặn cổng readiness), và dòng mang cụm
/// <see cref="AskedQuestionHistory.ReopenNote"/> — đó là người dùng vừa nói BA hiểu sai nhóm này, tức
/// đúng một lần họ chủ động mở lại đường hỏi. Đè lên nó là cướp mất cái đường ấy.
/// </para>
/// </summary>
public static class CoverageConfirmedTableGuard
{
    /// <summary>Bằng chứng ghim cho dòng phân quyền — đúng chữ mà <c>requirement-coverage.v3.md</c> đòi.</summary>
    private const string PermissionEvidence = "bảng phân quyền người dùng đã chốt";

    /// <summary>Bằng chứng ghim cho dòng thông báo.</summary>
    private const string NotificationEvidence = "bảng thông báo người dùng đã chốt";

    /// <summary>
    /// Nâng dòng của các nhóm đã có bảng chốt lên <c>[RÕ]</c> và viết lại tóm tắt theo chính bảng đó.
    /// Chưa bảng nào được chốt ⇒ trả nguyên bản đồ.
    /// </summary>
    public static string? Apply(string? coverageMap, string? permissionMatrixJson, string? notificationMapJson)
    {
        if (string.IsNullOrWhiteSpace(coverageMap))
            return coverageMap;

        var settled = new List<(string Label, string Summary, string Evidence)>();

        if (PermissionMatrixGate.IsConfirmed(permissionMatrixJson))
            settled.Add((PermissionMatrixGate.PermissionGroupLabel,
                PermissionSummary(permissionMatrixJson), PermissionEvidence));

        if (IsNotificationTableComplete(notificationMapJson))
            settled.Add((NotificationMapGate.NotificationGroupLabel,
                NotificationSummary(notificationMapJson), NotificationEvidence));

        if (settled.Count == 0)
            return coverageMap;

        var lines = coverageMap.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = CoverageMapParser.LineRegex().Match(lines[i].Trim());
            if (!match.Success)
                continue;

            var label = match.Groups["label"].Value.Trim();
            var row = settled.FirstOrDefault(x => IsSameGroup(label, x.Label));
            if (row.Label == null)
                continue;

            // [RÕ] và [KHÔNG ÁP DỤNG] đều không chặn cổng ⇒ không có gì để sửa.
            var status = CoverageMapParser.NormalizeStatus(match.Groups["status"].Value);
            if (status is "RÕ" or "KHÔNG ÁP DỤNG")
                continue;

            // Người dùng vừa đính chính nhóm này ⇒ để nguyên đường hỏi lại mà họ vừa mở ra.
            if (match.Groups["summary"].Value.Contains(AskedQuestionHistory.ReopenNote, StringComparison.OrdinalIgnoreCase))
                continue;

            lines[i] = "- " + (match.Groups["core"].Success ? "★ " : string.Empty)
                + label + ": [RÕ] " + row.Summary + " {nguồn: " + row.Evidence + "}";
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Bảng thông báo đã chốt VÀ còn nguyên bất biến của nó. Đường gửi
    /// (<c>ConfirmNotificationMapUseCase</c>) không cho lưu một bảng còn dòng tích "Cần" mà chưa
    /// chọn người nhận, nên phép thử thứ hai chỉ chạm tới dữ liệu ghi trước khi bất biến đó tồn tại — và ở
    /// đúng ca ấy guard phải im: một dòng như thế nghĩa là "cần báo nhưng chưa chốt được ai", tức nhóm còn
    /// thiếu thật.
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
            parts.Add($"{sent} sự kiện gửi email kèm người nhận riêng");
        if (silent > 0)
            parts.Add($"{silent} sự kiện người dùng chọn không gửi");

        return parts.Count == 0
            ? "Đã chốt bảng thông báo theo từng sự kiện."
            : "Đã chốt bảng thông báo theo từng sự kiện: " + string.Join("; ", parts) + ".";
    }

    // "Đã chốt bảng phân quyền: 34 chức năng trên 13 màn hình, 5 vai trò."
    private static string PermissionSummary(string? json)
    {
        var rows = PermissionMatrixBuilder.Parse(json);
        var screens = rows
            .Select(r => (r.Screen ?? string.Empty).Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var roles = PermissionMatrixBuilder.Roles(json).Count;

        return $"Đã chốt bảng phân quyền theo từng ô: {rows.Count} chức năng trên {screens} màn hình, "
            + $"{roles} vai trò.";
    }

    // So khớp nhãn hai chiều bằng TIỀN TỐ, cùng lý do với CoveragePendingGuard.FindGap và
    // InterviewTableGate.IsClear: một lượt distill viết "Thông báo" thay vì "Thông báo / nhắc nhở" không
    // được phép làm guard câm trong im lặng.
    private static bool IsSameGroup(string label, string group)
        => label.StartsWith(group, StringComparison.OrdinalIgnoreCase)
           || group.StartsWith(label, StringComparison.OrdinalIgnoreCase);
}

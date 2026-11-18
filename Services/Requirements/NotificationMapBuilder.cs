using System.Text;
using System.Text.Json;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Đọc "bảng thông báo / nhắc nhở" mà các dự án CŨ đã chốt ở buổi phỏng vấn — mỗi sự kiện một dòng, người
/// nhận chính (To) và đồng gửi (CC). Xem <see cref="NotificationMapRow"/>.
///
/// <para>
/// <b>Bảng không còn được bày ra nữa.</b> Sản phẩm của buổi phỏng vấn là một POC không gửi email nào, nên
/// nhóm «Thông báo / nhắc nhở» không chốt ở đó: nó chỉ ghi nhận điều người dùng tự kể, và người nhận email
/// được ghi là CHƯA CHỐT cho đội Dev xác nhận sau POC (xem
/// <see cref="CoverageConfirmedTableGuard.NotificationDeferredNote"/>). Class này chỉ còn phần ĐỌC, để các
/// dự án đã chốt bảng trước đó vẫn đưa được bảng ấy vào ngữ cảnh chat, lượt distill bản đồ bao phủ, bản
/// xuất hội thoại và prompt sinh AI Design Spec.
/// </para>
///
/// <para>
/// <b>BẤT BIẾN của một bảng đã lưu: mỗi dòng chỉ có HAI trạng thái</b> — bỏ tích (<c>Needed = false</c>:
/// không gửi email, một quyết định hợp lệ) hoặc còn tích kèm <c>To</c> KHÔNG RỖNG. Đường gửi cũ chặn mọi
/// lần lưu vi phạm; <see cref="MissingRecipients"/> còn lại để guard nhận ra dữ liệu ghi trước khi bất biến
/// đó tồn tại.
/// </para>
/// </summary>
public static class NotificationMapBuilder
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Đọc JSON bảng thông báo đã lưu. null/rỗng/hỏng ⇒ mảng rỗng.</summary>
    public static List<NotificationMapRow> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<NotificationMapRow>();

        try
        {
            var rows = JsonSerializer.Deserialize<List<NotificationMapRow>>(json, ReadOptions)
                ?? new List<NotificationMapRow>();
            return rows.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Event)).ToList();
        }
        catch
        {
            return new List<NotificationMapRow>();
        }
    }

    /// <summary>Dự án này đã chốt bảng thông báo chưa (chỉ còn đúng với dự án cũ).</summary>
    public static bool IsConfirmed(string? json) => Parse(json).Count > 0;

    /// <summary>
    /// Các dòng VI PHẠM bất biến của bảng: còn tích "Cần" mà chưa có người nhận chính. Chỉ chạm tới dữ liệu
    /// ghi trước khi bất biến được chặn ở đường gửi.
    /// </summary>
    public static List<NotificationMapRow> MissingRecipients(IEnumerable<NotificationMapRow>? rows)
        => (rows ?? Enumerable.Empty<NotificationMapRow>())
            .Where(r => r != null && r.Needed && r.To.Count == 0)
            .ToList();

    /// <summary>Tên một sự kiện như người dùng từng đọc thấy trên bảng ("Đơn — "Chờ duyệt" (khi …)").</summary>
    public static string EventLabel(NotificationMapRow row) => RenderEvent(row);

    /// <summary>
    /// Khối ngữ cảnh gắn vào mọi lượt chat, vào lượt distill bản đồ bao phủ, và vào prompt sinh AI Design
    /// Spec của dự án đã chốt bảng. Trả null khi không có bảng (mọi dự án mới).
    ///
    /// <para>
    /// Khối này là một lệnh CẤM HỎI TUYỆT ĐỐI cho cả nhóm: bất biến của bảng bảo đảm mọi dòng đã lưu đều đã
    /// trả lời xong — hoặc "không gửi", hoặc có người nhận. Hai loại đều được GỌI TÊN ở đây, vì mặc định im
    /// lặng của mọi tầng phía sau là "có thay đổi thì báo": một sự kiện người dùng đã tắt mà khối này không
    /// nhắc tới sẽ được bước soạn tài liệu bật lại.
    /// </para>
    /// </summary>
    public static string? RenderConfirmedBlock(string? json)
    {
        var rows = Parse(json);
        if (rows.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Bảng thông báo / nhắc nhở đã được NGƯỜI DÙNG CHỐT (đừng hỏi lại) ---");
        sb.AppendLine("Mỗi dòng: sự kiện · người nhận chính (To) · người nhận đồng gửi (CC). Kênh gửi duy nhất "
            + "của nền tảng là EMAIL nên không có cột kênh. Người nhận là các mục của DANH SÁCH NGƯỜI NHẬN do "
            + "chính người dùng chốt: hoặc một QUAN HỆ với bản ghi (\"Người tạo\", \"Quản lý trực tiếp của "
            + "người tạo\"), hoặc tên một người/nhóm họ tự viết ra. TUYỆT ĐỐI không tự suy rộng một mục thành "
            + "\"mọi người mang vai đó\" — không sự kiện nào ở đây gửi email cho cả một vai trò.");

        foreach (var row in rows.Where(r => r.Needed && r.To.Count > 0))
            sb.AppendLine("* " + RenderRow(row));

        var silent = rows.Where(r => !r.Needed).ToList();
        if (silent.Count > 0)
            sb.AppendLine("* KHÔNG gửi thông báo ở các sự kiện sau — đây là quyết định của người dùng, không "
                + "phải chỗ còn thiếu: " + string.Join("; ", silent.Select(RenderEvent)));

        return sb.ToString().TrimEnd();
    }

    private static string RenderEvent(NotificationMapRow row)
    {
        var entity = string.IsNullOrWhiteSpace(row.Entity) ? string.Empty : $"{row.Entity.Trim()} — ";
        var trigger = string.IsNullOrWhiteSpace(row.Trigger) ? string.Empty : $" (khi {row.Trigger.Trim()})";
        return $"{entity}\"{row.Event.Trim()}\"{trigger}";
    }

    // Chỉ gọi cho dòng CÓ người nhận — bất biến của bảng bảo đảm không còn dòng "cần gửi mà To rỗng" nào
    // để phải kể.
    private static string RenderRow(NotificationMapRow row)
    {
        var cc = row.Cc.Count > 0 ? $"; CC: {string.Join(", ", row.Cc)}" : string.Empty;
        return $"{RenderEvent(row)} ⇒ To: {string.Join(", ", row.To)}{cc}";
    }
}

namespace ICOGenerator.Domain;

/// <summary>
/// Link chia sẻ bản demo POC cho người KHÔNG có tài khoản trong hệ thống (sếp, người dùng cuối, khách
/// hàng nội bộ) — họ mở được bản demo và ghim góp ý bằng tên mình, không thấy gì khác của dự án.
///
/// <para>
/// Vì sao cần: nghiệm thu thật luôn có nhiều người, nhưng trang POC Review bị chặn theo quyền truy cập
/// project (chỉ người tạo + admin). Trước đây muốn cho người khác xem chỉ có hai cách — cấp tài khoản,
/// hoặc chụp màn hình gửi qua chat — và cả hai đều làm mất chính thứ giá trị nhất của POC: người xem tự
/// bấm thử rồi chỉ đúng chỗ chưa ổn.
/// </para>
///
/// <para>
/// <b>Token là chìa khoá dạng "ai có link nấy vào được"</b> nên nó bị giới hạn ba lớp: chỉ mở được đúng
/// bản demo của MỘT project, luôn có hạn dùng (<see cref="ExpiresAtUtc"/>), và thu hồi được bất cứ lúc
/// nào (<see cref="RevokedAtUtc"/>). Token lưu nguyên văn để người tạo copy lại link về sau — đây là
/// đánh đổi có chủ ý giữa tiện dụng và bí mật, chấp nhận được vì thứ nó mở ra là một bản demo dữ liệu
/// giả, không phải dữ liệu thật của tổ chức.
/// </para>
/// </summary>
public class PocShareLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    /// <summary>Chuỗi ngẫu nhiên trong URL (base64url, 32 byte entropy). Duy nhất toàn hệ thống.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Ghi chú người tạo đặt cho link ("gửi anh Hùng phòng NS") — để biết link nào của ai khi thu hồi.</summary>
    public string? Label { get; set; }

    public string? CreatedByUsername { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Hết hạn là BẮT BUỘC — một link chia sẻ sống mãi là một lỗ hổng chờ ngày phát tác.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Đã thu hồi (null = còn hiệu lực nếu chưa hết hạn).</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Còn dùng được không, tại thời điểm <paramref name="nowUtc"/>.</summary>
    public bool IsUsable(DateTime nowUtc) => RevokedAtUtc == null && ExpiresAtUtc > nowUtc;
}

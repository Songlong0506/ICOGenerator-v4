namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Bốn trạng thái của một dòng bản đồ bao phủ, và phép chuẩn hoá đưa mọi cách viết của model về đúng bốn
/// giá trị ấy. Cùng khuôn với <see cref="FlowKind"/> và <see cref="PermissionScope"/>: chuỗi chứ không
/// phải enum, vì giá trị này đã nằm trong JSON của <c>Project.RequirementCoverageMap</c> — đổi tên là làm
/// hỏng bản đồ của mọi dự án đang dở.
///
/// <para>
/// <b>Vì sao phải là MỘT chỗ.</b> Bốn chuỗi này đi qua gần như mọi tầng của phía yêu cầu: model trả về
/// (<see cref="CoverageMapEntry.Status"/>) → <c>CoverageMapParser</c> chuẩn hoá → sáu guard hạ/nâng trạng
/// thái → các cổng bảng và cổng <c>Write Requirement</c> đọc để quyết định → panel "Tiến độ khai thác"
/// render, cả bản server lẫn bản <c>renderCoverage()</c> trong <c>requirements.js</c>. Trước đây chúng là
/// chuỗi trần lặp ở 11 file C# cộng Razor cộng JS, không có chỗ nào khai báo — nên không có cách nào sửa
/// một giá trị mà chắc rằng đã sửa hết, và một chỗ sót thì cổng im lặng đọc sai trạng thái chứ không nổ
/// ra lỗi. Phía trình duyệt nhận đúng bốn giá trị này qua khối từ vựng
/// <see cref="RequirementScreenText"/>, không tự khai lại.
/// </para>
/// </summary>
public static class CoverageStatus
{
    /// <summary>Nhóm đã khai thác đủ theo chuẩn <c>[RÕ]</c> của chính nó.</summary>
    public const string Clear = "RÕ";

    /// <summary>Đã chạm tới nhưng còn hụt — dòng thường kèm câu hỏi gọi tên phần còn thiếu.</summary>
    public const string Partial = "MỘT PHẦN";

    /// <summary>Chưa hỏi câu nào. Cũng là giá trị AN TOÀN cho mọi chuỗi lạ — xem <see cref="Normalize"/>.</summary>
    public const string NotAsked = "CHƯA HỎI";

    /// <summary>Nhóm không áp dụng với dự án này; tính là đã xong cho thanh tiến độ.</summary>
    public const string NotApplicable = "KHÔNG ÁP DỤNG";

    /// <summary>Bốn giá trị hợp lệ, theo thứ tự "đã xong dần" — dùng cho khối từ vựng gửi xuống trình duyệt.</summary>
    public static readonly string[] All = { Clear, Partial, NotAsked, NotApplicable };

    /// <summary>
    /// Kéo tên trạng thái model viết về đúng bốn giá trị trên; giá trị lạ ⇒ <see cref="NotAsked"/>.
    /// <para>
    /// Nhận cả bản KHÔNG DẤU vì model tiếng Việt thỉnh thoảng nhả vậy, và rơi về <see cref="NotAsked"/>
    /// chứ không về <see cref="Clear"/> là fail-closed có chủ đích: đọc nhầm thành "đã rõ" thì cổng
    /// <c>Write Requirement</c> mở cho một nhóm chưa ai hỏi, còn đọc nhầm thành "chưa hỏi" thì cùng lắm
    /// BA hỏi lại một câu.
    /// </para>
    /// </summary>
    public static string Normalize(string? raw)
    {
        var status = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return status switch
        {
            Clear or "RO" => Clear,
            Partial or "MOT PHAN" => Partial,
            NotApplicable or "KHONG AP DUNG" => NotApplicable,
            _ => NotAsked
        };
    }
}

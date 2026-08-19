
namespace ICOGenerator.Domain;

/// <summary>
/// Tài khoản người dùng đăng nhập. Không lưu mật khẩu VÀ KHÔNG lưu vai trò: đăng nhập do provider ngoài
/// quyết định — chế độ Local tự đăng nhập bằng tài khoản seed sẵn (dev/nội bộ), chế độ IdentityServer
/// xác thực SSO rồi đồng bộ user. Vai trò của một người CHỈ tồn tại trong claim của phiên đăng nhập
/// (xem <c>PermissionService</c>): mỗi lần đăng nhập SSO, role claim của IdentityServer được ánh xạ
/// thẳng thành claim <c>ClaimTypes.Role</c>, không có bản sao nào trong DB. Hệ quả phải nhớ: KHÔNG truy
/// vấn được "ai đang giữ vai trò X" từ DB — mọi thứ cần biết vai trò của người đang OFFLINE (ví dụ chọn
/// người nhận thông báo) phải dùng tiêu chí khác.
/// Bản ghi này chỉ giữ những gì SỐNG LÂU HƠN một phiên: danh tính (Username), đơn vị tổ chức, trí nhớ
/// người dùng và tùy chọn thông báo.
/// Bộ user được seed sẵn trong DbInitializer (superadmin/admin/teamdev/user), chưa có UI tạo user.
/// </summary>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Đơn vị tổ chức của user, đồng bộ từ claim "department" của IdentityServer mỗi lần đăng nhập SSO
    /// (ví dụ "HcP/ICO3"). null khi không có claim (đăng nhập Local) hoặc IdP không phát department.
    /// </summary>
    public string? OrgUnitName { get; set; }

    // Bộ nhớ dài hạn về CHÍNH người dùng này, gom XUYÊN SUỐT mọi project họ tạo (khác với
    // Project.ConversationSummary chỉ nhớ trong một dự án). Là một hồ sơ ngắn gọn các sự thật BỀN về
    // user — vai trò, lĩnh vực/ngành, tổ chức, văn phong/định dạng tài liệu họ ưa, thuật ngữ hay dùng,
    // ràng buộc lặp lại — được BA chắt lọc DẦN từ hội thoại và nạp lại ở mọi cuộc để "càng nói càng hiểu
    // user". null = chưa chắt lọc được gì. Xem UserMemoryService.
    public string? UserMemory { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ---- Tùy chọn thông báo (mỗi user tự quản ở trang Preferences) ----
    // Mặc định GIỮ NGUYÊN hành vi cũ: vẫn nhận chuông in-app cho mọi loại sự kiện; email cá nhân TẮT
    // (opt-in). Kênh Teams / danh sách email cố định do admin cấu hình, KHÔNG chịu ảnh hưởng của các cờ này.

    /// <summary>Email cá nhân để nhận thông báo (khi bật <see cref="NotifyByEmail"/>). Trống ⇒ không route email tới user.</summary>
    public string? Email { get; set; }

    /// <summary>Nhận thông báo qua chuông in-app.</summary>
    public bool NotifyInApp { get; set; } = true;

    /// <summary>Nhận thông báo qua email cá nhân (opt-in; cần <see cref="Email"/>).</summary>
    public bool NotifyByEmail { get; set; }

    /// <summary>Nhận sự kiện "cổng chờ duyệt".</summary>
    public bool NotifyOnGate { get; set; } = true;

    /// <summary>Nhận sự kiện "workflow hoàn tất".</summary>
    public bool NotifyOnCompleted { get; set; } = true;

    /// <summary>Nhận sự kiện "workflow thất bại".</summary>
    public bool NotifyOnFailed { get; set; } = true;
}

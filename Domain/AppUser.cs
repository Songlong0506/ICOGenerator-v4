using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Tài khoản người dùng đăng nhập. Không lưu mật khẩu: đăng nhập do provider ngoài quyết định —
/// chế độ Local tự đăng nhập bằng tài khoản 'admin' seed sẵn (dev/nội bộ), chế độ IdentityServer
/// xác thực SSO rồi đồng bộ user. Mỗi user gắn đúng một <see cref="UserRole"/>.
/// Bộ user được seed sẵn trong DbInitializer (admin/teamdev/user), chưa có UI tạo user.
/// </summary>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.User;
    public bool IsActive { get; set; } = true;
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

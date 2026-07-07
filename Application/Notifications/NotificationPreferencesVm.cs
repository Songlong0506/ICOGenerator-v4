namespace ICOGenerator.Application.Notifications;

/// <summary>Tùy chọn thông báo của một người dùng (trang tự phục vụ). Cũng dùng làm model của form lưu.</summary>
public sealed class NotificationPreferencesVm
{
    public string? Email { get; set; }

    public bool NotifyInApp { get; set; } = true;
    public bool NotifyByEmail { get; set; }

    public bool NotifyOnGate { get; set; } = true;
    public bool NotifyOnCompleted { get; set; } = true;
    public bool NotifyOnFailed { get; set; } = true;

    /// <summary>True nếu admin đã bật kênh email hệ thống — để trang gợi ý rằng tùy chọn email mới có tác dụng.</summary>
    public bool EmailChannelConfigured { get; set; }
}

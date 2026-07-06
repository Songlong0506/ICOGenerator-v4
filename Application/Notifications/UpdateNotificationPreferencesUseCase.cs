using System.Net.Mail;
using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Notifications;

public enum UpdatePreferencesResult
{
    Ok,
    InvalidEmail,   // bật email nhưng địa chỉ trống/sai định dạng
    NotFound        // không tìm thấy user (bất thường với người đã đăng nhập)
}

/// <summary>
/// Lưu tùy chọn thông báo của CHÍNH người dùng (ràng theo username). Nếu bật nhận email cá nhân thì bắt
/// buộc có địa chỉ hợp lệ để tránh opt-in "chết".
/// </summary>
public class UpdateNotificationPreferencesUseCase
{
    private readonly AppDbContext _db;

    public UpdateNotificationPreferencesUseCase(AppDbContext db) => _db = db;

    public async Task<UpdatePreferencesResult> ExecuteAsync(string username, NotificationPreferencesVm input, CancellationToken cancellationToken = default)
    {
        var email = input.Email?.Trim();

        if (input.NotifyByEmail && !IsValidEmail(email))
            return UpdatePreferencesResult.InvalidEmail;

        // Địa chỉ có nhập (dù chưa bật email) cũng phải hợp lệ nếu không rỗng.
        if (!string.IsNullOrEmpty(email) && !IsValidEmail(email))
            return UpdatePreferencesResult.InvalidEmail;

        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user == null)
            return UpdatePreferencesResult.NotFound;

        user.Email = string.IsNullOrWhiteSpace(email) ? null : email;
        user.NotifyInApp = input.NotifyInApp;
        user.NotifyByEmail = input.NotifyByEmail;
        user.NotifyOnGate = input.NotifyOnGate;
        user.NotifyOnCompleted = input.NotifyOnCompleted;
        user.NotifyOnFailed = input.NotifyOnFailed;

        await _db.SaveChangesAsync(cancellationToken);
        return UpdatePreferencesResult.Ok;
    }

    private static bool IsValidEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return MailAddress.TryCreate(value, out _);
    }
}

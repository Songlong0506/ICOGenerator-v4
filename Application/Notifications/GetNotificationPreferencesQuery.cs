using ICOGenerator.Data;
using ICOGenerator.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Notifications;

/// <summary>Đọc tùy chọn thông báo của một người dùng để hiển thị trang Preferences.</summary>
public class GetNotificationPreferencesQuery
{
    private readonly AppDbContext _db;
    private readonly NotificationOptions _channelOptions;

    public GetNotificationPreferencesQuery(AppDbContext db, NotificationOptions channelOptions)
    {
        _db = db;
        _channelOptions = channelOptions;
    }

    public async Task<NotificationPreferencesVm> ExecuteAsync(string username, CancellationToken cancellationToken = default)
    {
        var vm = await _db.AppUsers
            .Where(u => u.Username == username)
            .Select(u => new NotificationPreferencesVm
            {
                Email = u.Email,
                NotifyInApp = u.NotifyInApp,
                NotifyByEmail = u.NotifyByEmail,
                NotifyOnGate = u.NotifyOnGate,
                NotifyOnCompleted = u.NotifyOnCompleted,
                NotifyOnFailed = u.NotifyOnFailed
            })
            .FirstOrDefaultAsync(cancellationToken) ?? new NotificationPreferencesVm();

        // Chỉ để gợi ý trên UI: tùy chọn "email cá nhân" mới có tác dụng khi admin đã bật kênh email hệ thống.
        vm.EmailChannelConfigured = _channelOptions.Email.Enabled
            && !string.IsNullOrWhiteSpace(_channelOptions.Email.Host)
            && !string.IsNullOrWhiteSpace(_channelOptions.Email.From);

        return vm;
    }
}

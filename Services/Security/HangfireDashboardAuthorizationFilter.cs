using Hangfire.Dashboard;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Dashboard Hangfire (<c>/hangfire</c>) chỉ mở cho SuperAdmin: từ đó bấm được "Trigger now"/xoá job, tức là
/// gọi employee-api và ghi đè dữ liệu tổ chức theo ý muốn. Chưa đăng nhập thì bị đẩy về trang đăng nhập
/// như mọi trang khác; đã đăng nhập mà không phải SuperAdmin thì nhận 403.
/// </summary>
public sealed class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var user = context.GetHttpContext().User;
        return user.Identity?.IsAuthenticated == true && user.IsInRole(nameof(UserRole.SuperAdmin));
    }
}

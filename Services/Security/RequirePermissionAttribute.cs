using ICOGenerator.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Đặt trên controller (mức xem) hoặc action (mức thao tác) để yêu cầu một <see cref="AppPermission"/>.
/// Dùng TypeFilter để filter thật (<see cref="PermissionAuthorizationFilter"/>) được resolve qua DI,
/// nhờ đó lấy được IPermissionService scoped.
/// Truyền nhiều quyền = CẦN MỘT TRONG SỐ ĐÓ (OR), cho action dùng chung bởi nhiều thao tác (ví dụ thử kết
/// nối model, mở từ cả form Add lẫn Edit). Muốn buộc phải có ĐỦ nhiều quyền thì xếp nhiều attribute (AND).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute : TypeFilterAttribute
{
    public RequirePermissionAttribute(params AppPermission[] permissions) : base(typeof(PermissionAuthorizationFilter))
    {
        if (permissions.Length == 0)
            throw new ArgumentException("Cần ít nhất một AppPermission.", nameof(permissions));

        Arguments = new object[] { permissions };
    }
}

/// <summary>
/// Chưa đăng nhập => Challenge (cookie tự chuyển tới trang Login). Đã đăng nhập nhưng thiếu quyền
/// => Forbid (cookie chuyển tới AccessDeniedPath = /Account/AccessDenied).
/// </summary>
public sealed class PermissionAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly IPermissionService _permissions;
    private readonly AppPermission[] _required;

    public PermissionAuthorizationFilter(IPermissionService permissions, AppPermission[] required)
    {
        _permissions = permissions;
        _required = required;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            context.Result = new ChallengeResult();
            return;
        }

        foreach (var permission in _required)
        {
            if (await _permissions.HasPermissionAsync(user, permission, context.HttpContext.RequestAborted))
                return;
        }

        context.Result = new ForbidResult();
    }
}

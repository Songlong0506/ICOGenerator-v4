using ICOGenerator.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Đặt trên controller (mức xem) hoặc action (mức thao tác) để yêu cầu một <see cref="AppPermission"/>.
/// Dùng TypeFilter để filter thật (<see cref="PermissionAuthorizationFilter"/>) được resolve qua DI,
/// nhờ đó lấy được IPermissionService scoped.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute : TypeFilterAttribute
{
    public RequirePermissionAttribute(AppPermission permission) : base(typeof(PermissionAuthorizationFilter))
    {
        Arguments = new object[] { permission };
    }
}

/// <summary>
/// Chưa đăng nhập => Challenge (cookie tự chuyển tới trang Login). Đã đăng nhập nhưng thiếu quyền
/// => Forbid (cookie chuyển tới AccessDeniedPath = /Account/AccessDenied).
/// </summary>
public sealed class PermissionAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly IPermissionService _permissions;
    private readonly AppPermission _permission;

    public PermissionAuthorizationFilter(IPermissionService permissions, AppPermission permission)
    {
        _permissions = permissions;
        _permission = permission;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            context.Result = new ChallengeResult();
            return;
        }

        if (!await _permissions.HasPermissionAsync(user, _permission, context.HttpContext.RequestAborted))
            context.Result = new ForbidResult();
    }
}

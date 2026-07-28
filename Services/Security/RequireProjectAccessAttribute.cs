using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ICOGenerator.Services.Security;

/// <summary>Loại tài nguyên mà id trên action trỏ tới — quyết định overload nào của
/// <see cref="IProjectAccessGuard"/> được gọi.</summary>
public enum ProjectResource
{
    /// <summary>Id là <c>Project.Id</c>.</summary>
    Project,

    /// <summary>Id là <c>ProjectDocument.Id</c> (tài liệu sinh ra).</summary>
    Document,

    /// <summary>Id là <c>ProjectDocumentRevision.Id</c>.</summary>
    DocumentRevision,

    /// <summary>Id là <c>ProjectSourceFile.Id</c> (file user upload).</summary>
    SourceFile,

    /// <summary>Id là <c>AgentModelCallLog.Id</c> (popup AI Call Logs).</summary>
    CallLog,
}

/// <summary>Trả về gì khi user KHÔNG được phép — phải khớp với thứ client của action đang chờ.</summary>
public enum ProjectAccessDenial
{
    /// <summary>404 (kèm <see cref="RequireProjectAccessAttribute.Message"/> nếu có). Mặc định.</summary>
    NotFound,

    /// <summary>Redirect về danh sách dự án — cho các action trả về View.</summary>
    RedirectToProjects,

    /// <summary>200 kèm <c>{ ok = false, error }</c> — cho các endpoint fetch/JSON.</summary>
    JsonError,
}

/// <summary>
/// Chặn truy cập chéo cho action thao tác trong phạm vi MỘT project (horizontal authorization):
/// bổ sung cho <see cref="RequirePermissionAttribute"/> vốn chỉ trả lời "role này có quyền X không".
/// Luật thật nằm ở <see cref="IProjectAccessGuard"/>; attribute này chỉ là chỗ khai báo.
///
/// Trước đây mỗi action tự viết `if (!await CanAccessProjectAsync(projectId)) return ...` ở dòng đầu —
/// 51 chỗ lặp lại trên 3 controller. Vấn đề không phải là dài dòng mà là AN TOÀN: quên một chỗ thì
/// endpoint đó im lặng mở cho bất kỳ ai đoán được GUID, và không có cách nào nhìn ra thiếu sót ngoài
/// việc đọc từng action. Ở dạng attribute thì thiếu sót nhìn thấy được, và
/// <c>ProjectAccessCoverageTests</c> fail build nếu một action nhận projectId mà không khai báo.
///
/// Đặt tên tham số id qua <paramref name="idParameter"/>. Hỗ trợ đường dẫn có dấu chấm để lấy id nằm
/// trong view model đã bind, ví dụ <c>"vm.ProjectId"</c>.
/// </summary>
/// <example>
/// <code>
/// [RequireProjectAccess]                                             // đọc "projectId", trả 404
/// [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]     // endpoint fetch
/// [RequireProjectAccess("id", ProjectResource.Document)]             // id là document
/// [RequireProjectAccess("vm.ProjectId")]                             // id nằm trong view model
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireProjectAccessAttribute : Attribute, IFilterFactory
{
    public RequireProjectAccessAttribute(
        string idParameter = "projectId",
        ProjectResource resource = ProjectResource.Project)
    {
        IdParameter = idParameter;
        Resource = resource;
    }

    /// <summary>Tên tham số action chứa id, hoặc đường dẫn <c>thamSo.ThuocTinh</c>.</summary>
    public string IdParameter { get; }

    /// <summary>Id trỏ tới loại tài nguyên nào.</summary>
    public ProjectResource Resource { get; }

    /// <summary>Hình thức từ chối. Mặc định 404.</summary>
    public ProjectAccessDenial Denial { get; set; } = ProjectAccessDenial.NotFound;

    /// <summary>
    /// Nội dung báo lỗi. Với <see cref="ProjectAccessDenial.NotFound"/> đây là body của 404 (bỏ trống
    /// thì trả 404 rỗng); với <see cref="ProjectAccessDenial.JsonError"/> là trường <c>error</c>.
    /// Lưu ý giữ thông điệp trung tính kiểu "không tồn tại" để không xác nhận sự tồn tại của tài
    /// nguyên với người ngoài.
    /// </summary>
    public string? Message { get; set; }

    // false: filter giữ tham chiếu tới attribute nên không chia sẻ được giữa các request.
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new ProjectAccessFilter(serviceProvider.GetRequiredService<IProjectAccessGuard>(), this);
}

/// <summary>
/// Hiện thực của <see cref="RequireProjectAccessAttribute"/>. Là action filter (không phải
/// authorization filter) vì id cần lấy sau khi model binding xong — nó có thể nằm trong view model.
/// Chạy sau antiforgery/authorization và trước thân action, đúng vị trí mà lệnh kiểm tra viết tay
/// đứng trước đây.
/// </summary>
public sealed class ProjectAccessFilter : IAsyncActionFilter
{
    private readonly IProjectAccessGuard _guard;
    private readonly RequireProjectAccessAttribute _options;

    public ProjectAccessFilter(IProjectAccessGuard guard, RequireProjectAccessAttribute options)
    {
        _guard = guard;
        _options = options;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Id không đọc được = lỗi lập trình (sai tên tham số), KHÔNG phải "user không có quyền". Ném ra
        // để lộ ngay lúc dev/CI thay vì âm thầm cho qua — một filter bảo mật im lặng bỏ qua thì tệ hơn
        // là không có filter, vì nó tạo cảm giác an toàn giả.
        var resourceId = ResolveId(context);

        if (await IsAllowedAsync(context, resourceId))
        {
            await next();
            return;
        }

        context.Result = _options.Denial switch
        {
            ProjectAccessDenial.RedirectToProjects => new RedirectToActionResult("Index", "Projects", null),
            ProjectAccessDenial.JsonError => new JsonResult(new
            {
                ok = false,
                error = _options.Message ?? "Không có quyền truy cập dự án.",
            }),
            _ => _options.Message is null
                ? new NotFoundResult()
                : new NotFoundObjectResult(_options.Message),
        };
    }

    private Task<bool> IsAllowedAsync(ActionExecutingContext context, Guid resourceId)
    {
        var user = context.HttpContext.User;
        var cancellationToken = context.HttpContext.RequestAborted;

        return _options.Resource switch
        {
            ProjectResource.Document => _guard.CanAccessDocumentAsync(user, resourceId, cancellationToken),
            ProjectResource.DocumentRevision => _guard.CanAccessDocumentRevisionAsync(user, resourceId, cancellationToken),
            ProjectResource.SourceFile => _guard.CanAccessSourceFileAsync(user, resourceId, cancellationToken),
            ProjectResource.CallLog => _guard.CanAccessCallLogAsync(user, resourceId, cancellationToken),
            _ => _guard.CanAccessProjectAsync(user, resourceId, cancellationToken),
        };
    }

    // "projectId" -> tham số action; "vm.ProjectId" -> thuộc tính của tham số đã bind.
    private Guid ResolveId(ActionExecutingContext context)
    {
        var path = _options.IdParameter.Split('.');

        if (!context.ActionArguments.TryGetValue(path[0], out var current))
            throw new InvalidOperationException(
                $"[RequireProjectAccess] action '{context.ActionDescriptor.DisplayName}' không có tham số '{path[0]}'.");

        for (var i = 1; i < path.Length; i++)
        {
            var property = current?.GetType().GetProperty(path[i])
                ?? throw new InvalidOperationException(
                    $"[RequireProjectAccess] '{current?.GetType().Name}' không có thuộc tính '{path[i]}' " +
                    $"(action '{context.ActionDescriptor.DisplayName}').");
            current = property.GetValue(current);
        }

        return current switch
        {
            Guid id => id,
            // Tham số Guid không bind được (thiếu/sai định dạng) về null thay vì Guid.Empty. Coi như
            // không có quyền: Guid.Empty chắc chắn không khớp tài nguyên nào nên guard trả false.
            null => Guid.Empty,
            _ => throw new InvalidOperationException(
                $"[RequireProjectAccess] '{_options.IdParameter}' phải là Guid, đang là " +
                $"{current.GetType().Name} (action '{context.ActionDescriptor.DisplayName}')."),
        };
    }
}

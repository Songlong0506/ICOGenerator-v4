using System.Security.Claims;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Hành vi của ProjectAccessFilter — phần mà ProjectAccessCoverageTests KHÔNG kiểm được (nó chỉ soi
// attribute có mặt hay không). Ở đây guard bị thay bằng bản giả để ép cả hai nhánh:
//   - Cho qua  => thân action phải chạy.
//   - Từ chối  => thân action KHÔNG được chạy, và kết quả trả về phải đúng hình dạng client đang chờ
//                 (404 / redirect / JSON ok=false) — trả sai hình dạng thì phía JS im lặng hiểu nhầm.
public class ProjectAccessFilterTests
{
    private sealed class FakeGuard : IProjectAccessGuard
    {
        private readonly bool _allowed;

        public FakeGuard(bool allowed) => _allowed = allowed;

        public string? CalledMethod { get; private set; }
        public Guid CalledId { get; private set; }

        private Task<bool> Record(string method, Guid id)
        {
            CalledMethod = method;
            CalledId = id;
            return Task.FromResult(_allowed);
        }

        public Task<bool> CanAccessProjectAsync(ClaimsPrincipal u, Guid id, CancellationToken ct = default) =>
            Record(nameof(CanAccessProjectAsync), id);

        public Task<bool> CanAccessDocumentAsync(ClaimsPrincipal u, Guid id, CancellationToken ct = default) =>
            Record(nameof(CanAccessDocumentAsync), id);

        public Task<bool> CanAccessDocumentRevisionAsync(ClaimsPrincipal u, Guid id, CancellationToken ct = default) =>
            Record(nameof(CanAccessDocumentRevisionAsync), id);

        public Task<bool> CanAccessSourceFileAsync(ClaimsPrincipal u, Guid id, CancellationToken ct = default) =>
            Record(nameof(CanAccessSourceFileAsync), id);

        public Task<bool> CanAccessCallLogAsync(ClaimsPrincipal u, Guid id, CancellationToken ct = default) =>
            Record(nameof(CanAccessCallLogAsync), id);
    }

    public sealed class DeliveryVm
    {
        public Guid ProjectId { get; set; }
    }

    private static async Task<(ActionExecutingContext Context, bool ActionRan, FakeGuard Guard)> RunAsync(
        RequireProjectAccessAttribute attribute,
        bool allowed,
        Dictionary<string, object?> arguments)
    {
        var guard = new FakeGuard(allowed);
        var filter = new ProjectAccessFilter(guard, attribute);

        var context = new ActionExecutingContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ControllerActionDescriptor
            {
                DisplayName = "TestController.TestAction",
            }),
            new List<IFilterMetadata>(),
            arguments,
            controller: null!);

        var ran = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), null!));
        });

        return (context, ran, guard);
    }

    [Fact]
    public async Task Allowed_RunsTheAction_AndSetsNoResult()
    {
        var projectId = Guid.NewGuid();

        var (context, ran, guard) = await RunAsync(
            new RequireProjectAccessAttribute(),
            allowed: true,
            new Dictionary<string, object?> { ["projectId"] = projectId });

        Assert.True(ran);
        Assert.Null(context.Result);
        Assert.Equal(nameof(IProjectAccessGuard.CanAccessProjectAsync), guard.CalledMethod);
        Assert.Equal(projectId, guard.CalledId);
    }

    [Fact]
    public async Task Denied_ShortCircuitsBeforeTheActionBody()
    {
        var (_, ran, _) = await RunAsync(
            new RequireProjectAccessAttribute(),
            allowed: false,
            new Dictionary<string, object?> { ["projectId"] = Guid.NewGuid() });

        // Điểm cốt lõi: thân action KHÔNG được chạy — nếu chạy thì dữ liệu project người khác đã bị đụng.
        Assert.False(ran);
    }

    [Fact]
    public async Task Denied_DefaultsToBareNotFound()
    {
        var (context, _, _) = await RunAsync(
            new RequireProjectAccessAttribute(),
            allowed: false,
            new Dictionary<string, object?> { ["projectId"] = Guid.NewGuid() });

        Assert.IsType<NotFoundResult>(context.Result);
    }

    [Fact]
    public async Task Denied_WithMessage_ReturnsNotFoundCarryingThatMessage()
    {
        var (context, _, _) = await RunAsync(
            new RequireProjectAccessAttribute { Message = "Project không tồn tại." },
            allowed: false,
            new Dictionary<string, object?> { ["projectId"] = Guid.NewGuid() });

        var result = Assert.IsType<NotFoundObjectResult>(context.Result);
        Assert.Equal("Project không tồn tại.", result.Value);
    }

    [Fact]
    public async Task Denied_RedirectToProjects_GoesToTheProjectList()
    {
        var (context, _, _) = await RunAsync(
            new RequireProjectAccessAttribute { Denial = ProjectAccessDenial.RedirectToProjects },
            allowed: false,
            new Dictionary<string, object?> { ["projectId"] = Guid.NewGuid() });

        var result = Assert.IsType<RedirectToActionResult>(context.Result);
        Assert.Equal("Index", result.ActionName);
        Assert.Equal("Projects", result.ControllerName);
    }

    // Các endpoint fetch chờ đúng hình dạng { ok, error } — trả 404 vào đó thì JS rơi vào nhánh catch
    // chung và người dùng chỉ thấy lỗi mơ hồ.
    [Fact]
    public async Task Denied_JsonError_KeepsTheShapeTheClientExpects()
    {
        var (context, _, _) = await RunAsync(
            new RequireProjectAccessAttribute { Denial = ProjectAccessDenial.JsonError },
            allowed: false,
            new Dictionary<string, object?> { ["projectId"] = Guid.NewGuid() });

        var result = Assert.IsType<JsonResult>(context.Result);
        var value = result.Value!;
        Assert.False((bool)value.GetType().GetProperty("ok")!.GetValue(value)!);
        Assert.Equal("Không có quyền truy cập dự án.", value.GetType().GetProperty("error")!.GetValue(value));
    }

    [Theory]
    [InlineData(ProjectResource.Document, nameof(IProjectAccessGuard.CanAccessDocumentAsync))]
    [InlineData(ProjectResource.DocumentRevision, nameof(IProjectAccessGuard.CanAccessDocumentRevisionAsync))]
    [InlineData(ProjectResource.SourceFile, nameof(IProjectAccessGuard.CanAccessSourceFileAsync))]
    [InlineData(ProjectResource.CallLog, nameof(IProjectAccessGuard.CanAccessCallLogAsync))]
    public async Task ResourceKind_SelectsTheMatchingGuardOverload(ProjectResource resource, string expected)
    {
        var (_, _, guard) = await RunAsync(
            new RequireProjectAccessAttribute("id", resource),
            allowed: true,
            new Dictionary<string, object?> { ["id"] = Guid.NewGuid() });

        Assert.Equal(expected, guard.CalledMethod);
    }

    [Fact]
    public async Task DottedPath_ReadsTheIdOutOfABoundViewModel()
    {
        var projectId = Guid.NewGuid();

        var (_, ran, guard) = await RunAsync(
            new RequireProjectAccessAttribute("vm.ProjectId"),
            allowed: true,
            new Dictionary<string, object?> { ["vm"] = new DeliveryVm { ProjectId = projectId } });

        Assert.True(ran);
        Assert.Equal(projectId, guard.CalledId);
    }

    // Guid không bind được (thiếu tham số / sai định dạng) về null. Phải coi là TỪ CHỐI, không phải cho qua.
    [Fact]
    public async Task UnboundId_IsTreatedAsDenied()
    {
        var (context, ran, guard) = await RunAsync(
            new RequireProjectAccessAttribute(),
            allowed: false,
            new Dictionary<string, object?> { ["projectId"] = null });

        Assert.False(ran);
        Assert.Equal(Guid.Empty, guard.CalledId);
        Assert.IsType<NotFoundResult>(context.Result);
    }

    // Sai tên tham số là lỗi lập trình. Phải ném ra chứ KHÔNG được im lặng cho qua — một filter bảo mật
    // âm thầm bỏ qua còn tệ hơn là không có filter.
    [Fact]
    public async Task MisspelledParameterName_Throws()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            new RequireProjectAccessAttribute("projctId"),
            allowed: true,
            new Dictionary<string, object?> { ["projectId"] = Guid.NewGuid() }));

        Assert.Contains("projctId", exception.Message);
    }
}

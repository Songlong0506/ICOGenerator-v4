using System.Text.Json;
using System.Threading.Channels;
using ICOGenerator.Application.WebPilot;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

/// <summary>
/// Màn hình THỬ NGHIỆM WebPilot: giao một task tự do cho agent lái trình duyệt và xem nó chạy.
///
/// <para>
/// Dùng lại quyền của nhóm Agents (<see cref="AppPermission.AgentsView"/> để xem,
/// <see cref="AppPermission.AgentsManage"/> để chạy) thay vì khai quyền mới — quyền mới sẽ không tới
/// tay role nào trên DB đang chạy vì <c>SeedRolePermissionsAsync</c> chỉ seed khi bảng còn trống.
/// </para>
///
/// <para>
/// Không action nào ở đây nhận <c>projectId</c>: lượt chạy luôn gắn vào dự án hộp cát riêng
/// (<see cref="Services.WebPilot.WebPilotSandboxProvider"/>), nên không có gì để kiểm quyền theo dự án.
/// </para>
/// </summary>
[RequirePermission(AppPermission.AgentsView)]
public class WebPilotController : Controller
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GetWebPilotPageQuery _getWebPilotPageQuery;
    private readonly RunWebPilotTaskUseCase _runWebPilotTaskUseCase;
    private readonly ILogger<WebPilotController> _logger;

    public WebPilotController(
        GetWebPilotPageQuery getWebPilotPageQuery,
        RunWebPilotTaskUseCase runWebPilotTaskUseCase,
        ILogger<WebPilotController> logger)
    {
        _getWebPilotPageQuery = getWebPilotPageQuery;
        _runWebPilotTaskUseCase = runWebPilotTaskUseCase;
        _logger = logger;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
        => View(await _getWebPilotPageQuery.ExecuteAsync(cancellationToken));

    /// <summary>
    /// Chạy một lượt và stream tiến độ (SSE). Cùng khuôn với <c>RequirementsController.ChatStream</c>:
    /// callback tiến độ là đồng bộ nên không ghi thẳng response được, phải đẩy qua channel không giới
    /// hạn rồi vòng dưới đọc ra và ghi từng frame.
    ///
    /// <para>
    /// Khác ChatStream một điểm CỐ Ý: lượt chat BA chạy với <c>CancellationToken.None</c> để không để
    /// hội thoại cụt trong DB, còn ở đây lượt chạy dùng thẳng <c>RequestAborted</c> — nhờ vậy nút
    /// "Dừng" (client abort) và cả việc đóng tab đều thật sự dừng agent và đóng trình duyệt.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.AgentsManage)]
    public async Task Run(string task)
    {
        Response.StatusCode = 200;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var channel = Channel.CreateUnbounded<object>();
        using var heartbeatCts = new CancellationTokenSource();

        var aborted = HttpContext.RequestAborted;
        var clientGone = false;

        var runTask = RunAsync();
        var heartbeatTask = SendHeartbeatAsync(heartbeatCts.Token);

        await foreach (var ev in channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            if (clientGone)
                continue; // vẫn drain channel để lượt chạy kết thúc gọn, chỉ thôi ghi response

            try
            {
                await Response.WriteAsync($"data: {JsonSerializer.Serialize(ev, SseJsonOptions)}\n\n", aborted);
                await Response.Body.FlushAsync(aborted);
            }
            catch (OperationCanceledException)
            {
                clientGone = true;
            }
        }

        await runTask;
        await heartbeatTask;

        if (!clientGone)
        {
            try
            {
                await Response.WriteAsync("event: end\ndata: {}\n\n", aborted);
                await Response.Body.FlushAsync(aborted);
            }
            catch (OperationCanceledException) { }
        }

        async Task SendHeartbeatAsync(CancellationToken ct)
        {
            try
            {
                using var timer = new PeriodicTimer(HeartbeatInterval);
                while (await timer.WaitForNextTickAsync(ct))
                    channel.Writer.TryWrite(new { type = "ping" });
            }
            catch (OperationCanceledException)
            {
                // Lượt chạy đã xong — dừng nhịp tim là đúng.
            }
        }

        // Vỏ bọc bảo đảm hai việc LUÔN xảy ra dù lượt chạy vỡ kiểu gì: dừng nhịp tim và ĐÓNG channel.
        // Thiếu nó, một ngoại lệ lọt ra ngoài sẽ để vòng đọc channel ở trên treo vô hạn.
        async Task RunAsync()
        {
            try
            {
                var result = await _runWebPilotTaskUseCase.ExecuteAsync(
                    task,
                    (kind, message, detail) => channel.Writer.TryWrite(new { type = kind, message, detail }),
                    aborted);

                channel.Writer.TryWrite(result.Ok
                    ? new { type = "done", ok = true, output = result.Output, error = (string?)null }
                    : new { type = "done", ok = false, output = (string?)null, error = result.Error });
            }
            catch (OperationCanceledException)
            {
                channel.Writer.TryWrite(new { type = "done", ok = false, output = (string?)null, error = "Đã dừng theo yêu cầu." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lượt chạy WebPilot vỡ.");
                channel.Writer.TryWrite(new { type = "done", ok = false, output = (string?)null, error = ex.Message });
            }
            finally
            {
                heartbeatCts.Cancel();
                channel.Writer.TryComplete();
            }
        }
    }
}

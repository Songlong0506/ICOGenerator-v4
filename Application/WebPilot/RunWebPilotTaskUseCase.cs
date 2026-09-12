using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Browser;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.WebPilot;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.WebPilot;

/// <summary>Kết quả một lượt chạy thử WebPilot.</summary>
/// <param name="Ok">false ⇒ <paramref name="Error"/> nói vì sao không chạy được.</param>
public record WebPilotRunResult(bool Ok, string? Output, string? Error);

/// <summary>
/// Giao một task tự do cho agent WebPilot và chạy nó.
///
/// <para>
/// Lượt chạy dùng thẳng <see cref="AgentRunService"/> như mọi vai khác — điểm khác duy nhất là nó
/// không đi qua <c>AgentTaskWorker</c>/<c>DeliveryPipeline</c>: WebPilot không có bước nào trong
/// pipeline, nó là màn hình thử nghiệm.
/// </para>
///
/// <para>
/// Huỷ được: token truyền vào là <c>HttpContext.RequestAborted</c> của chính request SSE, nên nút
/// "Dừng" trên trang (client abort) huỷ luôn lượt chạy và đóng trình duyệt. Không cần sổ theo dõi
/// <c>CancellationTokenSource</c> phía server.
/// </para>
/// </summary>
public class RunWebPilotTaskUseCase
{
    private readonly AppDbContext _db;
    private readonly AgentRunService _agentRunService;
    private readonly WebPilotSandboxProvider _sandbox;
    private readonly WebTools _webTools;
    private readonly WebPilotSettings _settings;

    public RunWebPilotTaskUseCase(
        AppDbContext db,
        AgentRunService agentRunService,
        WebPilotSandboxProvider sandbox,
        WebTools webTools,
        WebPilotSettings settings)
    {
        _db = db;
        _agentRunService = agentRunService;
        _sandbox = sandbox;
        _webTools = webTools;
        _settings = settings;
    }

    /// <param name="task">Câu người dùng gõ ở ô task.</param>
    /// <param name="onProgress">Sink tiến độ (kind, message, detail) — đẩy thẳng ra SSE.</param>
    public async Task<WebPilotRunResult> ExecuteAsync(
        string task,
        Action<string, string, string?> onProgress,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
            return new WebPilotRunResult(false, null, "Tính năng WebPilot đang tắt (WebPilot:Enabled = false).");

        if (string.IsNullOrWhiteSpace(task))
            return new WebPilotRunResult(false, null, "Chưa nhập việc cần giao.");

        var agentId = await _db.Agents
            .Where(x => x.RoleKey == AgentRoleKey.WebPilot)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (agentId is null)
            return new WebPilotRunResult(false, null,
                "Chưa có agent vai WebPilot. Khởi động lại ứng dụng để nó được seed, rồi gán model ở màn hình Agents.");

        // Cùng cơ chế ambient như WorkspaceTools: WebTools là scoped nên đây đúng là instance mà
        // ToolRegistry sẽ resolve cho lượt chạy này. Nhờ vậy ảnh chụp màn hình đi thẳng ra SSE.
        _webTools.SetProgressSink(onProgress);

        var projectId = await _sandbox.GetOrCreateProjectIdAsync(cancellationToken);

        try
        {
            var output = await _agentRunService.RunAsync(
                projectId,
                agentId.Value,
                task,
                maxSteps: _settings.MaxSteps,
                onProgress: onProgress,
                onToken: token => onProgress("token", token, null),
                workflowRunId: null,
                cancellationToken: cancellationToken);

            return new WebPilotRunResult(true, output, null);
        }
        finally
        {
            // Đóng trình duyệt NGAY khi lượt chạy kết thúc, không đợi scope của request được dispose:
            // một lượt bỏ dở mà còn giữ trang là một tiến trình Chromium treo và một khoá chưa nhả.
            await _webTools.DisposeAsync();
        }
    }
}

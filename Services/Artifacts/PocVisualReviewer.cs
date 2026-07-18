using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Artifacts;

/// <summary>Kết quả một lần chấm hình ảnh POC. <see cref="Ran"/> = false ⇒ bị bỏ qua (không có agent UI/UX vision / tắt cấu hình / không có ảnh) — audit vẫn tiếp tục.</summary>
public sealed record PocVisualReport(bool Ran, string? SkipReason, IReadOnlyList<string> Issues, IReadOnlyList<string> Warnings)
{
    public static PocVisualReport Skipped(string reason) => new(false, reason, Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Tầng Visual QA của audit POC: đưa ảnh chụp từng màn hình (do <see cref="PlaywrightPocRuntimeChecker"/>
/// chụp) cho agent UI/UX (RoleKey = UiUx, model có vision) chấm bố cục/dữ liệu mẫu — lớp khiếm khuyết mà
/// cả scan chuỗi (<see cref="PocAudit"/>) lẫn self-test đều "mù": một màn hình trống trơn, layout vỡ,
/// bảng tràn, sai ngôn ngữ UI vẫn pass mọi kiểm tra hiện có. Kết quả nối vào báo cáo AuditPocContent như
/// ISSUES/WARNINGS bình thường để Developer tự sửa trong cùng vòng lặp.
/// <para>
/// FAIL-OPEN toàn phần: chưa cấu hình agent UI/UX, model không vision, tắt qua cấu hình, hoặc không có
/// ảnh nào ⇒ trả Skipped (audit chạy phần còn lại như trước). Đây là bước phụ trợ, không bao giờ chặn.
/// </para>
/// </summary>
public class PocVisualReviewer
{
    // Trần ảnh gửi cho model: đủ phủ các màn chính mà không đốt token vô kiểm soát.
    private const int MaxImages = 8;

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PocVisualReviewer> _logger;

    public PocVisualReviewer(
        AppDbContext db,
        ILlmClient llm,
        PromptTemplateService prompts,
        IConfiguration configuration,
        ILogger<PocVisualReviewer> logger)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// true khi tầng Visual QA có thể chạy: bật cấu hình + có agent UI/UX gắn model VISION. WorkspaceTools
    /// gọi trước để quyết định có yêu cầu runtime checker chụp ảnh hay không (không có agent thì khỏi chụp phí).
    /// </summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        if (!_configuration.GetValue("Poc:VisualCheck:Enabled", true))
            return false;
        return await ResolveVisionDesignerModelAsync(cancellationToken) != null;
    }

    /// <summary>
    /// Chấm bộ ảnh POC. Trả Skipped khi không đủ điều kiện (không agent/không vision/không ảnh) hoặc lời
    /// gọi lỗi — mọi lỗi đều nuốt + log, không bao giờ ném ra làm fail lượt audit.
    /// </summary>
    public async Task<PocVisualReport> ReviewAsync(
        Guid projectId,
        string? aiDesignSpec,
        IReadOnlyList<PocScreenshot> screenshots,
        Guid? workflowRunId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_configuration.GetValue("Poc:VisualCheck:Enabled", true))
                return PocVisualReport.Skipped("tắt qua cấu hình Poc:VisualCheck:Enabled.");
            if (screenshots.Count == 0)
                return PocVisualReport.Skipped("không có ảnh màn hình nào để chấm.");

            var designer = await ResolveVisionDesignerAsync(cancellationToken);
            if (designer == null)
                return PocVisualReport.Skipped("chưa cấu hình agent UI/UX (RoleKey = UiUx) gắn model hỗ trợ vision.");

            var shots = screenshots.Take(MaxImages).ToList();

            // Một message user gồm: text mở đầu + spec, rồi lần lượt "### Màn hình: X" + ảnh X.
            var contents = new List<AIContent>
            {
                new TextContent(
                    "Dưới đây là ảnh chụp từng màn hình của POC kèm AI Design Spec. Hãy chấm theo hướng dẫn.\n\n"
                    + "# AI Design Spec\n" + (aiDesignSpec ?? "(không có)"))
            };
            foreach (var shot in shots)
            {
                contents.Add(new TextContent($"\n### Màn hình: {shot.Screen}"));
                contents.Add(new DataContent(shot.Png, "image/png"));
            }

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _prompts.Get("UiUx/poc-visual-review.v1.md")),
                new(ChatRole.User, contents)
            };

            var (callResult, structured) = await _llm.ChatStructuredAsync<PocVisualReviewResult>(
                designer.AiModel!, messages, designer.Temperature,
                new ModelCallLogContext(projectId, designer, "UiUxPocVisualReview", workflowRunId),
                cancellationToken: cancellationToken);

            if (!callResult.IsSuccess || structured == null)
                return PocVisualReport.Skipped($"lời gọi model UI/UX thất bại ({callResult.ErrorMessage ?? callResult.Content}).");

            return new PocVisualReport(
                true, null,
                structured.Issues.Select(Format).Where(s => s.Length > 0).ToList(),
                structured.Warnings.Select(Format).Where(s => s.Length > 0).ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POC visual review failed for project {ProjectId}.", projectId);
            return PocVisualReport.Skipped($"lỗi khi chấm hình ({ex.Message}).");
        }
    }

    private static string Format(PocVisualFinding f)
    {
        var detail = (f.Detail ?? string.Empty).Trim();
        if (detail.Length == 0)
            return string.Empty;
        return string.IsNullOrWhiteSpace(f.Screen) ? detail : $"[{f.Screen.Trim()}] {detail}";
    }

    private async Task<Domain.Agent?> ResolveVisionDesignerAsync(CancellationToken cancellationToken)
    {
        var designer = await _db.Agents
            .Include(a => a.AiModel)
            .FirstOrDefaultAsync(a => a.RoleKey == AgentRoleKey.UiUx, cancellationToken);
        return designer?.AiModel is { SupportsVision: true } ? designer : null;
    }

    private async Task<Domain.AiModel?> ResolveVisionDesignerModelAsync(CancellationToken cancellationToken)
        => (await ResolveVisionDesignerAsync(cancellationToken))?.AiModel;
}

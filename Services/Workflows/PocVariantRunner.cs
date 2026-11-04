using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows;

/// <summary>
/// Chạy MỘT biến thể POC thử model (<see cref="PocVariant"/>): dựng lại bản demo từ đầu bằng model được
/// chọn, cùng đầu vào với bản demo chính, trong thư mục riêng — rồi chốt chi phí của lần chạy.
///
/// <para>
/// <b>Cùng đầu vào</b> là điều kiện để bảng so sánh có nghĩa: AI Design Spec lấy từ chính task PocPreview
/// đã dựng bản chính (<see cref="PocVariant.SourceTaskId"/>), cùng prompt template, cùng bộ kịch bản
/// nghiệm thu (UAT) và quy ước trình bày đang có của dự án, cùng dữ liệu mẫu thật, cùng đường dựng
/// (<see cref="PocOneShotBuilder"/>, rơi về agent nhiều bước với cùng ngân sách bước như bản chính), cùng
/// tầng tự kiểm (<c>AuditPocContent</c>). Thứ duy nhất khác là model.
/// </para>
///
/// <para>
/// <b>Không đụng gì của dự án</b>: agent chạy với gốc workspace là
/// <see cref="WorkspacePathResolver.GetPocVariantPath"/> nên poc-demo.html, kết quả tự kiểm và lịch sử
/// của bản chính giữ nguyên; không gọi <c>PocVerification.Reset</c>/<c>PocSnapshots.Reset</c>; không sinh
/// lại UAT (bộ đang có chỉ được ĐỌC); không mang ghi chú hoãn, không đổi trạng thái ghi chú POC hay run
/// nào; không ghi AgentConversations. Model thử chỉ áp cho lượt này (<see cref="AgentRunOverrides"/>).
/// </para>
/// </summary>
public class PocVariantRunner
{
    /// <summary>Nhãn Purpose của các lời gọi model trong một lượt thử — cũng là khoá để chốt chi phí.</summary>
    public const string CallPurpose = "PocVariant";

    private const int MaxOutputChars = 4000;

    private readonly AppDbContext _db;
    private readonly AgentRunService _agentRunService;
    private readonly WorkspaceTools _workspaceTools;
    private readonly WorkspacePathResolver _resolver;
    private readonly WorkflowTaskPromptBuilder _promptBuilder;
    private readonly UatScenarioService _uatScenarios;
    private readonly PocUiConventionService _uiConventions;
    private readonly PocVariantProgress _progress;
    private readonly IWebHostEnvironment _environment;
    private readonly PocOneShotBuilder _oneShotBuilder;

    public PocVariantRunner(AppDbContext db, AgentRunService agentRunService, WorkspaceTools workspaceTools,
        WorkspacePathResolver resolver, WorkflowTaskPromptBuilder promptBuilder, UatScenarioService uatScenarios,
        PocUiConventionService uiConventions, PocVariantProgress progress, IWebHostEnvironment environment,
        PocOneShotBuilder oneShotBuilder)
    {
        _oneShotBuilder = oneShotBuilder;
        _db = db;
        _agentRunService = agentRunService;
        _workspaceTools = workspaceTools;
        _resolver = resolver;
        _promptBuilder = promptBuilder;
        _uatScenarios = uatScenarios;
        _uiConventions = uiConventions;
        _progress = progress;
        _environment = environment;
    }

    /// <summary>
    /// Chạy biến thể <paramref name="variantId"/> nếu nó còn Queued. Lỗi của chính lượt chạy (model hỏng,
    /// hết ngân sách, thiếu đầu vào) được ghi vào dòng biến thể là Failed — không ném ra worker.
    /// </summary>
    public async Task RunAsync(Guid variantId, CancellationToken cancellationToken)
    {
        var variant = await _db.PocVariants
            .FirstOrDefaultAsync(x => x.Id == variantId && x.Status == PocVariantStatus.Queued, cancellationToken);
        if (variant == null)
            return;

        variant.Status = PocVariantStatus.Running;
        variant.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        _progress.Report(variant.Id, "Chuẩn bị thư mục thử và template POC…");

        try
        {
            var output = await BuildAsync(variant, cancellationToken);
            variant.Status = PocVariantStatus.Completed;
            variant.Output = output.Length > MaxOutputChars ? output[..MaxOutputChars] + "…" : output;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Tắt app giữa chừng: worker đánh Failed lúc khởi động lại (RecoverOrphaned). Không ghi gì ở
            // đây — token đã huỷ thì SaveChanges cũng không chạy được.
            throw;
        }
        catch (Exception ex)
        {
            variant.Status = PocVariantStatus.Failed;
            variant.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
        }

        variant.FinishedAt = DateTime.UtcNow;
        await CaptureCostAsync(variant, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        _progress.Clear(variant.Id);
    }

    private async Task<string> BuildAsync(PocVariant variant, CancellationToken cancellationToken)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == variant.ProjectId, cancellationToken)
            ?? throw new InvalidOperationException("Project không còn tồn tại.");

        var model = variant.AiModelId is { } modelId
            ? await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId, cancellationToken)
            : null;
        if (model == null)
            throw new InvalidOperationException($"Model '{variant.ModelLabel}' đã bị xoá khỏi màn AI Models.");

        var developer = await _db.Agents.AsNoTracking()
            .FirstOrDefaultAsync(a => a.RoleKey == AgentRoleKey.Developer, cancellationToken)
            ?? throw new InvalidOperationException("Chưa có agent Developer.");

        var spec = await _db.AgentTasks.AsNoTracking()
            .Where(t => t.Id == variant.SourceTaskId)
            .Select(t => t.Input)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(spec))
            throw new InvalidOperationException("Không còn AI Design Spec của bản demo chính để dựng lại.");

        // Thư mục riêng của lượt thử, luôn bắt đầu sạch.
        var sandbox = _resolver.GetPocVariantPath(project.Id, variant.Id);
        if (Directory.Exists(sandbox))
            Directory.Delete(sandbox, recursive: true);
        var implementationDir = Path.GetDirectoryName(Path.Combine(sandbox, PocTemplate.MockupRelativePath))!;
        if (!await PocDemoSeeder.SeedAsync(PocDemoSeeder.TemplatePath(_environment.ContentRootPath), implementationDir, cancellationToken))
            throw new InvalidOperationException("Không tìm thấy Prompts/Design/poc-template.html.");

        // Bối cảnh của tầng tự kiểm — y như bước PocPreview của pipeline (xem AgentTaskWorker). UAT và quy
        // ước trình bày chỉ ĐỌC từ workspace dự án; không sinh lại, không ghi.
        var uat = await _uatScenarios.LoadAsync(project.Id, project.Name, cancellationToken);
        var conventions = await _uiConventions.LoadAsync(project.Id, project.Name, cancellationToken);
        _workspaceTools.SetPocSpec(spec);
        _workspaceTools.SetPocReviewContext(spec, project.Id, null);
        _workspaceTools.SetPocRealSampleData(await RealSampleDataReader.ReadAsync(_db, project.Id, cancellationToken));
        _workspaceTools.SetPocUatScenarios(uat);

        var prompt = _promptBuilder.Build(AgentTaskType.PocPreview, spec, project.IsUseBoschTemplate == true,
            acceptanceBlock: UatScenarioService.BuildPromptBlock(uat),
            conventionsBlock: PocUiConventionService.BuildPromptBlock(conventions));
        var maxSteps = DeliveryPipeline.PocStepBudget(PocSpec.Parse(spec).Screens.Count);

        // Cùng đường dựng với bước POC của pipeline (xem AgentTaskWorker): một lượt sinh + vài lượt sửa trước,
        // agent nhiều bước khi lượt sinh không dùng được — để bảng so sánh đo đúng cách bản chính được dựng.
        _workspaceTools.SetWorkspacePath(sandbox);
        _progress.Report(variant.Id, $"Developer đang sinh POC bằng model {model.ModelId}…");
        var outcome = await _oneShotBuilder.TryBuildAsync(new PocOneShotRequest(
            project.Id, developer, model, prompt, null, CallPurpose, CallPurpose,
            (_, message, _) => _progress.Report(variant.Id, message)), cancellationToken);
        if (outcome.Built)
            return outcome.Output; // builder đã tự kiểm sau lượt cuối — poc-verification.json khớp bản demo.

        _progress.Report(variant.Id, $"Không dựng được trong một lượt ({outcome.FallbackReason}) — chuyển sang agent nhiều bước…");

        _progress.Report(variant.Id, $"Developer đang dựng POC bằng model {model.ModelId}…");
        var output = await _agentRunService.RunAsync(
            project.Id,
            developer.Id,
            prompt,
            maxSteps,
            onProgress: (_, message, _) => _progress.Report(variant.Id, message),
            cancellationToken: cancellationToken,
            overrides: new AgentRunOverrides(
                Model: model,
                WorkspacePath: sandbox,
                Purpose: CallPurpose,
                SaveConversation: false));

        if (string.Equals(output, AgentRunService.MaxStepsReachedResult, StringComparison.Ordinal))
            throw new InvalidOperationException("Agent đạt giới hạn số bước mà chưa tạo được POC.");

        // Vòng tự kiểm CHỐT do runner chạy, không trông vào agent: bảng so sánh đọc poc-verification.json,
        // mà model nào bỏ qua AuditPocContent (hoặc sửa tiếp sau lần audit cuối) sẽ ra cột trống/lệch với
        // bản demo thật — đúng loại model cần bị lộ ra chứ không được "trốn" khỏi bảng. Fail-open: audit
        // lỗi (không có Chromium…) không làm hỏng một lượt đã dựng xong.
        _progress.Report(variant.Id, "Chấm bản demo bằng tầng tự kiểm…");
        try
        {
            await _workspaceTools.AuditPocContent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        return output;
    }

    // Chi phí của lượt thử = các lời gọi Purpose = PocVariant của dự án trong khung giờ chạy. Đủ phân biệt vì
    // worker chạy TUẦN TỰ từng biến thể một trên toàn hệ thống (PocVariantWorker) — không có hai lượt thử
    // nào chồng khung giờ. Tính cả khi Failed: model hỏng giữa chừng vẫn đã tiêu tiền, và đó cũng là dữ kiện
    // để chọn model. Không có đơn giá ⇒ 0 (cùng quy ước LlmPrice của trang Usage).
    private async Task CaptureCostAsync(PocVariant variant, CancellationToken cancellationToken)
    {
        var from = variant.StartedAt ?? variant.CreatedAt;
        var to = variant.FinishedAt ?? DateTime.UtcNow;
        var calls = await _db.AgentModelCallLogs.AsNoTracking()
            .Where(l => l.ProjectId == variant.ProjectId && l.Purpose == CallPurpose && l.CreatedAt >= from && l.CreatedAt <= to)
            .Select(l => new { l.ModelId, l.PromptTokens, l.CachedPromptTokens, l.CompletionTokens })
            .ToListAsync(cancellationToken);

        var prices = await ModelPriceBook.LoadAsync(_db, cancellationToken);
        variant.ModelCalls = calls.Count;
        variant.PromptTokens = calls.Sum(c => (long)c.PromptTokens);
        variant.CompletionTokens = calls.Sum(c => (long)c.CompletionTokens);
        variant.CostUsd = calls.Sum(c => prices.CostFor(c.ModelId, c.PromptTokens, c.CachedPromptTokens, c.CompletionTokens));
    }
}

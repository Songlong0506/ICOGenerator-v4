using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>
/// Các con số máy đo được trên một bản demo — cùng nguồn <c>poc-verification.json</c> mà tầng tự kiểm
/// (<c>AuditPocContent</c>) ghi cho bản chính, nên bản chính và các biến thể được chấm bằng đúng một thước.
/// </summary>
public record PocVariantMetrics(
    int SpecScreens,
    int CoveredScreens,
    int OpenIssues,
    int UatTotal,
    int UatCovered,
    int UatDriveTotal,
    int UatDrivePass,
    int SelfTestTotal,
    int SelfTestPass,
    int ScenarioTotal,
    int ScenarioPass,
    bool RuntimeRan)
{
    public static PocVariantMetrics? From(PocVerificationSummary? summary) => summary == null
        ? null
        : new PocVariantMetrics(
            summary.SpecScreens,
            summary.CoveredScreens,
            summary.OpenIssues.Count,
            summary.UatTotal,
            summary.UatCovered,
            summary.UatDriveResults.Count,
            summary.UatDriveResults.Count(IsPass),
            summary.SelfTestResults.Count,
            summary.SelfTestResults.Count(IsPass),
            summary.ScenarioResults.Count,
            summary.ScenarioResults.Count(IsPass),
            summary.RuntimeRan);

    // "PASS BR-3 — …" / "FAIL …" — định dạng do PocRuntimeChecker dựng (xem PocVerification).
    private static bool IsPass(string line) => (line ?? string.Empty).TrimStart().StartsWith("PASS", StringComparison.Ordinal);
}

/// <summary>Một dòng của bảng so sánh: bản chính (<see cref="IsBaseline"/>) hoặc một biến thể.</summary>
public record PocVariantRow(
    Guid? Id,
    bool IsBaseline,
    string ModelLabel,
    string Status,
    string? Error,
    string? Progress,
    string? RequestedBy,
    DateTime CreatedAt,
    double? DurationSeconds,
    int ModelCalls,
    long PromptTokens,
    long CompletionTokens,
    decimal CostUsd,
    bool HasDemo,
    PocVariantMetrics? Metrics);

public record PocVariantModelOption(Guid Id, string Label, bool IsCurrent);

public record PocVariantPage(
    IReadOnlyList<PocVariantRow> Rows,
    IReadOnlyList<PocVariantModelOption> Models,
    // Số vòng "Yêu cầu chỉnh sửa" đã vá lên bản chính sau lượt dựng mới — bản chính có thể đã được người
    // review chỉnh tay qua vài vòng, còn biến thể là MỘT lượt dựng; bảng phải nói rõ để không so khập khiễng.
    int BaselineRevisionRounds,
    bool HasSource,
    bool HasActive,
    int Limit);

/// <summary>
/// Bảng so sánh "Thử model khác" trên trang POC Review: bản chính + mọi biến thể thử model của dự án,
/// mỗi dòng có độ phủ màn hình, kịch bản nghiệm thu, self-test, điểm chưa đạt, thời gian, token và chi phí.
///
/// <para>
/// Chi phí của bản chính lấy từ AgentModelCallLogs của chính task dựng nó (cùng run, cùng agent, trong khung
/// giờ của task) — nên cột chi phí của bản chính và biến thể đo bằng cùng một cách. Model của bản chính là
/// model THẬT đã trả lời các lời gọi đó, không phải model đang gán cho Developer (có thể đã đổi sau đó).
/// </para>
/// </summary>
public class ListPocVariantsQuery
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _resolver;
    private readonly PocVariantProgress _progress;

    public ListPocVariantsQuery(AppDbContext db, WorkspacePathResolver resolver, PocVariantProgress progress)
    {
        _db = db;
        _resolver = resolver;
        _progress = progress;
    }

    public async Task<PocVariantPage?> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return null;

        var prices = await ModelPriceBook.LoadAsync(_db, cancellationToken);
        var rows = new List<PocVariantRow>();
        var source = await PocVariantSource.FindAsync(_db, projectId, cancellationToken);
        var revisionRounds = 0;

        if (source != null)
        {
            var workspacePath = _resolver.GetProjectWorkspacePath(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));
            var from = source.StartedAt ?? source.CreatedAt;
            var to = source.FinishedAt ?? DateTime.UtcNow;
            var calls = await _db.AgentModelCallLogs.AsNoTracking()
                .Where(l => l.ProjectId == projectId
                            && l.WorkflowRunId == source.WorkflowRunId
                            && l.AgentId == source.AgentId
                            && (l.Purpose == "AgentRun"
                                || l.Purpose == PocOneShotBuilder.BuildPurpose
                                || l.Purpose == PocOneShotBuilder.RepairPurpose)
                            && l.CreatedAt >= from && l.CreatedAt <= to)
                .Select(l => new { l.ModelId, l.PromptTokens, l.CachedPromptTokens, l.CompletionTokens })
                .ToListAsync(cancellationToken);

            revisionRounds = await _db.AgentTasks.AsNoTracking()
                .CountAsync(t => t.ProjectId == projectId
                                 && t.Type == AgentTaskType.PocPreview
                                 && t.RevisionFeedback != null
                                 && t.CreatedAt > source.CreatedAt, cancellationToken);

            var models = calls.Select(c => c.ModelId).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();
            rows.Add(new PocVariantRow(
                Id: null,
                IsBaseline: true,
                ModelLabel: models.Count > 0 ? string.Join(", ", models) : "(không rõ — chưa có log)",
                Status: PocVariantStatus.Completed.ToString(),
                Error: null,
                Progress: null,
                RequestedBy: null,
                CreatedAt: source.CreatedAt,
                DurationSeconds: source.StartedAt is { } s && source.FinishedAt is { } f ? (f - s).TotalSeconds : null,
                ModelCalls: calls.Count,
                PromptTokens: calls.Sum(c => (long)c.PromptTokens),
                CompletionTokens: calls.Sum(c => (long)c.CompletionTokens),
                CostUsd: calls.Sum(c => prices.CostFor(c.ModelId, c.PromptTokens, c.CachedPromptTokens, c.CompletionTokens)),
                HasDemo: File.Exists(_resolver.GetMockupPath(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name))),
                Metrics: PocVariantMetrics.From(PocVerification.TryLoad(workspacePath))));
        }

        var variants = await _db.PocVariants.AsNoTracking()
            .Where(v => v.ProjectId == projectId)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var v in variants)
        {
            var sandbox = _resolver.GetPocVariantPath(projectId, v.Id);
            var running = v.Status == PocVariantStatus.Running;
            rows.Add(new PocVariantRow(
                Id: v.Id,
                IsBaseline: false,
                ModelLabel: v.ModelLabel,
                Status: v.Status.ToString(),
                Error: v.Error,
                Progress: running ? _progress.Get(v.Id) : null,
                RequestedBy: v.RequestedBy,
                CreatedAt: v.CreatedAt,
                DurationSeconds: v.StartedAt is { } s
                    ? ((v.FinishedAt ?? DateTime.UtcNow) - s).TotalSeconds
                    : null,
                ModelCalls: v.ModelCalls,
                PromptTokens: v.PromptTokens,
                CompletionTokens: v.CompletionTokens,
                CostUsd: v.CostUsd,
                HasDemo: v.Status != PocVariantStatus.Queued && File.Exists(Path.Combine(sandbox, PocTemplate.MockupRelativePath)),
                // Đang chạy vẫn đọc: tầng tự kiểm ghi file sau MỖI vòng audit, nên bảng thấy được model này
                // đang khá lên hay giậm chân qua từng vòng mà không phải đợi tới cuối.
                Metrics: v.Status == PocVariantStatus.Queued ? null : PocVariantMetrics.From(PocVerification.TryLoad(sandbox))));
        }

        var developerModelId = await _db.Agents.AsNoTracking()
            .Where(a => a.RoleKey == AgentRoleKey.Developer)
            .Select(a => (Guid?)a.AiModelId)
            .FirstOrDefaultAsync(cancellationToken);
        var options = await _db.AiModels.AsNoTracking()
            .Where(m => m.IsActive)
            .OrderBy(m => m.ModelId)
            .Select(m => new PocVariantModelOption(m.Id, m.ModelId, m.Id == developerModelId))
            .ToListAsync(cancellationToken);

        return new PocVariantPage(
            rows,
            options,
            revisionRounds,
            HasSource: source != null && !string.IsNullOrWhiteSpace(source.Input),
            HasActive: variants.Any(v => v.Status is PocVariantStatus.Queued or PocVariantStatus.Running),
            Limit: StartPocVariantUseCase.MaxVariantsPerProject);
    }
}

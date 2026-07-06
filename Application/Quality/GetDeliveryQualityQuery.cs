using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Quality;

// Một dòng "run thất bại theo giai đoạn": Failed run dừng ở stage nào — cho biết pipeline hay gãy ở đâu.
public record StageBreakdownItem(WorkflowStageKey Stage, string StageLabel, int Count);

// Chất lượng giao hàng theo project: bao nhiêu run, tỷ lệ xong, và mức RƯỚC VIỆC (revision + bugfix) —
// project phải sửa nhiều vòng là tín hiệu requirement/spec chưa đủ rõ.
public record ProjectQualityItem(
    Guid ProjectId,
    string ProjectName,
    int Runs,
    int Completed,
    int Failed,
    int RevisionRequests,
    int BugFixRounds,
    double? AvgDurationMinutes,
    decimal Cost);

// Độ tin cậy của model (khác trang Usage vốn chỉ đo chi phí): tỷ lệ call thành công + độ trễ trung bình.
public record ModelReliabilityItem(
    string ModelId,
    string ModelName,
    int Calls,
    int SuccessCount,
    double SuccessRate,
    double AvgLatencyMs,
    long TotalTokens,
    decimal Cost,
    bool HasPrice);

public record DeliveryQualityVm(
    int SelectedYear,
    IReadOnlyList<int> AvailableYears,
    // ----- Kết quả run -----
    int TotalRuns,
    int CompletedRuns,
    int FailedRuns,
    int CanceledRuns,
    int InProgressRuns,
    double CompletionRate,   // completed / (số run đã kết thúc: completed + failed + canceled)
    double FailureRate,
    double? AvgDurationMinutes,
    decimal TotalCost,
    decimal AvgCostPerRun,
    bool HasAnyPricing,
    // ----- Rước việc (rework) -----
    int TotalRevisionRequests,
    int TotalBugFixRounds,
    int RunsNeedingRevision,
    int RunsNeedingBugFix,
    double ReworkRate,       // (số run cần revision HOẶC bugfix) / tổng run
    // ----- Phân rã -----
    IReadOnlyList<StageBreakdownItem> FailedByStage,
    IReadOnlyList<ProjectQualityItem> Projects,
    IReadOnlyList<ModelReliabilityItem> Models);

/// <summary>
/// Tổng hợp "sức khỏe" quy trình giao hàng từ dữ liệu đã có (WorkflowRun + AgentTask + AgentModelCallLog):
/// tỷ lệ hoàn tất/thất bại, thời gian giao hàng, chi phí/run, và mức rước việc (revision + bugfix). Bổ trợ
/// cho trang Usage (vốn chỉ đo chi phí token) bằng góc nhìn CHẤT LƯỢNG/THÔNG LƯỢNG. Lọc theo năm như Usage.
/// </summary>
public class GetDeliveryQualityQuery
{
    private readonly AppDbContext _db;
    public GetDeliveryQualityQuery(AppDbContext db) => _db = db;

    public async Task<DeliveryQualityVm> ExecuteAsync(int? year = null, CancellationToken cancellationToken = default)
    {
        // Bảng giá theo ModelId (giống GetUsageOverviewQuery) — quy token ra USD bằng cùng công thức LlmCost.
        var priceByModelId = (await _db.AiModels
                .AsNoTracking()
                .Select(m => new { m.ModelId, m.InputPricePerMillionTokens, m.OutputPricePerMillionTokens })
                .ToListAsync(cancellationToken))
            .GroupBy(m => m.ModelId)
            .ToDictionary(
                g => g.Key ?? string.Empty,
                g => (Input: g.First().InputPricePerMillionTokens, Output: g.First().OutputPricePerMillionTokens),
                StringComparer.OrdinalIgnoreCase);

        decimal CostFor(string? modelId, long prompt, long completion)
            => modelId != null && priceByModelId.TryGetValue(modelId, out var p)
                ? LlmCost.Usd(prompt, completion, p.Input, p.Output)
                : 0m;

        bool HasPrice(string? modelId)
            => modelId != null && priceByModelId.TryGetValue(modelId, out var p) && (p.Input > 0 || p.Output > 0);

        var now = DateTime.UtcNow;

        var availableYears = (await _db.WorkflowRuns.AsNoTracking()
                .Select(r => r.CreatedAt.Year)
                .Distinct()
                .ToListAsync(cancellationToken))
            .Append(now.Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToList();

        var selectedYear = year is int y && availableYears.Contains(y) ? y : now.Year;

        // ----- Runs của năm đang chọn (một dòng / run) -----
        var runs = await _db.WorkflowRuns.AsNoTracking()
            .Where(r => r.CreatedAt.Year == selectedYear)
            .Select(r => new RunRow(
                r.Id,
                r.ProjectId,
                r.Project.Name,
                r.Status,
                r.CurrentStage,
                r.StartedAt,
                r.FinishedAt))
            .ToListAsync(cancellationToken);

        var runIds = runs.Select(r => r.Id).ToList();

        // ----- Rước việc: gộp AgentTask theo run (revision = task có RevisionFeedback; bugfix = task BugFix) -----
        var reworkByRun = (await _db.AgentTasks.AsNoTracking()
                .Where(t => t.WorkflowRun.CreatedAt.Year == selectedYear)
                .GroupBy(t => t.WorkflowRunId)
                .Select(g => new
                {
                    RunId = g.Key,
                    Revisions = g.Sum(x => x.RevisionFeedback != null ? 1 : 0),
                    BugFixes = g.Sum(x => x.Type == AgentTaskType.BugFix ? 1 : 0)
                })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.RunId, x => (x.Revisions, x.BugFixes));

        // ----- Chi phí theo run: gộp call-log (thuộc run) theo (run, model) rồi quy USD trong RAM -----
        var costByRun = new Dictionary<Guid, decimal>();
        if (runIds.Count > 0)
        {
            var callRows = await _db.AgentModelCallLogs.AsNoTracking()
                .Where(x => x.WorkflowRunId != null && runIds.Contains(x.WorkflowRunId.Value))
                .GroupBy(x => new { x.WorkflowRunId, x.ModelId })
                .Select(g => new
                {
                    g.Key.WorkflowRunId,
                    g.Key.ModelId,
                    Prompt = g.Sum(x => (long)x.PromptTokens),
                    Completion = g.Sum(x => (long)x.CompletionTokens)
                })
                .ToListAsync(cancellationToken);

            foreach (var row in callRows)
            {
                if (row.WorkflowRunId is not Guid rid) continue;
                var cost = CostFor(row.ModelId, row.Prompt, row.Completion);
                costByRun[rid] = costByRun.TryGetValue(rid, out var c) ? c + cost : cost;
            }
        }

        // ----- Độ tin cậy model: mọi call trong năm (kể cả chat BA) — đo endpoint/model, không chỉ delivery.
        // Gộp cả prompt/completion để tính chi phí ngay trong một truy vấn (cùng công thức LlmCost với Usage). -----
        var modelAgg = await _db.AgentModelCallLogs.AsNoTracking()
            .Where(x => x.CreatedAt.Year == selectedYear)
            .GroupBy(x => new { x.ModelId, x.ModelName })
            .Select(g => new
            {
                g.Key.ModelId,
                g.Key.ModelName,
                Calls = g.Count(),
                SuccessCount = g.Sum(x => x.IsSuccess ? 1 : 0),
                DurationSum = g.Sum(x => x.DurationMs),
                Prompt = g.Sum(x => (long)x.PromptTokens),
                Completion = g.Sum(x => (long)x.CompletionTokens),
                TotalTokens = g.Sum(x => (long)x.TotalTokens)
            })
            .ToListAsync(cancellationToken);

        var models = modelAgg
            .Select(m => new ModelReliabilityItem(
                m.ModelId ?? string.Empty,
                string.IsNullOrWhiteSpace(m.ModelName) ? (m.ModelId ?? "(unknown)") : m.ModelName,
                m.Calls,
                m.SuccessCount,
                m.Calls == 0 ? 0 : Math.Round(m.SuccessCount * 100d / m.Calls, 1),
                m.Calls == 0 ? 0 : Math.Round((double)m.DurationSum / m.Calls, 0),
                m.TotalTokens,
                CostFor(m.ModelId, m.Prompt, m.Completion),
                HasPrice(m.ModelId)))
            .OrderByDescending(m => m.Calls)
            .ToList();

        // ----- Kết quả run -----
        var completed = runs.Count(r => r.Status == WorkflowRunStatus.Completed);
        var failed = runs.Count(r => r.Status == WorkflowRunStatus.Failed);
        var canceled = runs.Count(r => r.Status == WorkflowRunStatus.Canceled);
        var inProgress = runs.Count(r => r.Status is WorkflowRunStatus.Queued or WorkflowRunStatus.Running or WorkflowRunStatus.WaitingForHuman);
        var finishedTerminal = completed + failed + canceled;

        var completedDurations = runs
            .Where(r => r.Status == WorkflowRunStatus.Completed && r.StartedAt.HasValue && r.FinishedAt.HasValue)
            .Select(r => (r.FinishedAt!.Value - r.StartedAt!.Value).TotalMinutes)
            .Where(m => m >= 0)
            .ToList();

        var totalCost = costByRun.Values.Sum();

        // ----- Rework tổng -----
        var totalRevisions = reworkByRun.Values.Sum(v => v.Revisions);
        var totalBugFixes = reworkByRun.Values.Sum(v => v.BugFixes);
        var runsNeedingRevision = reworkByRun.Values.Count(v => v.Revisions > 0);
        var runsNeedingBugFix = reworkByRun.Values.Count(v => v.BugFixes > 0);
        var runsNeedingRework = reworkByRun.Values.Count(v => v.Revisions > 0 || v.BugFixes > 0);

        // ----- Failed theo stage -----
        var failedByStage = runs
            .Where(r => r.Status == WorkflowRunStatus.Failed)
            .GroupBy(r => r.CurrentStage)
            .Select(g => new StageBreakdownItem(g.Key, g.Key.GetTitle(), g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        // ----- Theo project -----
        var projects = runs
            .GroupBy(r => new { r.ProjectId, r.ProjectName })
            .Select(g =>
            {
                var ids = g.Select(r => r.Id).ToList();
                var durations = g
                    .Where(r => r.Status == WorkflowRunStatus.Completed && r.StartedAt.HasValue && r.FinishedAt.HasValue)
                    .Select(r => (r.FinishedAt!.Value - r.StartedAt!.Value).TotalMinutes)
                    .Where(m => m >= 0)
                    .ToList();
                return new ProjectQualityItem(
                    g.Key.ProjectId,
                    g.Key.ProjectName,
                    g.Count(),
                    g.Count(r => r.Status == WorkflowRunStatus.Completed),
                    g.Count(r => r.Status == WorkflowRunStatus.Failed),
                    ids.Sum(id => reworkByRun.TryGetValue(id, out var v) ? v.Revisions : 0),
                    ids.Sum(id => reworkByRun.TryGetValue(id, out var v) ? v.BugFixes : 0),
                    durations.Count == 0 ? null : Math.Round(durations.Average(), 1),
                    ids.Sum(id => costByRun.TryGetValue(id, out var c) ? c : 0m));
            })
            .OrderByDescending(p => p.Runs)
            .ThenByDescending(p => p.Cost)
            .ToList();

        return new DeliveryQualityVm(
            selectedYear,
            availableYears,
            runs.Count,
            completed,
            failed,
            canceled,
            inProgress,
            finishedTerminal == 0 ? 0 : Math.Round(completed * 100d / finishedTerminal, 1),
            finishedTerminal == 0 ? 0 : Math.Round(failed * 100d / finishedTerminal, 1),
            completedDurations.Count == 0 ? null : Math.Round(completedDurations.Average(), 1),
            totalCost,
            runs.Count == 0 ? 0m : Math.Round(totalCost / runs.Count, 4),
            priceByModelId.Values.Any(p => p.Input > 0 || p.Output > 0),
            totalRevisions,
            totalBugFixes,
            runsNeedingRevision,
            runsNeedingBugFix,
            runs.Count == 0 ? 0 : Math.Round(runsNeedingRework * 100d / runs.Count, 1),
            failedByStage,
            projects,
            models);
    }

    // Dòng run rút gọn cho tổng hợp trong RAM.
    private sealed record RunRow(
        Guid Id,
        Guid ProjectId,
        string ProjectName,
        WorkflowRunStatus Status,
        WorkflowStageKey CurrentStage,
        DateTime? StartedAt,
        DateTime? FinishedAt);
}

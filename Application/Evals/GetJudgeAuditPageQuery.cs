using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Evals;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

public record JudgeAuditRunItemVm(
    Guid Id,
    string Purpose,
    string? SourceModelId,
    DateTime? From,
    DateTime? To,
    int SampleSize,
    string? CandidateModelName,
    string ArbiterModelName,
    bool ArbitrateAll,
    string? Note,
    EvalRunStatus Status,
    int CaseCount,
    int CompletedCount,
    int DisagreementCount,
    decimal TotalCost,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? CreatedByUsername)
{
    public bool IsReplay => CandidateModelName != null;

    public static JudgeAuditRunItemVm Of(JudgeAuditRun r) => new(
        r.Id, r.Purpose, r.SourceModelId, r.From, r.To, r.SampleSize, r.CandidateModelName, r.ArbiterModelName,
        r.ArbitrateAll, r.Note, r.Status, r.CaseCount, r.CompletedCount, r.DisagreementCount, r.TotalCost, r.Error,
        r.CreatedAt, r.StartedAt, r.FinishedAt, r.CreatedByUsername);
}

/// <summary>Có bao nhiêu lượt trọng tài đã log, theo mục đích × model — để biết lấy mẫu từ đâu.</summary>
public record JudgeLogAvailabilityVm(string Purpose, string ModelId, int Count, DateTime First, DateTime Last);

public record JudgeAuditPageVm(
    IReadOnlyList<JudgeAuditRunItemVm> Runs,
    IReadOnlyList<string> Purposes,
    IReadOnlyList<EvalModelOptionVm> Models,
    IReadOnlyList<JudgeLogAvailabilityVm> Availability);

/// <summary>Trang danh sách kiểm toán trọng tài: các run gần nhất + nguyên liệu cho form tạo run.</summary>
public class GetJudgeAuditPageQuery
{
    public const int RunLimit = 50;

    private readonly AppDbContext _db;

    public GetJudgeAuditPageQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<JudgeAuditPageVm> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var runs = await _db.JudgeAuditRuns.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(RunLimit)
            .ToListAsync(cancellationToken);

        var models = await _db.AiModels.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.ModelId)
            .ToListAsync(cancellationToken);

        var purposes = JudgeAuditSubject.All.Select(s => s.Purpose).ToList();

        // Đi index (Purpose, CreatedAt) INCLUDE (ModelId, IsSuccess) — không chạm cột mã hoá nào.
        var availability = await _db.AgentModelCallLogs.AsNoTracking()
            .Where(x => purposes.Contains(x.Purpose) && x.IsSuccess)
            .GroupBy(x => new { x.Purpose, x.ModelId })
            .Select(g => new { g.Key.Purpose, g.Key.ModelId, Count = g.Count(), First = g.Min(x => x.CreatedAt), Last = g.Max(x => x.CreatedAt) })
            .ToListAsync(cancellationToken);

        return new JudgeAuditPageVm(
            runs.Select(JudgeAuditRunItemVm.Of).ToList(),
            purposes,
            models.Select(m => new EvalModelOptionVm(m.Id, m.ModelId, m.DisplayLabel())).ToList(),
            availability
                .OrderBy(a => purposes.IndexOf(a.Purpose))
                .ThenByDescending(a => a.Count)
                .Select(a => new JudgeLogAvailabilityVm(a.Purpose, a.ModelId, a.Count, a.First, a.Last))
                .ToList());
    }
}

using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Evals;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

public enum StartJudgeAuditResult
{
    Started,
    UnknownPurpose,
    ArbiterModelNotFound,
    CandidateModelNotFound,
    InvalidSampleSize,

    /// <summary>Không dòng log nào khớp bộ lọc — chạy chỉ ra một run Failed.</summary>
    NoMatchingLogs,

    /// <summary>Đã có run Queued/Running cùng cấu hình — bấm hai lần là trả tiền hai lần.</summary>
    DuplicateRunInProgress
}

/// <summary>Tham số của form "Kiểm toán trọng tài". Ngày nhận theo lịch (UTC), <see cref="To"/> tính trọn ngày.</summary>
public sealed record StartJudgeAuditInput(
    string? Purpose,
    string? SourceModelId,
    DateTime? From,
    DateTime? To,
    int SampleSize,
    Guid? CandidateModelId,
    Guid ArbiterModelId,
    bool ArbitrateAll,
    string? Note);

/// <summary>
/// Tạo một <see cref="JudgeAuditRun"/> Queued (<c>EvalRunWorker</c> nhặt chạy nền — vài trăm lượt × tới 2 lời
/// gọi LLM, quá chậm cho một POST). Snapshot tên model ngay lúc tạo như <see cref="StartEvalRunUseCase"/>.
/// </summary>
public class StartJudgeAuditUseCase
{
    public const int MaxSampleSize = 1000;

    private readonly AppDbContext _db;

    public StartJudgeAuditUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<StartJudgeAuditResult> ExecuteAsync(StartJudgeAuditInput input, string? createdByUsername, CancellationToken cancellationToken = default)
    {
        if (JudgeAuditSubject.For(input.Purpose) is not { } subject)
            return StartJudgeAuditResult.UnknownPurpose;

        if (input.SampleSize is < 1 or > MaxSampleSize)
            return StartJudgeAuditResult.InvalidSampleSize;

        var arbiter = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == input.ArbiterModelId && x.IsActive, cancellationToken);
        if (arbiter == null)
            return StartJudgeAuditResult.ArbiterModelNotFound;

        AiModel? candidate = null;
        if (input.CandidateModelId is { } candidateId)
        {
            candidate = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == candidateId && x.IsActive, cancellationToken);
            if (candidate == null)
                return StartJudgeAuditResult.CandidateModelNotFound;
        }

        var sourceModelId = string.IsNullOrWhiteSpace(input.SourceModelId) ? null : input.SourceModelId.Trim();
        var from = input.From?.Date;
        // "Đến ngày 31/10" nghĩa là gồm cả ngày đó: chặn trên nửa mở ở 0h ngày hôm sau.
        var to = input.To?.Date.AddDays(1);

        var duplicate = await _db.JudgeAuditRuns.AsNoTracking().AnyAsync(x =>
            (x.Status == EvalRunStatus.Queued || x.Status == EvalRunStatus.Running)
            && x.Purpose == subject.Purpose
            && x.SourceModelId == sourceModelId
            && x.CandidateModelId == input.CandidateModelId
            && x.ArbiterModelId == input.ArbiterModelId, cancellationToken);
        if (duplicate)
            return StartJudgeAuditResult.DuplicateRunInProgress;

        var logs = _db.AgentModelCallLogs.AsNoTracking().Where(x => x.Purpose == subject.Purpose && x.IsSuccess);
        if (sourceModelId != null)
            logs = logs.Where(x => x.ModelId == sourceModelId);
        if (from != null)
            logs = logs.Where(x => x.CreatedAt >= from);
        if (to != null)
            logs = logs.Where(x => x.CreatedAt < to);
        if (!await logs.AnyAsync(cancellationToken))
            return StartJudgeAuditResult.NoMatchingLogs;

        _db.JudgeAuditRuns.Add(new JudgeAuditRun
        {
            Purpose = subject.Purpose,
            SourceModelId = sourceModelId,
            From = from,
            To = to,
            SampleSize = input.SampleSize,
            CandidateModelId = candidate?.Id,
            CandidateModelName = candidate?.ModelId,
            ArbiterModelId = arbiter.Id,
            ArbiterModelName = arbiter.ModelId,
            // Chế độ kiểm toán luôn hỏi trọng tài mọi lượt — cờ chỉ có nghĩa khi có ứng viên.
            ArbitrateAll = candidate != null && input.ArbitrateAll,
            Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(),
            CreatedByUsername = createdByUsername
        });
        await _db.SaveChangesAsync(cancellationToken);
        return StartJudgeAuditResult.Started;
    }
}

using ICOGenerator.Data;
using ICOGenerator.Services.Evals;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

/// <summary>Điểm của một model trong run: model đã phán ở production, hoặc model ứng viên chạy lại.</summary>
public record JudgeAuditSubjectVm(bool IsCandidate, JudgeSubjectScore Score);

/// <summary>Một lượt có ít nhất một phán quyết lệch đáp án chuẩn (hoặc lỗi), để người đọc tự xem lại.</summary>
public record JudgeAuditCaseVm(
    Guid Id,
    Guid SourceLogId,
    string SourceModelId,
    DateTime SourceCreatedAt,
    IReadOnlyList<JudgeClaim> Baseline,
    bool BaselineUnreadable,
    IReadOnlyList<JudgeClaim>? Candidate,
    bool CandidateUnreadable,
    IReadOnlyList<JudgeClaim>? Gold,
    bool ArbiterUsed,
    string? ArbiterNotes,
    string? Error,
    // Phần "user" của lượt trọng tài, đọc lại từ log gốc lúc xem. Null = log đã bị xoá (theo project).
    string? JudgeInput);

public record JudgeAuditDetailVm(
    JudgeAuditRunItemVm Run,
    IReadOnlyList<string> Fields,
    IReadOnlyList<JudgeAuditSubjectVm> Subjects,
    int ScoredCases,
    int ErrorCases,
    int ArbiterCalls,
    IReadOnlyList<JudgeAuditCaseVm> Cases,
    int TotalFlaggedCases);

/// <summary>
/// Kết quả một lượt kiểm toán trọng tài: bảng điểm theo TỪNG trường cho mỗi model, cộng danh sách lượt lệch
/// kèm đầu vào để người đọc tự phân xử lại.
///
/// <para>
/// Điểm tính LÚC XEM từ các case (không lưu sẵn trên run): vài trăm dòng, và cách chấm có đổi thì run cũ
/// được chấm lại theo cách mới thay vì giữ một con số không còn so được. Phán quyết đã lưu được chấm theo
/// TỪNG model nguồn — một khoảng lấy mẫu có thể trộn lượt của Sonnet với lượt của Haiku (đổi model giữa kỳ,
/// hoặc lượt Haiku lỗi hạ tầng rồi lùi về model BA).
/// </para>
/// </summary>
public class GetJudgeAuditDetailQuery
{
    /// <summary>Trần số lượt lệch hiện ra (mỗi lượt phải đọc lại và giải mã một dòng log).</summary>
    public const int MaxCasesShown = 200;

    private readonly AppDbContext _db;

    public GetJudgeAuditDetailQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<JudgeAuditDetailVm?> ExecuteAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _db.JudgeAuditRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run == null)
            return null;

        var fields = JudgeAuditSubject.For(run.Purpose)?.Fields ?? Array.Empty<string>();
        var cases = await _db.JudgeAuditCases.AsNoTracking()
            .Where(x => x.JudgeAuditRunId == runId)
            .OrderBy(x => x.SourceCreatedAt)
            .ToListAsync(cancellationToken);

        var parsed = cases.Select(c => new
        {
            Case = c,
            Baseline = JudgeClaim.Deserialize(c.BaselineClaimsJson) ?? Array.Empty<JudgeClaim>(),
            Candidate = JudgeClaim.Deserialize(c.CandidateClaimsJson),
            Gold = JudgeClaim.Deserialize(c.GoldClaimsJson)
        }).ToList();
        var scored = parsed.Where(p => p.Gold != null && p.Case.Error == null).ToList();

        var subjects = scored
            .GroupBy(p => p.Case.SourceModelId)
            .OrderByDescending(g => g.Count())
            .Select(g => new JudgeAuditSubjectVm(false, JudgeAuditScoring.Score(g.Key, fields,
                g.Select(p => new JudgeAuditScoring.Sample(p.Baseline, p.Gold!, p.Case.BaselineUnreadable)))))
            .ToList();
        if (run.CandidateModelName != null)
        {
            subjects.Add(new JudgeAuditSubjectVm(true, JudgeAuditScoring.Score(run.CandidateModelName, fields,
                scored.Where(p => p.Candidate != null)
                    .Select(p => new JudgeAuditScoring.Sample(p.Candidate!, p.Gold!, p.Case.CandidateUnreadable)))));
        }

        var flagged = parsed.Where(p => p.Case.Disagreement || p.Case.Error != null).ToList();
        var shown = flagged.Take(MaxCasesShown).ToList();
        var logIds = shown.Select(p => p.Case.SourceLogId).ToList();
        var inputs = (await _db.AgentModelCallLogs.AsNoTracking()
                .Where(x => logIds.Contains(x.Id))
                .Select(x => new { x.Id, x.RequestJson })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id, x => JudgeLogRequest.TryParse(x.RequestJson)?.User);

        return new JudgeAuditDetailVm(
            JudgeAuditRunItemVm.Of(run),
            fields,
            subjects,
            ScoredCases: scored.Count,
            ErrorCases: parsed.Count(p => p.Case.Error != null),
            ArbiterCalls: parsed.Count(p => p.Case.ArbiterUsed),
            shown.Select(p => new JudgeAuditCaseVm(
                p.Case.Id, p.Case.SourceLogId, p.Case.SourceModelId, p.Case.SourceCreatedAt,
                p.Baseline, p.Case.BaselineUnreadable, p.Candidate, p.Case.CandidateUnreadable, p.Gold,
                p.Case.ArbiterUsed, p.Case.ArbiterNotes, p.Case.Error,
                inputs.GetValueOrDefault(p.Case.SourceLogId))).ToList(),
            flagged.Count);
    }
}

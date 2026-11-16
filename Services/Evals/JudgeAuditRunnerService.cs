using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Localization;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Evals;

/// <summary>
/// Thực thi MỘT <see cref="JudgeAuditRun"/>: lấy mẫu ngẫu nhiên các lời gọi trọng tài đã log, với từng lượt —
/// (1) đọc phán quyết đã lưu; (2) chế độ chạy lại: gửi NGUYÊN VĂN đầu vào cho model ứng viên; (3) dựng đáp án
/// chuẩn — lấy luôn khi hai phán quyết trùng nhau, hỏi trọng tài phân xử khi lệch (hoặc khi run đòi hỏi cả
/// lượt trùng, hoặc ở chế độ kiểm toán); (4) lưu <see cref="JudgeAuditCase"/>.
///
/// <para>
/// <b>Đi đúng đường của production.</b> Lời gọi đi qua một <see cref="LlmClient"/> thật — cùng
/// <c>ChatStructuredAsync</c>, cùng mức structured output của model, cùng lần đòi lại JSON — nên model ứng
/// viên được đo đúng như khi nó chạy trong lượt chat. Khác production đúng hai chỗ, như Prompt Evals: logger
/// no-op (không ghi <c>AgentModelCallLogs</c>: dòng mới mang mục đích trọng tài sẽ lọt vào chính mẫu của lượt
/// kiểm toán sau, và chi phí sẽ bị tính vào project của người dùng) và không budget guard theo project. Chi
/// phí nằm trên case/run.
/// </para>
///
/// <para>
/// Lỗi TỪNG lượt (lời gọi hỏng, trọng tài trả thứ không đọc được) ghi vào <see cref="JudgeAuditCase.Error"/>
/// và lượt đó không tính điểm; chỉ lỗi mức run mới đánh Failed.
/// </para>
/// </summary>
public class JudgeAuditRunnerService
{
    public const string ArbiterPromptPath = "Eval/judge-audit-arbiter.v1.md";

    /// <summary>Mục đích ghi trên ngữ cảnh lời gọi (logger no-op, chỉ để middleware có nhãn).</summary>
    public const string CallPurpose = "JudgeAudit";

    private static readonly Agent AuditAgentStub = new();

    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly PromptTemplateService _prompts;
    private readonly ILogger<JudgeAuditRunnerService> _logger;

    public JudgeAuditRunnerService(
        AppDbContext db,
        IChatClientFactory chatClientFactory,
        LlmSettings llmSettings,
        PromptTemplateService prompts,
        ILogger<LlmClient> llmLogger,
        ILogger<JudgeAuditRunnerService> logger)
        : this(db, new LlmClient(chatClientFactory, new NullModelCallLogger(), new UnboundedBudgetGuard(), llmSettings, llmLogger), prompts, logger)
    {
    }

    internal JudgeAuditRunnerService(AppDbContext db, ILlmClient llm, PromptTemplateService prompts, ILogger<JudgeAuditRunnerService> logger)
    {
        _db = db;
        _llm = llm;
        _prompts = prompts;
        _logger = logger;
    }

    public async Task RunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _db.JudgeAuditRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run == null)
            return;

        run.Status = EvalRunStatus.Running;
        run.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var subject = JudgeAuditSubject.For(run.Purpose);
        var arbiter = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == run.ArbiterModelId, cancellationToken);
        AiModel? candidate = null;
        if (run.CandidateModelId is { } candidateId)
            candidate = await _db.AiModels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == candidateId, cancellationToken);

        if (subject == null || arbiter == null || (run.CandidateModelId != null && candidate == null))
        {
            await FinishAsync(run, EvalRunStatus.Failed, Loc.T("JudgeAuditRunnerService.RunAsync"), cancellationToken);
            return;
        }

        var pool = await SamplePoolAsync(run, cancellationToken);
        run.CaseCount = Math.Min(run.SampleSize, pool.Count);
        await _db.SaveChangesAsync(cancellationToken);
        if (run.CaseCount == 0)
        {
            await FinishAsync(run, EvalRunStatus.Failed, Loc.T("JudgeAuditRunnerService.RunAsync.2"), cancellationToken);
            return;
        }

        var arbiterPrompt = _prompts.Get(ArbiterPromptPath);

        // Duyệt hết bể đã xáo cho tới khi đủ mẫu: dòng log không dựng lại được (lần đòi lại JSON, lượt có ảnh)
        // bị bỏ qua mà không chiếm chỗ của một lượt dùng được.
        foreach (var logId in pool)
        {
            if (run.CompletedCount >= run.CaseCount)
                break;
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsCancelRequestedAsync(run.Id, cancellationToken))
            {
                await FinishAsync(run, EvalRunStatus.Cancelled,
                    Loc.T("JudgeAuditRunnerService.RunAsync.3", run.CompletedCount, run.CaseCount), cancellationToken);
                return;
            }

            JudgeAuditCase? auditCase;
            try
            {
                auditCase = await AuditOneAsync(run, subject, candidate, arbiter, arbiterPrompt, logId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Judge audit case for log {LogId} failed unexpectedly in run {RunId}.", logId, run.Id);
                auditCase = new JudgeAuditCase { JudgeAuditRunId = run.Id, SourceLogId = logId, Error = ex.Message };
            }

            if (auditCase == null)
                continue;

            _db.JudgeAuditCases.Add(auditCase);
            run.CompletedCount++;
            run.TotalCost += auditCase.Cost;
            if (auditCase.Disagreement)
                run.DisagreementCount++;
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Bể cạn trước khi đủ mẫu (nhiều dòng không dùng được): chốt lại tổng theo số lượt thật.
        run.CaseCount = run.CompletedCount;
        await FinishAsync(run, EvalRunStatus.Completed, null, cancellationToken);
    }

    /// <summary>
    /// Id các dòng log khớp bộ lọc, xáo ngẫu nhiên với hạt giống theo id run (chạy lại cùng run ra cùng thứ
    /// tự). Chỉ đọc cột không mã hoá (Purpose/ModelId/CreatedAt) nên lọc được ở SQL; xáo trong bộ nhớ vì
    /// "ORDER BY NEWID()" không có bản dịch chung cho cả SQL Server lẫn Sqlite.
    /// </summary>
    private async Task<List<Guid>> SamplePoolAsync(JudgeAuditRun run, CancellationToken cancellationToken)
    {
        var query = _db.AgentModelCallLogs.AsNoTracking()
            .Where(x => x.Purpose == run.Purpose && x.IsSuccess);
        if (!string.IsNullOrWhiteSpace(run.SourceModelId))
            query = query.Where(x => x.ModelId == run.SourceModelId);
        if (run.From is { } from)
            query = query.Where(x => x.CreatedAt >= from);
        if (run.To is { } to)
            query = query.Where(x => x.CreatedAt < to);

        var ids = await query.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(cancellationToken);
        var random = new Random(BitConverter.ToInt32(run.Id.ToByteArray(), 0));
        for (var i = ids.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }
        return ids;
    }

    /// <summary>Kiểm toán một dòng log. Null = dòng không dựng lại được đầu vào, bỏ qua không tính.</summary>
    private async Task<JudgeAuditCase?> AuditOneAsync(
        JudgeAuditRun run, JudgeAuditSubject subject, AiModel? candidate, AiModel arbiter, string arbiterPrompt,
        Guid logId, CancellationToken cancellationToken)
    {
        var log = await _db.AgentModelCallLogs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == logId, cancellationToken);
        if (log == null || JudgeLogRequest.TryParse(log.RequestJson) is not { } input)
            return null;

        var auditCase = new JudgeAuditCase
        {
            JudgeAuditRunId = run.Id,
            SourceLogId = log.Id,
            ProjectId = log.ProjectId,
            SourceModelId = log.ModelId,
            SourceCreatedAt = log.CreatedAt
        };
        var logContext = new ModelCallLogContext(log.ProjectId, AuditAgentStub, CallPurpose);

        var baseline = subject.ParseClaims(log.ResponseText);
        auditCase.BaselineUnreadable = baseline == null;
        baseline ??= Array.Empty<JudgeClaim>();
        auditCase.BaselineClaimsJson = JudgeClaim.Serialize(baseline);

        IReadOnlyList<JudgeClaim>? candidateClaims = null;
        if (candidate != null)
        {
            var replay = new List<ChatMessage> { new(ChatRole.System, input.System), new(ChatRole.User, input.User) };
            var (result, claims) = await subject.AskAsync(_llm, candidate, replay, logContext, cancellationToken);
            auditCase.Cost += CostOf(result, candidate);
            if (!result.IsSuccess)
            {
                auditCase.Error = Loc.T("JudgeAuditRunnerService.AuditOneAsync", result.ErrorMessage);
                return auditCase;
            }
            auditCase.CandidateUnreadable = claims == null;
            candidateClaims = claims ?? Array.Empty<JudgeClaim>();
            auditCase.CandidateClaimsJson = JudgeClaim.Serialize(candidateClaims);
        }

        IReadOnlyList<JudgeClaim> gold;
        var needsArbiter = candidateClaims == null || run.ArbitrateAll || !JudgeClaim.SameSet(baseline, candidateClaims);
        if (!needsArbiter)
        {
            gold = baseline;
        }
        else
        {
            // Phán quyết không đọc được đưa cho trọng tài ĐÚNG như vậy (không phải "rỗng"): nó cần biết model
            // đã hỏng định dạng, không phải đã chủ ý không phán gì.
            var verdicts = new List<IReadOnlyList<JudgeClaim>?> { auditCase.BaselineUnreadable ? null : baseline };
            if (candidateClaims != null)
                verdicts.Add(auditCase.CandidateUnreadable ? null : candidateClaims);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, arbiterPrompt),
                new(ChatRole.User, JudgeAuditSubject.RenderArbiterInput(input, subject.Fields, verdicts))
            };
            var (result, claims, notes) = await subject.ArbitrateAsync(_llm, arbiter, messages, logContext, cancellationToken);
            auditCase.Cost += CostOf(result, arbiter);
            auditCase.ArbiterUsed = true;
            if (!result.IsSuccess || claims == null)
            {
                auditCase.Error = result.IsSuccess
                    ? Loc.T("JudgeAuditRunnerService.AuditOneAsync.2")
                    : Loc.T("JudgeAuditRunnerService.AuditOneAsync.3", result.ErrorMessage);
                return auditCase;
            }
            gold = claims;
            auditCase.ArbiterNotes = notes;
        }

        auditCase.GoldClaimsJson = JudgeClaim.Serialize(gold);
        auditCase.Disagreement = !JudgeClaim.SameSet(baseline, gold)
                                 || (candidateClaims != null && !JudgeClaim.SameSet(candidateClaims, gold));
        return auditCase;
    }

    private static decimal CostOf(LlmCallResult result, AiModel model) =>
        LlmCost.Usd(result.PromptTokens, result.CachedPromptTokens, result.CompletionTokens,
            new LlmPrice(model.InputPricePerMillionTokens, model.CachedInputPricePerMillionTokens, model.OutputPricePerMillionTokens));

    private async Task<bool> IsCancelRequestedAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.JudgeAuditRuns.AsNoTracking()
            .Where(x => x.Id == runId)
            .Select(x => x.CancelRequestedAt)
            .FirstOrDefaultAsync(cancellationToken) != null;

    private async Task FinishAsync(JudgeAuditRun run, EvalRunStatus status, string? error, CancellationToken cancellationToken)
    {
        run.Status = status;
        run.Error = error;
        run.FinishedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Kiểm toán không thuộc project nào để giữ trần (như Prompt Evals) — chi phí nằm trên run.</summary>
    private sealed class UnboundedBudgetGuard : IBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

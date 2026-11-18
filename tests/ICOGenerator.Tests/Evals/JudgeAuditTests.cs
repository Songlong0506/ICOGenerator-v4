using System.Text.Json;
using ICOGenerator.Application.Evals;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Evals;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Evals;

// KIỂM TOÁN TRỌNG TÀI: đo một model trọng tài phán đúng tới đâu trên chính các lượt đã log. File này ghim
// (1) cách đọc phán quyết thành khẳng định, (2) cách chấm — phán THỪA tách khỏi phán SÓT, vì với
// repeats/emptyChips/drop phán thừa là XOÁ OAN, và (3) hành vi của runner: trùng nhau thì không tốn lời gọi
// trọng tài, lệch thì hỏi; dòng log của lần đòi lại JSON không bị đếm đôi.
public class JudgeAuditTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly Guid _candidateId = Guid.NewGuid();
    private readonly Guid _arbiterId = Guid.NewGuid();

    public JudgeAuditTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        var main = new AiModel { ModelId = "sonnet" };
        db.AiModels.AddRange(
            main,
            new AiModel { Id = _candidateId, ModelId = "haiku", InputPricePerMillionTokens = 1m, OutputPricePerMillionTokens = 1m },
            new AiModel { Id = _arbiterId, ModelId = "opus", InputPricePerMillionTokens = 10m, OutputPricePerMillionTokens = 10m });
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.Agents.Add(new Agent { Id = _agentId, AiModelId = main.Id });
        db.SaveChanges();
    }

    // ── Khẳng định ───────────────────────────────────────────────────────────────────────────────

    // Lượt BA là hai lời gọi, mỗi mục đích một subject. Dòng log BATurnJudge CŨ (trước khi tách) phán đủ năm
    // trường: subject của nó chỉ đọc repeats, bốn trường kia không được tính hai lần.
    [Fact]
    public void TurnVerdicts_BecomeNormalizedClaims_DroppingWhatTheGuardsWouldIgnore()
    {
        const string legacyFiveFieldAnswer = """
            ```json
            { "repeats": [2, 2, 0], "emptyChips": [ { "question": 1, "chips": ["  Khác ", ""] } ],
              "yesNoChips": [], "userMentionsExistingSource": true, "asksForFile": false }
            ```
            """;

        var repeats = JudgeAuditSubject.For(InterviewJudge.TurnPurpose)!.ParseClaims(legacyFiveFieldAnswer)!;
        var signals = JudgeAuditSubject.For(InterviewJudge.SignalPurpose)!.ParseClaims(legacyFiveFieldAnswer)!;

        Assert.Equal(new[] { "repeats: Q2" }, repeats.Select(c => c.ToString()));
        Assert.Equal(
            new[] { "emptyChips: Q1 · Khác", "userMentionsExistingSource: có" },
            signals.Select(c => c.ToString()).OrderBy(x => x, StringComparer.Ordinal));
    }

    // Guard xoá câu hỏi tồn đọng như nhau với mọi lý do hợp lệ ⇒ hai phán quyết cùng xoá Q2 với hai lý do
    // khác nhau là CÙNG hiệu lực, không được đếm là lệch.
    [Fact]
    public void CoverageVerdict_IgnoresTheDropReason()
    {
        var subject = JudgeAuditSubject.For(InterviewJudge.CoveragePurpose)!;

        var a = subject.ParseClaims("""{"drop":[{"question":2,"reason":"answered"}]}""")!;
        var b = subject.ParseClaims("""{"drop":[{"question":2,"reason":"hollow"}]}""")!;

        Assert.True(JudgeClaim.SameSet(a, b));
    }

    [Fact]
    public void AnUnreadableVerdict_IsNull_NotEmpty()
    {
        Assert.Null(JudgeAuditSubject.For(InterviewJudge.TurnPurpose)!.ParseClaims("xin lỗi, tôi không chắc"));
    }

    // ── Chấm điểm ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scoring_SeparatesExtraFromMissed_PerField()
    {
        var fields = new[] { "repeats", "emptyChips" };
        var samples = new[]
        {
            // Đúng hoàn toàn (cả hai rỗng) — loại lượt chiếm đa số, và lý do "% lượt đúng" một mình vô nghĩa.
            new JudgeAuditScoring.Sample(Array.Empty<JudgeClaim>(), Array.Empty<JudgeClaim>(), false),
            // Chặn oan Q1.
            new JudgeAuditScoring.Sample(new[] { new JudgeClaim("repeats", "Q1") }, Array.Empty<JudgeClaim>(), false),
            // Bỏ sót chip rỗng; repeats đúng.
            new JudgeAuditScoring.Sample(
                new[] { new JudgeClaim("repeats", "Q2") },
                new[] { new JudgeClaim("repeats", "Q2"), new JudgeClaim("emptyChips", "Q1 · Khác") }, false),
        };

        var score = JudgeAuditScoring.Score("haiku", fields, samples);

        Assert.Equal(3, score.Cases);
        Assert.Equal(1, score.ExactCases);
        Assert.Equal(1, score.CasesWithExtra);
        var repeats = score.Fields.Single(f => f.Field == "repeats");
        Assert.Equal((1, 1, 0), (repeats.Correct, repeats.Extra, repeats.Missed));
        Assert.Equal(0.5, repeats.Precision);
        var chips = score.Fields.Single(f => f.Field == "emptyChips");
        Assert.Equal((0, 0, 1), (chips.Correct, chips.Extra, chips.Missed));
        Assert.Null(chips.Precision); // không có gì để chia — hiện "–", không hiện 0%
        Assert.Equal(0.0, chips.Recall);
    }

    // ── Dựng lại đầu vào từ log ──────────────────────────────────────────────────────────────────

    [Fact]
    public void JudgeLogRequest_AcceptsOnlyAnOriginalTextOnlyCall()
    {
        Assert.Equal(new JudgeLogRequest.Input("luật", "đầu vào"), JudgeLogRequest.TryParse(Request("luật", "đầu vào")));

        // Dòng log của lần ĐÒI LẠI JSON: cùng một lượt với dòng gốc, đếm cả hai là đếm đôi.
        Assert.Null(JudgeLogRequest.TryParse(Request("luật", "đầu vào", retry: true)));

        // Lượt có ảnh: bytes ảnh không nằm trong log, chạy lại sẽ là một đầu vào khác.
        Assert.Null(JudgeLogRequest.TryParse("""
            {"messages":[{"role":"system","content":"luật"},{"role":"user","content":[{"type":"image","index":1}]}]}
            """));
    }

    // ── Runner ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Replay_AgreeingVerdictsSkipTheArbiter_DisagreeingOnesAskIt()
    {
        var agreeLog = await AddLogAsync(user: "agree", response: """{"repeats":[1]}""");
        var disagreeLog = await AddLogAsync(user: "disagree", response: """{"repeats":[1]}""");
        var llm = new FakeLlm
        {
            Candidate = user => user == "agree" ? """{"repeats":[1]}""" : """{"repeats":[]}""",
            Arbiter = """{"verdict":{"repeats":[]},"notes":"A chặn oan Q1."}"""
        };

        var runId = await AddRunAsync(candidate: true);
        await RunAsync(llm, runId);

        await using var db = NewDb();
        var run = await db.JudgeAuditRuns.SingleAsync(x => x.Id == runId);
        Assert.Equal(EvalRunStatus.Completed, run.Status);
        Assert.Equal(2, run.CompletedCount);
        Assert.Equal(1, run.DisagreementCount);
        Assert.Equal(2, llm.CandidateCalls);
        Assert.Equal(1, llm.ArbiterCalls);
        Assert.True(run.TotalCost > 0);

        var disagree = await db.JudgeAuditCases.SingleAsync(x => x.SourceLogId == disagreeLog);
        Assert.True(disagree.ArbiterUsed);
        Assert.Equal("A chặn oan Q1.", disagree.ArbiterNotes);
        Assert.Empty(JudgeClaim.Deserialize(disagree.GoldClaimsJson)!);
        Assert.False((await db.JudgeAuditCases.SingleAsync(x => x.SourceLogId == agreeLog)).ArbiterUsed);

        // Trọng tài thấy các phán quyết dán nhãn A/B, không thấy tên model.
        Assert.Contains("### Phán quyết A", llm.LastArbiterInput, StringComparison.Ordinal);
        Assert.Contains("### Phán quyết B", llm.LastArbiterInput, StringComparison.Ordinal);
        Assert.DoesNotContain("haiku", llm.LastArbiterInput, StringComparison.Ordinal);
    }

    // Chế độ kiểm toán (không ứng viên): hỏi trọng tài MỌI lượt, lọc đúng model nguồn, bỏ qua dòng đòi lại.
    [Fact]
    public async Task Audit_AsksTheArbiterForEveryCase_OfTheChosenSourceModelOnly()
    {
        await AddLogAsync(user: "u1", response: """{"repeats":[1]}""", model: "haiku");
        await AddLogAsync(user: "u2", response: "không phải JSON", model: "haiku");
        await AddLogAsync(user: "u3", response: """{"repeats":[1]}""", model: "haiku", retry: true);
        await AddLogAsync(user: "u4", response: """{"repeats":[1]}""", model: "sonnet");
        var llm = new FakeLlm { Arbiter = """{"verdict":{"repeats":[1]},"notes":"đều đúng"}""" };

        var runId = await AddRunAsync(candidate: false, sourceModel: "haiku");
        await RunAsync(llm, runId);

        await using var db = NewDb();
        var run = await db.JudgeAuditRuns.SingleAsync(x => x.Id == runId);
        var cases = await db.JudgeAuditCases.Where(x => x.JudgeAuditRunId == runId).ToListAsync();
        Assert.Equal(2, run.CaseCount);
        Assert.Equal(2, cases.Count);
        Assert.All(cases, c => Assert.Equal("haiku", c.SourceModelId));
        Assert.Equal(0, llm.CandidateCalls);
        Assert.Equal(2, llm.ArbiterCalls);

        // Phán quyết không đọc được: lưu là rỗng + cờ (đúng hiệu lực fail-open), và bị tính là lệch.
        var unreadable = Assert.Single(cases, c => c.BaselineUnreadable);
        Assert.Empty(JudgeClaim.Deserialize(unreadable.BaselineClaimsJson)!);
        Assert.True(unreadable.Disagreement);
        Assert.Equal(1, run.DisagreementCount);
    }

    [Fact]
    public async Task AFailedCandidateCall_IsRecordedAsAnErrorCase_NotScored()
    {
        await AddLogAsync(user: "u", response: """{"repeats":[]}""");
        var llm = new FakeLlm { CandidateFails = true };

        var runId = await AddRunAsync(candidate: true);
        await RunAsync(llm, runId);

        await using var db = NewDb();
        var auditCase = await db.JudgeAuditCases.SingleAsync(x => x.JudgeAuditRunId == runId);
        Assert.NotNull(auditCase.Error);
        Assert.Null(auditCase.GoldClaimsJson);
        Assert.Equal(0, llm.ArbiterCalls);
    }

    [Fact]
    public async Task NoMatchingLogs_FailsTheRun()
    {
        var runId = await AddRunAsync(candidate: false);
        await RunAsync(new FakeLlm(), runId);

        await using var db = NewDb();
        Assert.Equal(EvalRunStatus.Failed, (await db.JudgeAuditRuns.SingleAsync(x => x.Id == runId)).Status);
    }

    // ── Tạo run ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_RejectsAFilterThatMatchesNoLogs()
    {
        await AddLogAsync(user: "u", response: "{}", model: "sonnet");
        await using var db = NewDb();

        var result = await new StartJudgeAuditUseCase(db).ExecuteAsync(
            new StartJudgeAuditInput(InterviewJudge.TurnPurpose, "haiku", null, null, 10, null, _arbiterId, false, null), "admin");

        Assert.Equal(StartJudgeAuditResult.NoMatchingLogs, result);
        Assert.Empty(db.JudgeAuditRuns);
    }

    // "Đến ngày X" gồm cả ngày X (chặn trên nửa mở ở 0h ngày hôm sau); chế độ kiểm toán bỏ cờ ArbitrateAll —
    // nó vốn luôn hỏi trọng tài mọi lượt, để cờ bật trên run chỉ làm người đọc tưởng có hai kiểu kiểm toán.
    [Fact]
    public async Task Start_MakesTheEndDateInclusive_AndIgnoresArbitrateAllWithoutACandidate()
    {
        await AddLogAsync(user: "u", response: "{}");
        await using var db = NewDb();

        var result = await new StartJudgeAuditUseCase(db).ExecuteAsync(
            new StartJudgeAuditInput(InterviewJudge.TurnPurpose, null, null, DateTime.UtcNow.Date, 10, null, _arbiterId, true, " thử "), "admin");

        Assert.Equal(StartJudgeAuditResult.Started, result);
        var run = await db.JudgeAuditRuns.SingleAsync();
        Assert.Equal(DateTime.UtcNow.Date.AddDays(1), run.To);
        Assert.False(run.ArbitrateAll);
        Assert.Equal("thử", run.Note);
        Assert.Equal("opus", run.ArbiterModelName);
    }

    [Theory]
    [InlineData("NotAJudge", 10, StartJudgeAuditResult.UnknownPurpose)]
    [InlineData(InterviewJudge.TurnPurpose, 0, StartJudgeAuditResult.InvalidSampleSize)]
    [InlineData(InterviewJudge.TurnPurpose, StartJudgeAuditUseCase.MaxSampleSize + 1, StartJudgeAuditResult.InvalidSampleSize)]
    public async Task Start_ValidatesPurposeAndSampleSize(string purpose, int sampleSize, StartJudgeAuditResult expected)
    {
        await using var db = NewDb();
        var result = await new StartJudgeAuditUseCase(db).ExecuteAsync(
            new StartJudgeAuditInput(purpose, null, null, null, sampleSize, null, _arbiterId, false, null), "admin");
        Assert.Equal(expected, result);
    }

    // ── Hạ tầng test ─────────────────────────────────────────────────────────────────────────────

    private static string Request(string system, string user, bool retry = false)
    {
        var messages = new List<object> { new { role = "system", content = system }, new { role = "user", content = user } };
        if (retry)
        {
            messages.Add(new { role = "assistant", content = "không phải JSON" });
            messages.Add(new { role = "user", content = "trả lại JSON" });
        }
        return JsonSerializer.Serialize(new { model = "x", messages });
    }

    private async Task<Guid> AddLogAsync(string user, string response, string model = "sonnet", bool retry = false)
    {
        await using var db = NewDb();
        var log = new AgentModelCallLog
        {
            ProjectId = _projectId,
            AgentId = _agentId,
            ModelId = model,
            Purpose = InterviewJudge.TurnPurpose,
            RequestJson = Request("luật trọng tài", user, retry),
            ResponseText = response,
            IsSuccess = true
        };
        db.AgentModelCallLogs.Add(log);
        await db.SaveChangesAsync();
        return log.Id;
    }

    private async Task<Guid> AddRunAsync(bool candidate, string? sourceModel = null)
    {
        await using var db = NewDb();
        var run = new JudgeAuditRun
        {
            Purpose = InterviewJudge.TurnPurpose,
            SourceModelId = sourceModel,
            SampleSize = 50,
            CandidateModelId = candidate ? _candidateId : null,
            CandidateModelName = candidate ? "haiku" : null,
            ArbiterModelId = _arbiterId,
            ArbiterModelName = "opus"
        };
        db.JudgeAuditRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task RunAsync(FakeLlm llm, Guid runId)
    {
        await using var db = NewDb();
        await new JudgeAuditRunnerService(db, llm, new StubPrompts(), NullLogger<JudgeAuditRunnerService>.Instance).RunAsync(runId);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        public Func<string, string> Candidate = _ => "{}";
        public string Arbiter = "{}";
        public bool CandidateFails;
        public int CandidateCalls;
        public int ArbiterCalls;
        public string LastArbiterInput = string.Empty;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Kiểm toán chỉ đi đường structured output.");

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            var user = messages.Last(m => m.Role == ChatRole.User).Text;
            string content;
            if (model.ModelId == "opus")
            {
                ArbiterCalls++;
                LastArbiterInput = user;
                content = Arbiter;
            }
            else
            {
                CandidateCalls++;
                if (CandidateFails)
                    return Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "boom" }, (T?)null));
                content = Candidate(user);
            }
            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = content, PromptTokens = 1000, CompletionTokens = 100 }, (T?)null));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => relativePath;
    }
}

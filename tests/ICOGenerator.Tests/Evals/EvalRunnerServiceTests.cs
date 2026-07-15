using System.Runtime.CompilerServices;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Evals;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Evals;

// EvalRunnerService: mỗi scenario = 1 lời gọi model mục tiêu + 1 lời gọi judge; điểm judge lưu vào
// EvalResult, run chốt AverageScore. Lỗi TỪNG scenario (judge trả rác, call lỗi) không làm gãy run;
// lỗi mức run (model bị xoá, không còn scenario) mới đánh Failed.
public class EvalRunnerServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _targetModelId = Guid.NewGuid();
    private readonly Guid _judgeModelId = Guid.NewGuid();

    public EvalRunnerServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        // Đơn giá khác nhau để test khẳng định chi phí được tính theo ĐÚNG model của từng lời gọi.
        db.AiModels.AddRange(
            new AiModel { Id = _targetModelId, Name = "Target", ModelId = "target", InputPricePerMillionTokens = 1m, OutputPricePerMillionTokens = 2m },
            new AiModel { Id = _judgeModelId, Name = "Judge", ModelId = "judge", InputPricePerMillionTokens = 3m, OutputPricePerMillionTokens = 4m });
        db.SaveChanges();
    }

    [Fact]
    public async Task RunAsync_ScoresEveryScenario_AndComputesAverage()
    {
        Guid runId;
        await using (var db = NewDb())
        {
            db.EvalScenarios.AddRange(
                new EvalScenario { Name = "S1", PromptKey = "BA/x.md", UserInput = "in1", Criteria = "c1" },
                new EvalScenario { Name = "S2", PromptKey = "BA/x.md", UserInput = "in2", Criteria = "c2" });
            var run = NewRun();
            db.EvalRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        await using (var db = NewDb())
        {
            var runner = NewRunner(db, judgeReply: """{"score": 4, "reasoning": "ổn"}""");
            await runner.RunAsync(runId);
        }

        await using var verify = NewDb();
        var reloaded = await verify.EvalRuns.SingleAsync(x => x.Id == runId);
        var results = await verify.EvalResults.Where(x => x.EvalRunId == runId).ToListAsync();

        Assert.Equal(EvalRunStatus.Completed, reloaded.Status);
        Assert.Equal(2, reloaded.ScenarioCount);
        Assert.Equal(2, reloaded.CompletedCount);
        Assert.Equal(4, reloaded.AverageScore);
        Assert.True(reloaded.TotalTokens > 0);
        // Chi phí USD chốt lúc chạy theo đơn giá model; cả hai model đều có giá > 0 nên tổng phải > 0
        // và bằng đúng tổng chi phí target+judge cộng dồn từ các result.
        Assert.True(reloaded.TotalCost > 0);
        Assert.Equal(results.Sum(r => r.TargetCost + r.JudgeCost), reloaded.TotalCost);
        Assert.NotNull(reloaded.FinishedAt);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.IsSuccess);
            Assert.Equal(4, r.Score);
            Assert.Equal("ổn", r.JudgeReasoning);
            Assert.Equal("câu trả lời của target", r.Output);
            Assert.True(r.TargetCost > 0);
            Assert.True(r.JudgeCost > 0);
        });
    }

    [Fact]
    public async Task RunAsync_JudgeReturnsGarbage_ResultHasNoScore_ButRunCompletes()
    {
        Guid runId;
        await using (var db = NewDb())
        {
            db.EvalScenarios.Add(new EvalScenario { Name = "S1", PromptKey = "BA/x.md", UserInput = "in", Criteria = "c" });
            var run = NewRun();
            db.EvalRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        await using (var db = NewDb())
        {
            await NewRunner(db, judgeReply: "tôi thấy khá ổn đấy (không phải JSON)").RunAsync(runId);
        }

        await using var verify = NewDb();
        var reloaded = await verify.EvalRuns.SingleAsync(x => x.Id == runId);
        var result = await verify.EvalResults.SingleAsync(x => x.EvalRunId == runId);

        Assert.Equal(EvalRunStatus.Completed, reloaded.Status);
        Assert.Null(reloaded.AverageScore);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Score);
        Assert.Contains("không đúng định dạng", result.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_MissingModel_FailsRun()
    {
        Guid runId;
        await using (var db = NewDb())
        {
            db.EvalScenarios.Add(new EvalScenario { Name = "S1", PromptKey = "BA/x.md", UserInput = "in", Criteria = "c" });
            var run = NewRun();
            run.TargetModelId = Guid.NewGuid(); // model không tồn tại
            db.EvalRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        await using (var db = NewDb())
        {
            await NewRunner(db, judgeReply: "{}").RunAsync(runId);
        }

        await using var verify = NewDb();
        var reloaded = await verify.EvalRuns.SingleAsync(x => x.Id == runId);
        Assert.Equal(EvalRunStatus.Failed, reloaded.Status);
        Assert.NotNull(reloaded.Error);
        Assert.Empty(await verify.EvalResults.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_PromptKeyFilter_OnlyRunsMatchingScenarios()
    {
        Guid runId;
        await using (var db = NewDb())
        {
            db.EvalScenarios.AddRange(
                new EvalScenario { Name = "Khớp", PromptKey = "BA/x.md", UserInput = "in", Criteria = "c" },
                new EvalScenario { Name = "Không khớp", PromptKey = "BA/y.md", UserInput = "in", Criteria = "c" },
                new EvalScenario { Name = "Tắt", PromptKey = "BA/x.md", UserInput = "in", Criteria = "c", IsActive = false });
            var run = NewRun();
            run.PromptKey = "BA/x.md";
            db.EvalRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        await using (var db = NewDb())
        {
            await NewRunner(db, judgeReply: """{"score": 5, "reasoning": "ok"}""").RunAsync(runId);
        }

        await using var verify = NewDb();
        var results = await verify.EvalResults.Where(x => x.EvalRunId == runId).ToListAsync();
        Assert.Single(results);
        Assert.Equal("Khớp", results[0].ScenarioName);
    }

    [Fact]
    public async Task RunAsync_StampsPromptVersionOnResults_NullMeansFile()
    {
        Guid runId;
        await using (var db = NewDb())
        {
            db.EvalScenarios.Add(new EvalScenario { Name = "S1", PromptKey = "BA/x.md", UserInput = "in", Criteria = "c" });
            var run = NewRun();
            db.EvalRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        // (1) Không có bản DB active ⇒ kết quả ghi "file" (PromptVersionId null).
        await using (var db = NewDb())
        {
            await NewRunner(db, judgeReply: """{"score": 4, "reasoning": "ổn"}""").RunAsync(runId);
        }
        await using (var verify = NewDb())
        {
            var result = await verify.EvalResults.SingleAsync(x => x.EvalRunId == runId);
            Assert.Null(result.PromptVersionId);
            Assert.Null(result.PromptVersionNumber);
        }

        // (2) Có bản DB active ⇒ kết quả snapshot đúng id + số phiên bản đã đo.
        var overrideId = Guid.NewGuid();
        Guid secondRunId;
        await using (var db = NewDb())
        {
            var run = NewRun();
            db.EvalRuns.Add(run);
            await db.SaveChangesAsync();
            secondRunId = run.Id;
        }
        await using (var db = NewDb())
        {
            await NewRunner(db, judgeReply: """{"score": 4, "reasoning": "ổn"}""",
                overrides: new FixedPromptOverrideProvider(new PromptOverride(overrideId, 3, "## prompt v3"))).RunAsync(secondRunId);
        }

        await using var verify2 = NewDb();
        var stamped = await verify2.EvalResults.SingleAsync(x => x.EvalRunId == secondRunId);
        Assert.Equal(overrideId, stamped.PromptVersionId);
        Assert.Equal(3, stamped.PromptVersionNumber);
    }

    private EvalRun NewRun() => new()
    {
        TargetModelId = _targetModelId,
        TargetModelName = "Target",
        JudgeModelId = _judgeModelId,
        JudgeModelName = "Judge"
    };

    private EvalRunnerService NewRunner(AppDbContext db, string judgeReply, IPromptOverrideProvider? overrides = null) =>
        new(db,
            new FakeChatClientFactory("câu trả lời của target", judgeReply),
            new StubPromptTemplateService(),
            overrides ?? new NoPromptOverrideProvider(),
            new ConfigurationBuilder().Build(),
            NullLogger<EvalRunnerService>.Instance);

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Trả lời cố định theo ModelId: model "target" → câu trả lời, model "judge" → verdict JSON (hoặc rác).
    private sealed class FakeChatClientFactory : IChatClientFactory
    {
        private readonly string _targetReply;
        private readonly string _judgeReply;

        public FakeChatClientFactory(string targetReply, string judgeReply)
        {
            _targetReply = targetReply;
            _judgeReply = judgeReply;
        }

        public IChatClient Create(AiModel model) =>
            new FakeChatClient(model.ModelId == "judge" ? _judgeReply : _targetReply);
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly string _reply;

        public FakeChatClient(string reply) => _reply = reply;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, _reply);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class NoPromptOverrideProvider : IPromptOverrideProvider
    {
        public PromptOverride? GetActiveOverride(string promptKey) => null;
        public void Invalidate() { }
    }

    private sealed class FixedPromptOverrideProvider : IPromptOverrideProvider
    {
        private readonly PromptOverride _override;
        public FixedPromptOverrideProvider(PromptOverride @override) => _override = @override;
        public PromptOverride? GetActiveOverride(string promptKey) => _override;
        public void Invalidate() { }
    }

    private sealed class StubPromptTemplateService : PromptTemplateService
    {
        public StubPromptTemplateService() : base(new FakeWebHostEnvironment()) { }
        public override string Get(string relativePath) => "## prompt stub";
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}

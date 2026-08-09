using ICOGenerator.Application.Evals;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace ICOGenerator.Tests.Evals;

// Trang Prompt Evals: bảng Runs lọc + phân trang PHÍA SERVER, bảng Scenarios mang điểm của lần chấm gần
// nhất, và modal chạy có ước lượng chi phí dựng từ chi phí THẬT của các run gần đây.
public class GetEvalPageQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _scenarioA = Guid.NewGuid();
    private readonly Guid _scenarioB = Guid.NewGuid();

    public GetEvalPageQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.EvalScenarios.AddRange(
            new EvalScenario { Id = _scenarioA, Name = "A", PromptKey = "BA/x.md", UserInput = "in", Criteria = "c" },
            new EvalScenario { Id = _scenarioB, Name = "B", PromptKey = "BA/y.md", UserInput = "in", Criteria = "c", Kind = EvalScenarioKind.Interview },
            new EvalScenario { Name = "Tắt", PromptKey = "BA/z.md", UserInput = "in", Criteria = "c", IsActive = false });
        db.SaveChanges();
    }

    [Fact]
    public async Task Execute_RunsArePagedAndFiltered()
    {
        await using (var seed = NewDb())
        {
            for (var i = 0; i < 7; i++)
                seed.EvalRuns.Add(NewRun(EvalRunStatus.Completed, promptKey: i < 3 ? "BA/x.md" : null, minutesAgo: i));
            seed.EvalRuns.Add(NewRun(EvalRunStatus.Failed, promptKey: "BA/x.md", minutesAgo: 20));
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();

        var firstPage = await NewSut(db).ExecuteAsync(page: 1, pageSize: 5);
        Assert.Equal(8, firstPage.TotalRunCount);
        Assert.Equal(5, firstPage.Runs.Count);
        Assert.Equal(2, firstPage.TotalPages);

        var secondPage = await NewSut(db).ExecuteAsync(page: 2, pageSize: 5);
        Assert.Equal(3, secondPage.Runs.Count);

        // Lọc chồng nhau: prompt key + trạng thái.
        var filtered = await NewSut(db).ExecuteAsync(runPromptKey: "BA/x.md", runStatus: EvalRunStatus.Failed);
        Assert.Equal(1, filtered.TotalRunCount);
        Assert.True(filtered.HasRunFilter);
        Assert.All(filtered.Runs, r => Assert.Equal(EvalRunStatus.Failed, r.Status));

        // Danh mục cho ô lọc chỉ gồm các prompt key THỰC SỰ có run.
        Assert.Equal(new[] { "BA/x.md" }, filtered.RunPromptKeys);
    }

    [Fact]
    public async Task Execute_RunCarriesActorFailedCountAndDuration()
    {
        Guid runId;
        await using (var seed = NewDb())
        {
            var run = NewRun(EvalRunStatus.Completed, promptKey: null, minutesAgo: 0);
            run.CreatedByUsername = "admin";
            run.StartedAt = run.CreatedAt;
            run.FinishedAt = run.CreatedAt.AddSeconds(90);
            seed.EvalRuns.Add(run);
            runId = run.Id;

            seed.EvalResults.AddRange(
                new EvalResult { EvalRunId = runId, EvalScenarioId = _scenarioA, ScenarioName = "A", Score = 4, IsSuccess = true },
                new EvalResult { EvalRunId = runId, EvalScenarioId = _scenarioB, ScenarioName = "B", IsSuccess = false, ErrorMessage = "lỗi" });
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        var loaded = Assert.Single((await NewSut(db).ExecuteAsync()).Runs);

        Assert.Equal("admin", loaded.CreatedByUsername);
        Assert.Equal(1, loaded.FailedCount);
        Assert.Equal(TimeSpan.FromSeconds(90), loaded.Duration);
    }

    [Fact]
    public async Task Execute_ScenarioCarriesMostRecentScore()
    {
        await using (var seed = NewDb())
        {
            var older = NewRun(EvalRunStatus.Completed, null, minutesAgo: 30);
            var newer = NewRun(EvalRunStatus.Completed, null, minutesAgo: 1);
            seed.EvalRuns.AddRange(older, newer);
            seed.EvalResults.AddRange(
                new EvalResult { EvalRunId = older.Id, EvalScenarioId = _scenarioA, ScenarioName = "A", Score = 2, IsSuccess = true, CreatedAt = older.CreatedAt },
                new EvalResult { EvalRunId = newer.Id, EvalScenarioId = _scenarioA, ScenarioName = "A", Score = 5, IsSuccess = true, CreatedAt = newer.CreatedAt });
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        var scenarios = (await NewSut(db).ExecuteAsync()).Scenarios;

        var measured = Assert.Single(scenarios, s => s.Id == _scenarioA);
        Assert.Equal(5, measured.LastScore); // điểm MỚI NHẤT, không phải điểm đầu tiên
        Assert.NotNull(measured.LastRunAt);

        var neverRun = Assert.Single(scenarios, s => s.Id == _scenarioB);
        Assert.Null(neverRun.LastScore);
        Assert.Null(neverRun.LastRunAt);
    }

    [Fact]
    public async Task Execute_CostEstimate_UsesRealHistoryPerScenario()
    {
        await using (var seed = NewDb())
        {
            var run = NewRun(EvalRunStatus.Completed, null, minutesAgo: 1);
            seed.EvalRuns.Add(run);
            seed.EvalResults.AddRange(
                new EvalResult { EvalRunId = run.Id, EvalScenarioId = _scenarioA, ScenarioName = "A", Score = 4, IsSuccess = true, TargetCost = 0.01m, JudgeCost = 0.01m },
                new EvalResult { EvalRunId = run.Id, EvalScenarioId = _scenarioB, ScenarioName = "B", Score = 4, IsSuccess = true, TargetCost = 0.20m, JudgeCost = 0.05m });
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        var estimates = (await NewSut(db).ExecuteAsync()).CostEstimates;

        var all = Assert.Single(estimates, e => e.PromptKey == null);
        Assert.Equal(2, all.ScenarioCount);            // scenario đã tắt không được tính
        Assert.Equal(0.27m, all.EstimatedCost);        // 0.02 + 0.25
        Assert.True(all.HasHistory);

        // Phỏng vấn mô phỏng đắt hơn hẳn một-lượt — ước lượng phải phản ánh đúng scenario được chọn.
        Assert.Equal(0.02m, Assert.Single(estimates, e => e.PromptKey == "BA/x.md").EstimatedCost);
        Assert.Equal(0.25m, Assert.Single(estimates, e => e.PromptKey == "BA/y.md").EstimatedCost);
    }

    [Fact]
    public async Task Execute_CostEstimate_WithoutHistory_SaysSo()
    {
        await using var db = NewDb();
        var all = Assert.Single((await NewSut(db).ExecuteAsync()).CostEstimates, e => e.PromptKey == null);

        Assert.Equal(2, all.ScenarioCount);
        Assert.Equal(0m, all.EstimatedCost);
        Assert.False(all.HasHistory);
    }

    private EvalRun NewRun(EvalRunStatus status, string? promptKey, int minutesAgo) => new()
    {
        Status = status,
        PromptKey = promptKey,
        TargetModelName = "target",
        JudgeModelName = "judge",
        ScenarioCount = 2,
        CreatedAt = DateTime.UtcNow.AddMinutes(-minutesAgo)
    };

    private static GetEvalPageQuery NewSut(AppDbContext db) =>
        new(db, new PromptFileCatalog(new FakeWebHostEnvironment()));

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "ico-empty-" + Guid.NewGuid().ToString("N"));
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}

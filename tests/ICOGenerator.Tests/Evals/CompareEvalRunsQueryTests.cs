using ICOGenerator.Application.Evals;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Evals;

// So sánh 2 run: khớp scenario theo EvalScenarioId, delta = B - A, và mỗi bên mang NHÃN phiên bản
// prompt đã đo ("v{n}" = bản DB Prompt Studio, "file" = nội dung repo, null = run không có kết quả).
public class CompareEvalRunsQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _scenarioId = Guid.NewGuid();
    private readonly Guid _runAId = Guid.NewGuid();
    private readonly Guid _runBId = Guid.NewGuid();

    public CompareEvalRunsQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.EvalRuns.AddRange(
            new EvalRun { Id = _runAId, TargetModelName = "T", JudgeModelName = "J", AverageScore = 3 },
            new EvalRun { Id = _runBId, TargetModelName = "T", JudgeModelName = "J", AverageScore = 4 });
        db.EvalResults.AddRange(
            // Run A đo nội dung FILE, run B đo bản DB v2 của cùng scenario.
            new EvalResult { EvalRunId = _runAId, EvalScenarioId = _scenarioId, ScenarioName = "S", Score = 3, IsSuccess = true },
            new EvalResult { EvalRunId = _runBId, EvalScenarioId = _scenarioId, ScenarioName = "S", Score = 4, IsSuccess = true, PromptVersionNumber = 2, PromptVersionId = Guid.NewGuid() },
            // Scenario chỉ chạy ở run B — bên A phải là null (không có kết quả).
            new EvalResult { EvalRunId = _runBId, EvalScenarioId = Guid.NewGuid(), ScenarioName = "ChiB", Score = 5, IsSuccess = true });
        db.SaveChanges();
    }

    [Fact]
    public async Task Execute_RowsCarryScoreDelta_AndPromptVersionLabels()
    {
        await using var db = NewDb();
        var vm = await new CompareEvalRunsQuery(db).ExecuteAsync(_runAId, _runBId);

        Assert.NotNull(vm);
        var shared = Assert.Single(vm!.Rows, r => r.ScenarioName == "S");
        Assert.Equal(3, shared.ScoreA);
        Assert.Equal(4, shared.ScoreB);
        Assert.Equal(1, shared.Delta);
        Assert.Equal("file", shared.PromptA);
        Assert.Equal("v2", shared.PromptB);

        var onlyB = Assert.Single(vm.Rows, r => r.ScenarioName == "ChiB");
        Assert.Null(onlyB.ScoreA);
        Assert.Null(onlyB.PromptA);   // run A không có kết quả ⇒ không có nhãn
        Assert.Equal("file", onlyB.PromptB);
        Assert.Null(onlyB.Delta);
    }

    [Fact]
    public async Task Execute_UnknownRun_ReturnsNull()
    {
        await using var db = NewDb();
        Assert.Null(await new CompareEvalRunsQuery(db).ExecuteAsync(_runAId, Guid.NewGuid()));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}

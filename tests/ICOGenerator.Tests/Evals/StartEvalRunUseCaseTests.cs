using ICOGenerator.Application.Evals;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Evals;

// StartEvalRunUseCase chỉ TẠO run Queued (worker chạy nền): phải chốt ScenarioCount + snapshot tên model
// ngay lúc tạo, và chặn sớm các trường hợp vô nghĩa (model tắt/không tồn tại, không có scenario khớp).
public class StartEvalRunUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _targetModelId = Guid.NewGuid();
    private readonly Guid _judgeModelId = Guid.NewGuid();

    public StartEvalRunUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.AddRange(
            new AiModel { Id = _targetModelId, Name = "Target", ModelId = "target" },
            new AiModel { Id = _judgeModelId, Name = "Judge", ModelId = "judge" });
        db.EvalScenarios.AddRange(
            new EvalScenario { Name = "S1", PromptKey = "BA/x.md", UserInput = "in", Criteria = "c" },
            new EvalScenario { Name = "S2", PromptKey = "BA/y.md", UserInput = "in", Criteria = "c" },
            new EvalScenario { Name = "Tắt", PromptKey = "BA/x.md", UserInput = "in", Criteria = "c", IsActive = false });
        db.SaveChanges();
    }

    [Fact]
    public async Task ExecuteAsync_CreatesQueuedRun_WithSnapshotAndCount()
    {
        await using var db = NewDb();
        var result = await new StartEvalRunUseCase(db)
            .ExecuteAsync(_targetModelId, _judgeModelId, promptKey: null, note: "  thử  ", "admin");

        Assert.Equal(StartEvalRunResult.Started, result);

        await using var verify = NewDb();
        var run = await verify.EvalRuns.SingleAsync();
        Assert.Equal(EvalRunStatus.Queued, run.Status);
        Assert.Equal(2, run.ScenarioCount); // chỉ đếm scenario đang bật
        Assert.Equal("Target", run.TargetModelName);
        Assert.Equal("Judge", run.JudgeModelName);
        Assert.Equal("thử", run.Note);
        Assert.Equal("admin", run.CreatedByUsername);
        Assert.Null(run.PromptKey);
    }

    [Fact]
    public async Task ExecuteAsync_PromptKeyFilter_CountsOnlyMatching()
    {
        await using var db = NewDb();
        var result = await new StartEvalRunUseCase(db)
            .ExecuteAsync(_targetModelId, _judgeModelId, "BA/x.md", null, null);

        Assert.Equal(StartEvalRunResult.Started, result);

        await using var verify = NewDb();
        var run = await verify.EvalRuns.SingleAsync();
        Assert.Equal(1, run.ScenarioCount);
        Assert.Equal("BA/x.md", run.PromptKey);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOrInactiveModel_Rejected()
    {
        await using (var db = NewDb())
        {
            Assert.Equal(StartEvalRunResult.TargetModelNotFound,
                await new StartEvalRunUseCase(db).ExecuteAsync(Guid.NewGuid(), _judgeModelId, null, null, null));
            Assert.Equal(StartEvalRunResult.JudgeModelNotFound,
                await new StartEvalRunUseCase(db).ExecuteAsync(_targetModelId, Guid.NewGuid(), null, null, null));
        }

        // Model tắt cũng bị từ chối như không tồn tại.
        await using (var db = NewDb())
        {
            var target = await db.AiModels.FirstAsync(x => x.Id == _targetModelId);
            target.IsActive = false;
            await db.SaveChangesAsync();

            Assert.Equal(StartEvalRunResult.TargetModelNotFound,
                await new StartEvalRunUseCase(db).ExecuteAsync(_targetModelId, _judgeModelId, null, null, null));
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoMatchingActiveScenarios_Rejected()
    {
        await using var db = NewDb();
        var result = await new StartEvalRunUseCase(db)
            .ExecuteAsync(_targetModelId, _judgeModelId, "BA/khong-ton-tai.md", null, null);

        Assert.Equal(StartEvalRunResult.NoActiveScenarios, result);
        Assert.Equal(0, await db.EvalRuns.CountAsync());
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

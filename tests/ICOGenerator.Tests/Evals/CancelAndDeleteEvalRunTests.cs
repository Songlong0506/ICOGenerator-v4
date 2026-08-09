using ICOGenerator.Application.Evals;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Evals;

// Đường lùi của một run: HUỶ (Queued chốt ngay, Running chỉ đặt cờ cho runner tự dừng) và XOÁ (chỉ run
// đã kết thúc, kéo theo kết quả nhờ FK Cascade). Hai việc này giữ cho bảng Runs dùng được về lâu dài —
// và giữ cho một lần bấm nhầm model đắt không phải chạy tới cùng.
public class CancelAndDeleteEvalRunTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public CancelAndDeleteEvalRunTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Cancel_QueuedRun_IsFinishedImmediately()
    {
        var runId = await AddRunAsync(EvalRunStatus.Queued);

        await using var db = NewDb();
        Assert.Equal(CancelEvalRunResult.Cancelled, await new CancelEvalRunUseCase(db).ExecuteAsync(runId));

        await using var verify = NewDb();
        var run = await verify.EvalRuns.SingleAsync();
        Assert.Equal(EvalRunStatus.Cancelled, run.Status);
        Assert.NotNull(run.CancelRequestedAt);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task Cancel_RunningRun_OnlyFlagsIt_LeavingStatusToTheRunner()
    {
        var runId = await AddRunAsync(EvalRunStatus.Running);

        await using var db = NewDb();
        Assert.Equal(CancelEvalRunResult.CancelRequested, await new CancelEvalRunUseCase(db).ExecuteAsync(runId));

        await using var verify = NewDb();
        var run = await verify.EvalRuns.SingleAsync();
        // Không giật trạng thái từ bên ngoài: worker chạy ở DbContext khác, runner mới là nơi chốt.
        Assert.Equal(EvalRunStatus.Running, run.Status);
        Assert.NotNull(run.CancelRequestedAt);
        Assert.Null(run.FinishedAt);
    }

    [Theory]
    [InlineData(EvalRunStatus.Completed)]
    [InlineData(EvalRunStatus.Failed)]
    [InlineData(EvalRunStatus.Cancelled)]
    public async Task Cancel_FinishedRun_IsRejected(EvalRunStatus status)
    {
        var runId = await AddRunAsync(status);

        await using var db = NewDb();
        Assert.Equal(CancelEvalRunResult.AlreadyFinished, await new CancelEvalRunUseCase(db).ExecuteAsync(runId));
    }

    [Fact]
    public async Task Cancel_UnknownRun_ReturnsNotFound()
    {
        await using var db = NewDb();
        Assert.Equal(CancelEvalRunResult.NotFound, await new CancelEvalRunUseCase(db).ExecuteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Delete_FinishedRun_RemovesRunAndItsResults()
    {
        var runId = await AddRunAsync(EvalRunStatus.Completed);
        await using (var seed = NewDb())
        {
            seed.EvalResults.Add(new EvalResult { EvalRunId = runId, EvalScenarioId = Guid.NewGuid(), ScenarioName = "S", Score = 4, IsSuccess = true });
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        Assert.Equal(DeleteEvalRunResult.Deleted, await new DeleteEvalRunUseCase(db).ExecuteAsync(runId));

        await using var verify = NewDb();
        Assert.Equal(0, await verify.EvalRuns.CountAsync());
        Assert.Equal(0, await verify.EvalResults.CountAsync()); // FK Cascade dọn kết quả theo
    }

    [Theory]
    [InlineData(EvalRunStatus.Queued)]
    [InlineData(EvalRunStatus.Running)]
    public async Task Delete_LiveRun_IsRejected(EvalRunStatus status)
    {
        var runId = await AddRunAsync(status);

        await using var db = NewDb();
        Assert.Equal(DeleteEvalRunResult.StillRunning, await new DeleteEvalRunUseCase(db).ExecuteAsync(runId));

        await using var verify = NewDb();
        Assert.Equal(1, await verify.EvalRuns.CountAsync());
    }

    private async Task<Guid> AddRunAsync(EvalRunStatus status)
    {
        await using var db = NewDb();
        var run = new EvalRun
        {
            Status = status,
            TargetModelName = "target",
            JudgeModelName = "judge",
            ScenarioCount = 3
        };
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}

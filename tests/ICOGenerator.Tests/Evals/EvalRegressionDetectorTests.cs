using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Evals;
using ICOGenerator.Services.Notifications;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Evals;

// EvalRegressionDetector: baseline = run Completed gần nhất CÙNG model mục tiêu + CÙNG bộ lọc PromptKey;
// delta tính trên các scenario CHUNG cả hai run đều chấm được; tụt từ ngưỡng trở lên ⇒ IsRegression +
// gọi NotifyEvalRegressionAsync. Detector chỉ ghi lên entity đang track — KHÔNG SaveChanges (caller lưu).
public class EvalRegressionDetectorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _modelId = Guid.NewGuid();
    private readonly Guid _scenarioA = Guid.NewGuid();
    private readonly Guid _scenarioB = Guid.NewGuid();

    public EvalRegressionDetectorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Apply_DropBeyondThreshold_FlagsRegressionAndNotifies()
    {
        var baselineId = SeedCompletedRun(promptKey: null, createdAt: DateTime.UtcNow.AddHours(-2),
            scores: new() { [_scenarioA] = 5, [_scenarioB] = 5 });
        var currentId = SeedCompletedRun(promptKey: null, createdAt: DateTime.UtcNow,
            scores: new() { [_scenarioA] = 4, [_scenarioB] = 4 });

        var recorder = new RecordingNotifications();
        await using (var db = NewDb())
        {
            var run = await db.EvalRuns.FirstAsync(x => x.Id == currentId);
            await NewDetector(db, recorder).ApplyAsync(run);

            Assert.Equal(baselineId, run.BaselineEvalRunId);
            Assert.Equal(-1.0, run.ScoreDelta);
            Assert.True(run.IsRegression);
        }

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(currentId, call.Run.Id);
        Assert.Equal(-1.0, call.Delta);
        Assert.Equal(0.5, call.Threshold); // ngưỡng mặc định từ config
    }

    [Fact]
    public async Task Apply_SmallDropOrImprovement_SetsDeltaWithoutNotifying()
    {
        SeedCompletedRun(promptKey: null, createdAt: DateTime.UtcNow.AddHours(-2),
            scores: new() { [_scenarioA] = 4 });
        var currentId = SeedCompletedRun(promptKey: null, createdAt: DateTime.UtcNow,
            scores: new() { [_scenarioA] = 5 });

        var recorder = new RecordingNotifications();
        await using var db = NewDb();
        var run = await db.EvalRuns.FirstAsync(x => x.Id == currentId);
        await NewDetector(db, recorder).ApplyAsync(run);

        Assert.Equal(1.0, run.ScoreDelta);
        Assert.False(run.IsRegression);
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task Apply_DeltaUsesOnlyCommonScenarios()
    {
        // Baseline có A=5 và B=1; run mới chỉ chấm được A=5 ⇒ so trên MỖI A: delta 0 (không phải so 5 với TB 3).
        SeedCompletedRun(promptKey: null, createdAt: DateTime.UtcNow.AddHours(-2),
            scores: new() { [_scenarioA] = 5, [_scenarioB] = 1 });
        var currentId = SeedCompletedRun(promptKey: null, createdAt: DateTime.UtcNow,
            scores: new() { [_scenarioA] = 5 });

        var recorder = new RecordingNotifications();
        await using var db = NewDb();
        var run = await db.EvalRuns.FirstAsync(x => x.Id == currentId);
        await NewDetector(db, recorder).ApplyAsync(run);

        Assert.Equal(0.0, run.ScoreDelta);
        Assert.False(run.IsRegression);
    }

    [Fact]
    public async Task Apply_IgnoresRunsOfOtherModelOrOtherPromptFilter()
    {
        // Hai run "gần giống" nhưng khác model / khác bộ lọc — không được lấy làm baseline.
        SeedCompletedRun(promptKey: null, createdAt: DateTime.UtcNow.AddHours(-3),
            scores: new() { [_scenarioA] = 5 }, targetModelId: Guid.NewGuid());
        SeedCompletedRun(promptKey: "BA/x.md", createdAt: DateTime.UtcNow.AddHours(-2),
            scores: new() { [_scenarioA] = 5 });
        var currentId = SeedCompletedRun(promptKey: null, createdAt: DateTime.UtcNow,
            scores: new() { [_scenarioA] = 1 });

        var recorder = new RecordingNotifications();
        await using var db = NewDb();
        var run = await db.EvalRuns.FirstAsync(x => x.Id == currentId);
        await NewDetector(db, recorder).ApplyAsync(run);

        Assert.Null(run.BaselineEvalRunId); // không có baseline hợp lệ ⇒ không delta, không cảnh báo
        Assert.Null(run.ScoreDelta);
        Assert.False(run.IsRegression);
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task Apply_ScheduledRun_UsesScheduleThreshold()
    {
        var scheduleId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.EvalSchedules.Add(new EvalSchedule
            {
                Id = scheduleId,
                Name = "Lịch",
                TargetModelId = _modelId,
                TargetModelName = "M",
                JudgeModelId = _modelId,
                JudgeModelName = "M",
                RegressionThreshold = 2.0, // dễ dãi hơn ngưỡng mặc định 0.5
                NextRunAt = DateTime.UtcNow.AddDays(1)
            });
            await db.SaveChangesAsync();
        }

        SeedCompletedRun(promptKey: null, createdAt: DateTime.UtcNow.AddHours(-2),
            scores: new() { [_scenarioA] = 5 });
        var currentId = SeedCompletedRun(promptKey: null, createdAt: DateTime.UtcNow,
            scores: new() { [_scenarioA] = 4 }, scheduleId: scheduleId);

        var recorder = new RecordingNotifications();
        await using var db2 = NewDb();
        var run = await db2.EvalRuns.FirstAsync(x => x.Id == currentId);
        await NewDetector(db2, recorder).ApplyAsync(run);

        Assert.Equal(-1.0, run.ScoreDelta);
        Assert.False(run.IsRegression); // tụt 1.0 < ngưỡng 2.0 của lịch
        Assert.Empty(recorder.Calls);
    }

    // Run Completed + các EvalResult có điểm, seed thẳng vào DB (không đi qua runner).
    private Guid SeedCompletedRun(string? promptKey, DateTime createdAt, Dictionary<Guid, int> scores, Guid? targetModelId = null, Guid? scheduleId = null)
    {
        using var db = NewDb();
        var run = new EvalRun
        {
            Status = EvalRunStatus.Completed,
            PromptKey = promptKey,
            TargetModelId = targetModelId ?? _modelId,
            TargetModelName = "M",
            JudgeModelId = _modelId,
            JudgeModelName = "M",
            ScheduleId = scheduleId,
            ScenarioCount = scores.Count,
            CompletedCount = scores.Count,
            AverageScore = scores.Count == 0 ? null : Math.Round(scores.Values.Average(), 2),
            CreatedAt = createdAt
        };
        db.EvalRuns.Add(run);
        foreach (var (scenarioId, score) in scores)
        {
            db.EvalResults.Add(new EvalResult
            {
                EvalRunId = run.Id,
                EvalScenarioId = scenarioId,
                ScenarioName = "S",
                Score = score,
                IsSuccess = true
            });
        }
        db.SaveChanges();
        return run.Id;
    }

    private static EvalRegressionDetector NewDetector(AppDbContext db, INotificationService notifications) =>
        new(db, notifications,
            new ConfigurationBuilder().Build(), // không cấu hình ⇒ dùng ngưỡng mặc định 0.5
            NullLogger<EvalRegressionDetector>.Instance);

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }

    // Ghi lại các lời gọi NotifyEvalRegressionAsync để assert; các method workflow không dùng trong test này.
    private sealed class RecordingNotifications : INotificationService
    {
        public List<(EvalRun Run, double Delta, double Threshold)> Calls { get; } = new();

        public Task NotifyEvalRegressionAsync(EvalRun run, double delta, double threshold, CancellationToken cancellationToken = default)
        {
            Calls.Add((run, delta, threshold));
            return Task.CompletedTask;
        }

        public Task NotifyGateOpenedAsync(WorkflowRun run, string nextStepTitle, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyRunCompletedAsync(WorkflowRun run, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyRunFailedAsync(WorkflowRun run, string? error, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
